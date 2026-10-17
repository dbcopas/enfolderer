using System.Security.Claims;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Api;

public static class ScanDiagnosticsEndpoints
{
    public static void MapScanDiagnostics(this WebApplication app)
    {
        var routes = app.MapGroup("/diagnostics/scans").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ScanDiagnostics");
            http.Response.Headers.CacheControl = "no-store";
            var owner = JobOwnerFilter.ReadOwner(http.User);
            var scopes = http.User.FindFirstValue("scp")
                ?? http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope");
            if (owner is null
                || !string.Equals(owner.Value.TenantId, config["AzureAd:TenantId"], StringComparison.OrdinalIgnoreCase)
                || !(scopes ?? "").Split(' ').Contains("Scan.Submit")
                || !Ids(config, "Diagnostics:AllowedUserObjectIds").Contains(owner.Value.ObjectId, StringComparer.OrdinalIgnoreCase))
            {
                log.LogWarning("Denied scan diagnostics request by {Caller}.", owner?.ObjectId ?? "(unknown)");
                return Results.Forbid();
            }
            if (http.Request.RouteValues["jobId"] is string jobId)
            {
                var job = await http.RequestServices.GetRequiredService<IJobStore>().GetAsync(jobId, http.RequestAborted);
                if (job is null || !string.Equals(job.OwnerTenantId, owner.Value.TenantId, StringComparison.OrdinalIgnoreCase)
                    || !Ids(config, "MobileWeb:AllowedUserObjectIds").Contains(job.OwnerObjectId, StringComparer.OrdinalIgnoreCase))
                {
                    log.LogWarning("Diagnostics caller {Caller} requested absent or non-pilot job {JobId}.", owner.Value.ObjectId, jobId);
                    return Results.NotFound();
                }
                http.Items["DiagnosticJob"] = job;
            }
            log.LogInformation("Diagnostics read by {Caller}: {Path}", owner.Value.ObjectId, http.Request.Path);
            return await next(context);
        });

        routes.MapGet("/", async (int? hours, int? limit, IConfiguration config, IJobStore jobs, CancellationToken ct) =>
        {
            if (hours is < 1 or > 168 || limit is < 1 or > 50)
                return Results.BadRequest(new { error = "hours must be 1-168 and limit 1-50." });
            var recent = await jobs.FindRecentAsync(config["AzureAd:TenantId"]!,
                Ids(config, "MobileWeb:AllowedUserObjectIds"), DateTimeOffset.UtcNow.AddHours(-(hours ?? 24)), limit ?? 10, ct);
            return Results.Ok(recent.Select(j => new { j.JobId, j.Status, j.CreatedAt, j.UpdatedAt,
                j.OwnerObjectId, j.CardsDetected, j.CardsProcessed, j.Error }));
        });
        routes.MapGet("/{jobId}", (HttpContext http) => Results.Ok(Job(http)));
        routes.MapGet("/{jobId}/original", (HttpContext http, IScanImageStore images, CancellationToken ct) =>
            FileAsync(images, Job(http).BlobPath, ScanBlobPaths.ContentTypeFor(Job(http).BlobPath), ct));
        routes.MapGet("/{jobId}/annotated-image", (HttpContext http, IScanImageStore images, CancellationToken ct) =>
            FileAsync(images, ScanBlobPaths.BuildAnnotatedBlobPath(Job(http).JobId), "image/png", ct));
        routes.MapGet("/{jobId}/crops/{index:int}", (int index, HttpContext http, IScanImageStore images, CancellationToken ct) =>
            index < 0 || index >= Job(http).CardsDetected
                ? Task.FromResult<IResult>(Results.NotFound())
                : FileAsync(images, $"{ScanBlobPaths.CropsContainer}/{ScanBlobPaths.BuildCropBlobName(Job(http).JobId, index)}", "image/png", ct));
        routes.MapGet("/{jobId}/cards.csv", (HttpContext http) => Job(http).Result is { } result
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(MobileWebEndpoints.Csv(result)), "text/csv; charset=utf-8")
            : Results.NotFound());
    }

    private static ScanJobDocument Job(HttpContext http) => (ScanJobDocument)http.Items["DiagnosticJob"]!;

    private static string[] Ids(IConfiguration config, string key) =>
        (config[key] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task<IResult> FileAsync(IScanImageStore images, string path, string contentType, CancellationToken ct) =>
        await images.ExistsAsync(path, ct) ? Results.File(await images.OpenReadAsync(path, ct), contentType) : Results.NotFound();
}

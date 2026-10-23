using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Allowances;

namespace Enfolderer.Ai.Api;

public static class ScanAllowanceEndpoints
{
    public static string OwnerKey(HttpContext http)
    {
        var owner = JobOwnerFilter.ReadOwner(http.User)
            ?? throw new InvalidOperationException("Authenticated owner is required.");
        return ScanAllowanceOwner.Key(owner.TenantId, owner.ObjectId);
    }

    public static IResult Exhausted() => Results.Json(new
    {
        error = "Your scan allowance has no credits available. Ask Douglas for more scans, or sign out to use Request access. Previous results remain available.",
        code = "scan_allowance_exhausted"
    }, statusCode: StatusCodes.Status403Forbidden);

    public static void MapScanAllowances(this WebApplication app)
    {
        var own = app.MapGroup("/allowance").RequireAuthorization();
        own.AddEndpointFilter<JobOwnerFilter>();
        own.MapGet("/", async (HttpContext http, IScanAllowanceStore store, CancellationToken ct) =>
            Results.Ok(View(await store.GetAsync(OwnerKey(http), ct))));

        var admin = app.MapGroup("/admin/allowances").RequireAuthorization();
        admin.AddEndpointFilter<JobOwnerFilter>();
        admin.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var caller = JobOwnerFilter.ReadOwner(http.User);
            var admins = (config["ScanAllowances:AdminUserObjectIds"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (caller is null || !string.Equals(caller.Value.TenantId, config["AzureAd:TenantId"], StringComparison.OrdinalIgnoreCase)
                || !admins.Contains(caller.Value.ObjectId, StringComparer.OrdinalIgnoreCase))
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ScanAllowances")
                    .LogWarning("Denied allowance administration by {Caller}.", caller?.ObjectId);
                return Results.Forbid();
            }
            if (!Guid.TryParse(http.Request.RouteValues["objectId"]?.ToString(), out var target) || target == Guid.Empty)
                return Results.BadRequest(new { error = "A nonempty tenant user object ID is required." });
            var pilot = (config["MobileWeb:AllowedUserObjectIds"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!pilot.Contains(target.ToString("D"), StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Approve the user in the pilot allowlist before granting credits." });
            return await next(context);
        });
        admin.MapGet("/{objectId}", async (string objectId, HttpContext http, IScanAllowanceStore store, CancellationToken ct) =>
        {
            var owner = JobOwnerFilter.ReadOwner(http.User)!.Value;
            return Results.Ok(View(await store.GetAsync(ScanAllowanceOwner.Key(owner.TenantId, objectId), ct)));
        });
        admin.MapPost("/{objectId}", async (string objectId, Adjustment request, HttpContext http,
            IScanAllowanceStore store, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (request.OperationId == Guid.Empty || request.Delta is < -1_000_000 or > 1_000_000
                || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
                return Results.BadRequest(new { error = "An operation GUID, credits between -1000000 and 1000000, and a reason (1-500 characters) are required." });
            var owner = JobOwnerFilter.ReadOwner(http.User)!.Value;
            var key = ScanAllowanceOwner.Key(owner.TenantId, objectId);
            try
            {
                await store.AdjustAsync(key, request.OperationId, request.Delta, request.Unlimited,
                    OwnerKey(http), request.Reason, ct);
            }
            catch (InvalidOperationException ex)
            {
                logs.CreateLogger("ScanAllowances").LogWarning(ex, "Rejected allowance adjustment {OperationId} by {Actor}.",
                    request.OperationId, owner.ObjectId);
                return Results.Conflict(new { error = ex.Message });
            }
            logs.CreateLogger("ScanAllowances").LogInformation(
                "Allowance adjustment {OperationId} by {Actor} for {Target}: delta {Delta}, unlimited {Unlimited}, reason {Reason}.",
                request.OperationId, owner.ObjectId, objectId, request.Delta, request.Unlimited, request.Reason);
            return Results.Ok(View(await store.GetAsync(key, ct)));
        });
    }

    private static object View(ScanAllowance value) => new
    {
        value.OwnerKey,
        value.Granted,
        remaining = value.CreditsRemaining,
        value.Unlimited,
        value.TotalReserved,
        value.UpdatedAt
    };

    public sealed record Adjustment(Guid OperationId, long Delta, bool Unlimited, string Reason);
}

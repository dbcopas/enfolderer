using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.RateLimiting;

namespace Enfolderer.Ai.Api;

public static class AccessRequestEndpoints
{
    public static void AddAccessRequestServices(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("access-requests", limiter =>
            {
                limiter.PermitLimit = 5;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AccessRequests").LogWarning("Access request rate limit reached.");
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many access requests. Please wait a minute and try again." }, ct);
            };
        });
    }

    private static bool IsAdmin(HttpContext http)
    {
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        var owner = JobOwnerFilter.ReadOwner(http.User);
        var scope = http.User.FindFirstValue("scp")
            ?? http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope") ?? "";
        return owner is not null
            && string.Equals(owner.Value.TenantId, config["AzureAd:TenantId"], StringComparison.OrdinalIgnoreCase)
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Scan.Submit", StringComparer.Ordinal)
            && (config["ScanAllowances:AdminUserObjectIds"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(owner.Value.ObjectId, StringComparer.OrdinalIgnoreCase);
    }

    public static void MapAccessRequests(this WebApplication app)
    {
        app.MapPost("/access-requests", async (HttpContext http, IAccessRequestStore store,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("AccessRequests");
            if (!http.Request.HasJsonContentType())
            {
                log.LogWarning("Rejected access request without JSON content type.");
                return Results.BadRequest(new { error = "Send the access request using the form on this page." });
            }
            var buffer = new byte[4097];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await http.Request.Body.ReadAsync(buffer.AsMemory(length), ct);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096)
            {
                log.LogWarning("Rejected oversized access request.");
                return Results.Json(new { error = "Your request is too long. Please shorten it and try again." }, statusCode: 413);
            }
            Submission? request;
            try { request = JsonSerializer.Deserialize<Submission>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException)
            {
                log.LogWarning("Rejected malformed access request JSON.");
                return Results.BadRequest(new { error = "The request could not be read. Please check the form and try again." });
            }
            var name = request?.Name?.Trim() ?? "";
            var email = request?.Email?.Trim() ?? "";
            var note = request?.Note?.Trim() ?? "";
            if (name.Length is < 1 or > 100 || name.Any(char.IsControl)
                || email.Length is < 3 or > 254 || email.Any(char.IsControl)
                || !MailAddress.TryCreate(email, out var address)
                || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)
                || !address.Host.Contains('.') || note.Length > 500
                || note.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            {
                log.LogWarning("Rejected invalid access request fields.");
                return Results.BadRequest(new { error = "Enter your name (up to 100 characters), a valid email address, and an optional note (up to 500 characters)." });
            }
            var id = AccessRequest.EmailId(email);
            await store.AddAsync(new(id, name, email, note, DateTimeOffset.UtcNow), ct);
            log.LogInformation("Access request {RequestId} saved or already pending.", id);
            return Results.Json(new { message = "Your request is saved for Douglas to review. Access is not automatic; please wait for an invitation before signing in." },
                statusCode: StatusCodes.Status202Accepted);
        }).AllowAnonymous().RequireRateLimiting("access-requests");

        app.MapGet("/access-requests/admin", (HttpContext http) => Results.Ok(new { allowed = IsAdmin(http) }))
            .RequireAuthorization();

        var admin = app.MapGroup("/admin/access-requests").RequireAuthorization();
        admin.AddEndpointFilter(async (context, next) =>
        {
            if (!IsAdmin(context.HttpContext))
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AccessRequests").LogWarning("Denied access request inbox administration.");
                return Results.Forbid();
            }
            return await next(context);
        });
        admin.MapGet("/", async (IAccessRequestStore store, CancellationToken ct) =>
            Results.Ok(await store.ReadAsync(ct)));
        admin.MapDelete("/{id}", async (string id, IAccessRequestStore store, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit))
            {
                logs.CreateLogger("AccessRequests").LogWarning("Rejected invalid access request ID.");
                return Results.BadRequest(new { error = "Invalid request ID." });
            }
            await store.DeleteAsync(id, ct);
            logs.CreateLogger("AccessRequests").LogInformation("Removed access request {RequestId}.", id);
            return Results.NoContent();
        });
    }

    public sealed record Submission(string? Name, string? Email, string? Note);
}

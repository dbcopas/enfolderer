using System.Security.Claims;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Api;

/// <summary>Applies creator identity checks to every job route, including uploads and downloads.</summary>
public sealed class JobOwnerFilter : IEndpointFilter
{
    public static (string TenantId, string ObjectId)? ReadOwner(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var tenant = principal.FindFirstValue("tid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        var subject = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        return Guid.TryParse(tenant, out var tenantId) && Guid.TryParse(subject, out var objectId)
            ? (tenantId.ToString("D"), objectId.ToString("D")) : null;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var log = http.RequestServices.GetRequiredService<ILogger<JobOwnerFilter>>();
        if (ReadOwner(http.User) is not { } owner)
        {
            log.LogWarning("Rejected job request without an authenticated tenant and object identity.");
            return Results.Unauthorized();
        }
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        var scopes = http.User.FindFirstValue("scp")
            ?? http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope");
        if (!(scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Scan.Submit", StringComparer.Ordinal))
        {
            log.LogWarning("Rejected scan request without delegated Scan.Submit permission.");
            return Results.Forbid();
        }
        var allowed = (config["MobileWeb:AllowedUserObjectIds"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0 && !allowed.Contains(owner.ObjectId, StringComparer.OrdinalIgnoreCase))
        {
            log.LogWarning("Rejected scan request by a user outside the private pilot.");
            return Results.Forbid();
        }

        if (http.Request.RouteValues["jobId"] is string jobId)
        {
            var store = http.RequestServices.GetRequiredService<IJobStore>();
            var job = await store.GetAsync(jobId, http.RequestAborted);
            if (job is null
                || !string.Equals(job.OwnerTenantId, owner.TenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(job.OwnerObjectId, owner.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Rejected access to absent, ownerless or another user's job {JobId}.", jobId);
                return Results.NotFound();
            }
        }
        return await next(context);
    }
}

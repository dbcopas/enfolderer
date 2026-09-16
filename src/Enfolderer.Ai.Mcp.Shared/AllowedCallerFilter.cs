using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Mcp;

/// <summary>
/// Rejects callers that are not on the server's allow-list, which is the executable form of the
/// <c>allowed_callers</c> key in the MCP server's YAML definition.
///
/// This is deliberately a second check on top of authentication. Authentication only establishes
/// that the caller holds a valid token from the tenant; every project identity in the subscription
/// can obtain one. The boundary the demo is about is which of those identities a given server
/// answers, so an empty allow-list denies everyone rather than admitting everyone.
/// </summary>
public sealed class AllowedCallerFilter : IEndpointFilter
{
    // Object id of the calling principal. The v2.0 endpoint issues "oid"; ASP.NET maps it to the
    // long-form claim type, so accept both rather than depending on claim mapping being off.
    private static readonly string[] ObjectIdClaims =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];

    private readonly IReadOnlySet<string> _allowedCallerObjectIds;
    private readonly string _serverName;

    public AllowedCallerFilter(IReadOnlySet<string> allowedCallerObjectIds, string serverName)
    {
        _allowedCallerObjectIds = allowedCallerObjectIds;
        _serverName = serverName;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var callerObjectId = ObjectIdClaims
            .Select(claim => http.User.FindFirstValue(claim))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (callerObjectId is null || !_allowedCallerObjectIds.Contains(callerObjectId))
        {
            var logger = http.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            logger?.CreateLogger<AllowedCallerFilter>().LogWarning(
                "Rejected a call to {ServerName} from principal {CallerObjectId}, which is not an allowed caller.",
                _serverName, callerObjectId ?? "(no object id claim)");

            // Deliberately terse: the caller learns it was refused, not who is allowed.
            return Results.Json(
                new { error = $"Caller is not permitted to use {_serverName}." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}

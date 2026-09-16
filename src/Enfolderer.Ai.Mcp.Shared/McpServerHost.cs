using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Enfolderer.Ai.Mcp;

/// <summary>
/// Shared hosting for the MCP servers. Each one is an ASP.NET application exposing MCP over
/// streamable HTTP at <c>/mcp</c>, because the Foundry agent data plane will only accept an
/// <c>https://</c> URL for a tool server: a stdio process has no address it can be given.
///
/// Every server is protected the same way, by an allow-list of caller object ids. That list is the
/// executable form of the <c>allowed_callers</c> key in the agent YAML, and it is what makes
/// scenario 2 of the Foundry boundary demo fail closed: Team A's project identity is not on Team
/// B's catalogue servers, so the call is rejected even though the URL is reachable.
/// </summary>
public static class McpServerHost
{
    /// <summary>
    /// Builds a web application hosting <paramref name="serverName"/> over HTTP.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the host.</param>
    /// <param name="serverName">MCP server name reported in the handshake, e.g. <c>mcp-imaging</c>.</param>
    /// <param name="configureServices">
    /// Registers the server's own dependencies. Tools are discovered from the calling assembly.
    /// </param>
    public static WebApplication Create(
        string[] args,
        string serverName,
        Action<IServiceCollection, IConfiguration> configureServices)
    {
        var builder = WebApplication.CreateBuilder(args);

        configureServices(builder.Services, builder.Configuration);

        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = serverName, Version = "1.0.0" })
            .WithHttpTransport()
            .WithToolsFromAssembly(System.Reflection.Assembly.GetCallingAssembly());

        // Callers are Foundry projects presenting their own managed identity, so the token is an
        // app token: authorise on the object id of the calling principal rather than on a scope.
        var tenantId = builder.Configuration["McpServer:TenantId"];
        var audience = builder.Configuration["McpServer:Audience"];
        var allowedCallers = ReadAllowedCallers(builder.Configuration);
        var authConfigured = !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(audience);

        if (authConfigured)
        {
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidAudiences = [audience, $"api://{audience}"],
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true
                    };
                });
        }
        builder.Services.AddAuthorization();

        var app = builder.Build();

        if (authConfigured)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }
        else
        {
            app.Logger.LogWarning(
                "McpServer:TenantId or McpServer:Audience is not configured; {ServerName} is running " +
                "unauthenticated. This is only acceptable for local development: a public MCP endpoint " +
                "would let any caller bypass the project boundaries the demo is about.",
                serverName);
        }

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", server = serverName }));

        var mcp = app.MapMcp("/mcp");
        if (authConfigured)
        {
            mcp.RequireAuthorization();
            // Authentication proves who the caller is; this proves they are one of the callers this
            // server exists to serve. Without it any identity in the tenant would be accepted.
            mcp.AddEndpointFilter(new AllowedCallerFilter(allowedCallers, serverName));
        }

        return app;
    }

    /// <summary>
    /// Object ids of the principals permitted to call this server, from
    /// <c>McpServer:AllowedCallerObjectIds</c> as either a bound array or a comma-separated string.
    /// </summary>
    private static IReadOnlySet<string> ReadAllowedCallers(IConfiguration configuration)
    {
        var section = configuration.GetSection("McpServer:AllowedCallerObjectIds");
        var values = section.Get<string[]>();

        if (values is null && section.Value is { Length: > 0 } raw)
            values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new HashSet<string>(values ?? [], StringComparer.OrdinalIgnoreCase);
    }
}

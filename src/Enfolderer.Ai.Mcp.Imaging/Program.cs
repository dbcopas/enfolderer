using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Mcp;
using Microsoft.Extensions.DependencyInjection;

// Storage access only: this server deliberately does not register a job store client, so Team A
// cannot reach job state even if the tool code were changed.
var app = McpServerHost.Create(args, "mcp-imaging", (services, configuration) =>
    services.AddScanPlatform(configuration, registerUploadUrlIssuer: false));

await app.RunAsync();

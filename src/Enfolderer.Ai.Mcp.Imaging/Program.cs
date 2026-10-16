using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Mcp;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--check-imaging-runtime"])
{
    Console.WriteLine(CardDetector.CheckNativeRuntime());
    return;
}

// Storage access only: this server deliberately does not register a job store client, so Team A
// cannot reach job state even if the tool code were changed.
var app = McpServerHost.Create(args, "mcp-imaging", (services, configuration) =>
    services.AddScanPlatform(configuration));

await app.RunAsync();

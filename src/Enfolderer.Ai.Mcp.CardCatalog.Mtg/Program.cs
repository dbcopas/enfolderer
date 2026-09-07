using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddHttpClient<ScryfallCatalogue>(CatalogueHttp.ApplyDefaults)
    .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(100)));

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "mcp-cardcatalog-mtg", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

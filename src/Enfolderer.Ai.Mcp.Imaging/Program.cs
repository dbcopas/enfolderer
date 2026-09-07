using Enfolderer.Ai.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Storage access only: this server deliberately does not register a job store client, so Team A
// cannot reach job state even if the tool code were changed.
builder.Services.AddScanPlatform(builder.Configuration, registerUploadUrlIssuer: false);

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "mcp-imaging", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Pokemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddHttpClient<PokemonTcgCatalogue>(client =>
    {
        CatalogueHttp.ApplyDefaults(client);
        // pokemontcg.io works without a key at a lower rate limit; supply one via configuration.
        var apiKey = Environment.GetEnvironmentVariable("POKEMONTCG_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    })
    .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(100)));

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "mcp-cardcatalog-pokemon", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Microsoft.Extensions.DependencyInjection;
using Enfolderer.Ai.Infrastructure.Catalogue;

var app = McpServerHost.Create(args, "mcp-cardcatalog-mtg", (services, _) =>
    services
        .AddHttpClient<ScryfallCatalogue>(CatalogueHttp.ApplyDefaults)
        .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(100)))
        .Services.AddHttpClient<ScryfallPriceClient>(CatalogueHttp.ApplyDefaults)
        .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(100))));

await app.RunAsync();

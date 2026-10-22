using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Microsoft.Extensions.DependencyInjection;
using Enfolderer.Ai.Infrastructure.Catalogue;

var app = McpServerHost.Create(args, "mcp-cardcatalog-mtg", (services, _) =>
    services
        .AddHttpClient("scryfall", CatalogueHttp.ApplyDefaults)
        // Catalogue and worker replicas can share an outbound IP: budget two requests/second each.
        .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(500)))
        .AddTypedClient<ScryfallCatalogue>()
        .AddTypedClient<ScryfallPriceClient>());

await app.RunAsync();

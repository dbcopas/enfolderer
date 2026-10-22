using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Microsoft.Extensions.DependencyInjection;
using Enfolderer.Ai.Infrastructure.Catalogue;

var app = McpServerHost.Create(args, "mcp-cardcatalog-mtg", (services, _) =>
    services
        .AddHttpClient("scryfall", CatalogueHttp.ApplyDefaults)
        // Four requests/second per replica keeps the two-replica limit below Scryfall's ceiling.
        .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(250)))
        .AddTypedClient<ScryfallCatalogue>()
        .AddTypedClient<ScryfallPriceClient>());

await app.RunAsync();

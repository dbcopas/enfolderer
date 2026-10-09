using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Pokemon;
using Microsoft.Extensions.DependencyInjection;

var app = McpServerHost.Create(args, "mcp-cardcatalog-pokemon", (services, configuration) =>
    services
        .AddHttpClient<PokemonTcgCatalogue>(client =>
        {
            CatalogueHttp.ApplyDefaults(client);
            // pokemontcg.io works without a key at a lower rate limit; supply one via configuration.
            var apiKey = configuration["POKEMONTCG_API_KEY"];
            if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        })
        .AddHttpMessageHandler(() => new PoliteRateLimitHandler(TimeSpan.FromMilliseconds(100))));

await app.RunAsync();

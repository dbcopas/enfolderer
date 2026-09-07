using System.Text.Json;

namespace Enfolderer.Ai.Mcp.CardCatalog.Pokemon;

/// <summary>A Pokémon printing as returned by the catalogue.</summary>
public sealed record CataloguePrinting(string Set, string CollectorNumber, string Name, string Language);

/// <summary>
/// pokemontcg.io backed catalogue lookups. The API key, when configured, is read from the
/// <c>POKEMONTCG_API_KEY</c> environment variable so it is supplied by the container/app-service
/// configuration rather than committed anywhere.
/// </summary>
public sealed class PokemonTcgCatalogue
{
    public const string ApiRoot = "https://api.pokemontcg.io/v2";

    private readonly HttpClient _http;

    public PokemonTcgCatalogue(HttpClient http) => _http = http;

    /// <summary>
    /// Builds a Lucene-style query for the pokemontcg.io <c>/cards</c> endpoint. Values are quoted
    /// and embedded quotes are stripped so a card name cannot alter the query structure.
    /// </summary>
    internal static string BuildQuery(params (string Field, string Value)[] terms)
    {
        var clauses = terms
            .Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .Select(t => $"{t.Field}:\"{Escape(t.Value)}\"");
        return string.Join(" ", clauses);
    }

    private static string Escape(string value) =>
        new(value.Where(c => c is not ('"' or '\\') && !char.IsControl(c)).ToArray());

    public Task<CataloguePrinting?> LookupBySetAndNumberAsync(string setCode, string collectorNumber, CancellationToken ct) =>
        QueryAsync(BuildQuery(("set.id", setCode), ("number", collectorNumber)), ct);

    public Task<CataloguePrinting?> LookupByNameAndSetAsync(string name, string setCode, CancellationToken ct) =>
        QueryAsync(BuildQuery(("name", name), ("set.id", setCode)), ct);

    public Task<CataloguePrinting?> LookupByNameAsync(string name, CancellationToken ct) =>
        QueryAsync(BuildQuery(("name", name)), ct);

    private async Task<CataloguePrinting?> QueryAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var url = $"{ApiRoot}/cards?q={Uri.EscapeDataString(query)}&pageSize=1";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseFirst(document.RootElement);
    }

    /// <summary>Projects the first card of a pokemontcg.io response. Internal for testing.</summary>
    internal static CataloguePrinting? ParseFirst(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        foreach (var card in data.EnumerateArray())
        {
            string Read(JsonElement element, string property) =>
                element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var name = Read(card, "name");
            if (string.IsNullOrEmpty(name)) continue;

            var setId = card.TryGetProperty("set", out var set) ? Read(set, "id") : string.Empty;
            return new CataloguePrinting(setId, Read(card, "number"), name, "en");
        }

        return null;
    }
}

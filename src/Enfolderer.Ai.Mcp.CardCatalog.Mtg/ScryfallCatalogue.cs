using System.Text.Json;

namespace Enfolderer.Ai.Mcp.CardCatalog.Mtg;

/// <summary>A printing as returned by the catalogue.</summary>
public sealed record CataloguePrinting(string Set, string CollectorNumber, string Name, string Language);

/// <summary>
/// Scryfall-backed catalogue lookups, ported from the desktop app's <c>BinderScanService</c> so the
/// identification agent — not the client — owns card resolution.
/// </summary>
public sealed class ScryfallCatalogue
{
    public const string ApiRoot = "https://api.scryfall.com";

    private readonly HttpClient _http;

    public ScryfallCatalogue(HttpClient http) => _http = http;

    /// <summary>
    /// Builds the exact-printing URL. Collector numbers are normalized by stripping internal
    /// whitespace per segment, so "5 J-b" becomes "5J-b" — behaviour carried over from the desktop
    /// app's <c>ScryfallUrlHelper</c>.
    /// </summary>
    public static string BuildCardApiUrl(string setCode, string number)
    {
        if (string.IsNullOrWhiteSpace(setCode)) return string.Empty;
        setCode = setCode.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(number)) return $"{ApiRoot}/cards/{Uri.EscapeDataString(setCode)}";

        static string NormalizeSegment(string s) =>
            string.IsNullOrEmpty(s) ? s : new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

        var segments = number
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Select(Uri.EscapeDataString);

        return $"{ApiRoot}/cards/{Uri.EscapeDataString(setCode)}/" + string.Join('/', segments);
    }

    public Task<CataloguePrinting?> LookupBySetAndNumberAsync(string setCode, string collectorNumber, CancellationToken ct) =>
        GetPrintingAsync(BuildCardApiUrl(setCode, collectorNumber), ct);

    public Task<CataloguePrinting?> LookupByNameAndSetAsync(string name, string setCode, CancellationToken ct) =>
        GetPrintingAsync(
            $"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}&set={Uri.EscapeDataString(setCode)}",
            ct);

    public Task<CataloguePrinting?> LookupByNameAsync(string name, CancellationToken ct) =>
        GetPrintingAsync($"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}", ct);

    private async Task<CataloguePrinting?> GetPrintingAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(document.RootElement);
    }

    /// <summary>Projects a Scryfall card object onto the catalogue shape. Internal for testing.</summary>
    internal static CataloguePrinting? Parse(JsonElement root)
    {
        string Read(string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        var name = Read("name");
        if (string.IsNullOrEmpty(name)) return null;

        var language = Read("lang");
        return new CataloguePrinting(
            Read("set"),
            Read("collector_number"),
            name,
            string.IsNullOrEmpty(language) ? "en" : language);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Enfolderer.Ai.Mcp.CardCatalog.Mtg;

/// <summary>A printing as returned by the catalogue.</summary>
/// <param name="PrintedName">
/// The name in the language the card was printed in, where that differs from the English name. A
/// Japanese card carries no English text at all, so this is the only name a reader can check.
/// </param>
public sealed record CataloguePrinting(
    string Set,
    string CollectorNumber,
    string Name,
    string Language,
    string PrintedName = "");

/// <summary>What the catalogue made of a card someone read off a photograph.</summary>
/// <param name="Printing">The printing the catalogue settled on, or null when it settled on none.</param>
/// <param name="Resolution">
/// How it was settled: <c>confirmed</c> when the printed number produced the card whose name was
/// read, <c>corrected</c> when it did not and the name was used instead, <c>assumed</c> when only
/// the number could be looked up and there was no name to check it against, and <c>unresolved</c>
/// when nothing matched.
/// </param>
/// <param name="ReadCollectorNumber">The number as it was read, kept so a correction is visible.</param>
public sealed record ResolvedPrinting(
    CataloguePrinting? Printing,
    string Resolution,
    string? ReadCollectorNumber);

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
    public static string BuildCardApiUrl(string setCode, string number, string? language = null)
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

        var url = $"{ApiRoot}/cards/{Uri.EscapeDataString(setCode)}/" + string.Join('/', segments);

        // Scryfall takes the language as a further path segment. English is the default and asking
        // for it explicitly is a 404 on sets that were never printed in another language, so it is
        // only appended when the card is in one.
        language = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(language) || language == "en"
            ? url
            : $"{url}/{Uri.EscapeDataString(language)}";
    }

    public async Task<CataloguePrinting?> LookupBySetAndNumberAsync(
        string setCode, string collectorNumber, string? language, CancellationToken ct)
    {
        var localised = await GetPrintingAsync(BuildCardApiUrl(setCode, collectorNumber, language), ct);
        if (localised is not null) return localised;

        // A card photographed in Japanese may still be catalogued only in English — promos and
        // Secret Lairs especially — so the number is worth asking about without the language.
        return string.IsNullOrWhiteSpace(language) || language.Trim().ToLowerInvariant() == "en"
            ? null
            : await GetPrintingAsync(BuildCardApiUrl(setCode, collectorNumber), ct);
    }

    public Task<CataloguePrinting?> LookupBySetAndNumberAsync(string setCode, string collectorNumber, CancellationToken ct) =>
        LookupBySetAndNumberAsync(setCode, collectorNumber, null, ct);

    public Task<CataloguePrinting?> LookupByNameAndSetAsync(string name, string setCode, CancellationToken ct) =>
        GetPrintingAsync(
            $"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}&set={Uri.EscapeDataString(setCode)}",
            ct);

    public Task<CataloguePrinting?> LookupByNameAsync(string name, CancellationToken ct) =>
        GetPrintingAsync($"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}", ct);

    /// <summary>
    /// Finds a printing from the name as it appears on the card, in whatever language that is.
    /// <para>
    /// The fuzzy endpoint only knows English names, so a Japanese card read correctly still finds
    /// nothing there. Search does know printed names, given the language to look in.
    /// </para>
    /// </summary>
    public async Task<CataloguePrinting?> SearchByPrintedNameAsync(
        string name, string? setCode, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var query = new StringBuilder();
        query.Append('"').Append(name.Replace("\"", string.Empty)).Append('"');
        if (!string.IsNullOrWhiteSpace(setCode)) query.Append(" set:").Append(setCode.Trim());
        if (!string.IsNullOrWhiteSpace(language)) query.Append(" lang:").Append(language.Trim());

        var url = $"{ApiRoot}/cards/search?unique=prints&order=released&q={Uri.EscapeDataString(query.ToString())}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        return data.EnumerateArray().Select(Parse).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// Settles what printing a card is, given what was read off it, and says how it was settled.
    /// <para>
    /// This exists because a collector number is the least legible thing on a card — six point type
    /// in the bottom corner, behind a sleeve, at whatever angle the photograph was taken — while
    /// the name is the most legible thing on it. Looking the number up on its own always succeeds:
    /// a set has a card at almost every number, so a digit misread returns a real printing of the
    /// wrong card, which is why a wrong number survives a lookup that was supposed to check it.
    /// What catches it is comparing the name that came back with the name that was read, and when
    /// they disagree, believing the name.
    /// </para>
    /// </summary>
    public async Task<ResolvedPrinting> ResolveAsync(
        string? name, string setCode, string? collectorNumber, string? language, CancellationToken ct)
    {
        var byNumber = string.IsNullOrWhiteSpace(collectorNumber)
            ? null
            : await LookupBySetAndNumberAsync(setCode, collectorNumber, language, ct);

        if (byNumber is not null && string.IsNullOrWhiteSpace(name))
            return new ResolvedPrinting(byNumber, "assumed", collectorNumber);

        if (byNumber is not null && NameMatches(name!, byNumber))
            return new ResolvedPrinting(byNumber, "confirmed", collectorNumber);

        if (string.IsNullOrWhiteSpace(name))
            return new ResolvedPrinting(null, "unresolved", collectorNumber);

        var byName = await LookupByNameAndSetAsync(name, setCode, ct)
                  ?? await SearchByPrintedNameAsync(name, setCode, language, ct)
                  ?? await SearchByPrintedNameAsync(name, setCode, null, ct);

        if (byName is not null && !string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(byName.CollectorNumber))
        {
            // Prefer the card in the language it was printed in: it is the same printing, but its
            // own name is what a reader can check the answer against.
            byName = await LookupBySetAndNumberAsync(byName.Set, byName.CollectorNumber, language, ct) ?? byName;
        }

        if (byName is null) return new ResolvedPrinting(null, "unresolved", collectorNumber);

        var agrees = byNumber is not null
            && string.Equals(byNumber.CollectorNumber, byName.CollectorNumber, StringComparison.OrdinalIgnoreCase);

        return new ResolvedPrinting(byName, agrees ? "confirmed" : "corrected", collectorNumber);
    }

    /// <summary>
    /// Whether a name read off a card is the name of a printing. Both are reduced to their letters
    /// and digits first, because a reader transcribes an apostrophe, an accent or a comma however
    /// they like, and a double-faced card is named for its front face alone on the card itself.
    /// </summary>
    internal static bool NameMatches(string read, CataloguePrinting printing)
    {
        var candidate = Simplify(read);
        if (candidate.Length == 0) return false;

        foreach (var known in new[] { printing.Name, printing.PrintedName })
        {
            if (string.IsNullOrEmpty(known)) continue;
            foreach (var face in known.Split("//", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var simplified = Simplify(face);
                if (simplified.Length == 0) continue;
                if (simplified == candidate) return true;
                if (candidate.Length >= 6 && (simplified.StartsWith(candidate, StringComparison.Ordinal)
                                           || candidate.StartsWith(simplified, StringComparison.Ordinal))) return true;
            }
        }

        return false;
    }

    private static string Simplify(string value)
    {
        var normalised = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        foreach (var character in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

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
            string.IsNullOrEmpty(language) ? "en" : language,
            Read("printed_name"));
    }
}

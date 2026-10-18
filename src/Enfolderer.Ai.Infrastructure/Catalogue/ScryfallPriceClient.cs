using System.Globalization;
using System.Net;
using System.Text.Json;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Catalogue;

/// <summary>Exact-printing prices. Each missing foreign-language finish can fall back to English.</summary>
public sealed class ScryfallPriceClient(HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset _nextRequest;

    public async Task<CardPrices> GetAsync(string set, string number, string? language, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        language = language?.Trim().ToLowerInvariant() switch
        {
            "jp" or "jpn" => "ja",
            null or "" => "en",
            var value => value
        };
        var local = await ReadAsync(set, number, language, ct);
        (decimal? Nonfoil, decimal? Foil, string? Layout, string? Name, IReadOnlyList<int> GathererIds, Guid? OracleId) english = (null, null, null, null, [], null);
        string? error = null;
        if (language != "en" && (local.Nonfoil is null || local.Foil is null))
        {
            try { english = await ReadAsync(set, number, "en", ct); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                error = $"English fallback lookup failed: {ex.Message}";
            }
        }
        return new CardPrices
        {
            Layout = local.Layout ?? english.Layout,
            OracleId = local.OracleId ?? english.OracleId,
            CardName = local.Name ?? english.Name,
            GathererIds = local.GathererIds.Concat(english.GathererIds).Distinct().ToArray(),
            Nonfoil = local.Nonfoil ?? english.Nonfoil,
            Foil = local.Foil ?? english.Foil,
            NonfoilLanguage = local.Nonfoil.HasValue ? language : english.Nonfoil.HasValue ? "en" : null,
            FoilLanguage = local.Foil.HasValue ? language : english.Foil.HasValue ? "en" : null,
            RetrievedAt = DateTimeOffset.UtcNow,
            Error = error
        };
    }

    public async Task<IReadOnlyList<CardPrintingReference>> GetRelatedPrintingsAsync(Guid oracleId, CancellationToken ct)
    {
        if (oracleId == Guid.Empty) throw new ArgumentException("A canonical card ID is required.", nameof(oracleId));
        var url = $"https://api.scryfall.com/cards/search?unique=prints&q={Uri.EscapeDataString($"oracleid:{oracleId:D} game:paper")}";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CardPrintingReference>();
        while (true)
        {
            if (!visited.Add(url) || visited.Count > 20)
                throw new InvalidDataException("Ownership printing search exceeded its page limit or repeated a page.");
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _nextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(120);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Enfolderer-ScanPricing/1.0");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("has_more", out var more)
                    || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Ownership printing search returned an invalid list.");
                foreach (var item in data.EnumerateArray())
                {
                    var id = ReadOptionalText(item, "oracle_id");
                    if (!Guid.TryParse(id, out var foundId) || foundId != oracleId)
                        throw new InvalidDataException("Ownership printing search returned a different canonical card.");
                    if (item.TryGetProperty("digital", out var digital) && digital.ValueKind == JsonValueKind.True) continue;
                    var set = ReadOptionalText(item, "set");
                    var number = ReadOptionalText(item, "collector_number");
                    if (string.IsNullOrWhiteSpace(set) || string.IsNullOrWhiteSpace(number))
                        throw new InvalidDataException("Ownership printing search returned no printing key.");
                    result.Add(new(set, number, ReadGathererIds(item)));
                }
                if (!more.GetBoolean()) return result;
                if (!root.TryGetProperty("next_page", out var next) || next.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var page)
                    || page.Scheme != Uri.UriSchemeHttps || page.Host != "api.scryfall.com"
                    || page.AbsolutePath != "/cards/search" || !page.IsDefaultPort
                    || !string.IsNullOrEmpty(page.UserInfo))
                    throw new InvalidDataException("Ownership printing search returned an invalid next page.");
                url = page.AbsoluteUri;
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<(decimal? Nonfoil, decimal? Foil, string? Layout, string? Name, IReadOnlyList<int> GathererIds, Guid? OracleId)> ReadAsync(
        string set, string number, string language, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(120);
            var url = $"https://api.scryfall.com/cards/{Uri.EscapeDataString(set.Trim().ToLowerInvariant())}"
                + $"/{Uri.EscapeDataString(number.Trim())}"
                + (language == "en" ? "" : $"/{Uri.EscapeDataString(language)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Enfolderer-ScanPricing/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null, null, null, [], null);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("set", out var foundSet) || foundSet.ValueKind != JsonValueKind.String
                || !string.Equals(foundSet.GetString(), set.Trim(), StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("collector_number", out var foundNumber) || foundNumber.ValueKind != JsonValueKind.String
                || !string.Equals(foundNumber.GetString(), number.Trim(), StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("lang", out var foundLanguage) || foundLanguage.ValueKind != JsonValueKind.String
                || foundLanguage.GetString() != language)
                throw new InvalidDataException("Scryfall price response does not match the requested printing/language.");
            if (!root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Scryfall price response has no prices object.");
            var oracle = ReadOptionalText(root, "oracle_id");
            Guid? oracleId = null;
            if (oracle is not null)
                oracleId = Guid.TryParse(oracle, out var parsed) && parsed != Guid.Empty ? parsed
                    : throw new InvalidDataException("Scryfall returned an invalid canonical card ID.");
            return (ReadPrice(prices, "eur"), ReadPrice(prices, "eur_foil"),
                ReadOptionalText(root, "layout"), ReadOptionalText(root, "name"), ReadGathererIds(root), oracleId);
        }
        finally { _gate.Release(); }
    }

    private static decimal? ReadPrice(JsonElement prices, string key)
    {
        if (!prices.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price)
            && price >= 0) return price;
        throw new InvalidDataException($"Scryfall returned an invalid {key} price.");
    }

    private static IReadOnlyList<int> ReadGathererIds(JsonElement root)
    {
        if (!root.TryGetProperty("multiverse_ids", out var ids)) return [];
        if (ids.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Scryfall returned invalid multiverse_ids.");
        var result = new List<int>();
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value <= 0)
                throw new InvalidDataException("Scryfall returned an invalid Gatherer identifier.");
            result.Add(value);
        }
        return result;
    }

    private static string? ReadOptionalText(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        throw new InvalidDataException($"Scryfall returned an invalid {key}.");
    }
}

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
        (decimal? Nonfoil, decimal? Foil, string? Layout, string? Name) english = (null, null, null, null);
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
            CardName = local.Name ?? english.Name,
            Nonfoil = local.Nonfoil ?? english.Nonfoil,
            Foil = local.Foil ?? english.Foil,
            NonfoilLanguage = local.Nonfoil.HasValue ? language : english.Nonfoil.HasValue ? "en" : null,
            FoilLanguage = local.Foil.HasValue ? language : english.Foil.HasValue ? "en" : null,
            RetrievedAt = DateTimeOffset.UtcNow,
            Error = error
        };
    }

    private async Task<(decimal? Nonfoil, decimal? Foil, string? Layout, string? Name)> ReadAsync(
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
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null, null, null);
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
            return (ReadPrice(prices, "eur"), ReadPrice(prices, "eur_foil"),
                ReadOptionalText(root, "layout"), ReadOptionalText(root, "name"));
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

    private static string? ReadOptionalText(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        throw new InvalidDataException($"Scryfall returned an invalid {key}.");
    }
}

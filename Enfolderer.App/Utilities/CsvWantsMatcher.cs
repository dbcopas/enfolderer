using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Importing;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Utilities;

public static class CsvWantsMatcher
{
    public record MatchResult(int CollectionCount, int WantsCount, int MatchCount, string OutputPath);

    public static async Task<MatchResult> MatchAsync(
        string collectionCsvPath,
        string wantsCsvPath,
        string? outputPath = null,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        outputPath ??= Path.Combine(Path.GetDirectoryName(collectionCsvPath)!, "matches.csv");

        // Parse collection CSV (semicolon-delimited, no header): set;number;foil status;language;name
        var collectionLines = File.ReadAllLines(collectionCsvPath);
        var collectionEntries = new List<(string Set, string Number, string Foil, string RawLine)>();

        foreach (var line in collectionLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';');
            if (parts.Length < 5) continue;

            var set = parts[0].Trim().ToLowerInvariant();
            var number = parts[1].Trim();
            var foilRaw = parts[2].Trim().ToLowerInvariant();
            var foil = string.IsNullOrEmpty(foilRaw) || foilRaw == "nonfoil" ? "nonfoil" : "foil";

            collectionEntries.Add((set, number, foil, line));
        }

        // Parse Moxfield wants CSV (comma-delimited with quoted fields, has header)
        var wantsLines = File.ReadAllLines(wantsCsvPath);
        var wantedCards = new HashSet<(string Set, string Number, string Foil)>();

        for (int i = 1; i < wantsLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(wantsLines[i])) continue;
            var fields = ParseCsvLine(wantsLines[i]);
            if (fields.Count < 11) continue;

            var set = fields[3].ToLowerInvariant();
            var collectorNumber = fields[9];
            var foilRaw = fields[6].Trim().ToLowerInvariant();
            var foil = string.IsNullOrEmpty(foilRaw) ? "nonfoil" : "foil";

            wantedCards.Add((set, collectorNumber, foil));
        }

        // Find matches
        var matches = new List<(string Set, string Number, string Foil, string RawLine)>();
        foreach (var entry in collectionEntries)
        {
            if (wantedCards.Contains((entry.Set, entry.Number, entry.Foil)))
                matches.Add(entry);
        }

        // Fetch prices from Scryfall
        using var http = BinderViewModelHttpFactory.Create();
        var outputLines = new List<string>();

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (set, number, foil, rawLine) = matches[i];
            progressCallback?.Invoke(i + 1, matches.Count);

            var price = await FetchPriceAsync(http, set, number, foil == "foil", ct);
            var priceStr = price.HasValue
                ? price.Value.Price.ToString("0.00", CultureInfo.InvariantCulture)
                : "";
            var currencyStr = price.HasValue ? price.Value.Currency : "";
            outputLines.Add($"{rawLine};{priceStr};{currencyStr}");
        }

        File.WriteAllLines(outputPath, outputLines);
        return new MatchResult(collectionEntries.Count, wantedCards.Count, matches.Count, outputPath);
    }

    private static async Task<(decimal Price, string Currency)?> FetchPriceAsync(
        HttpClient http, string setCode, string number, bool isFoil, CancellationToken ct)
    {
        var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            await ApiRateLimiter.WaitAsync();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object)
                return null;

            // Prefer Cardmarket EU prices matching foil status, then fall back to US prices
            string[] keys = isFoil
                ? ["eur_foil", "usd_foil", "eur", "usd"]
                : ["eur", "eur_foil", "usd", "usd_foil"];
            string[] currencies = isFoil
                ? ["EUR", "USD", "EUR", "USD"]
                : ["EUR", "EUR", "USD", "USD"];

            for (int i = 0; i < keys.Length; i++)
            {
                if (prices.TryGetProperty(keys[i], out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && decimal.TryParse(prop.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
                {
                    return (val, currencies[i]);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* price fetch failure is non-fatal */ }

        return null;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}

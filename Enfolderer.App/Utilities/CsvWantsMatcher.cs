using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Imaging;
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

        // Parse collection CSV (Moxfield export format: comma-delimited with quoted fields, has header)
        var collectionLines = File.ReadAllLines(collectionCsvPath);
        var collectionEntries = new List<(string Set, string Number, string Foil, string RawLine)>();

        for (int ci = 1; ci < collectionLines.Length; ci++)
        {
            if (string.IsNullOrWhiteSpace(collectionLines[ci])) continue;
            var cFields = ParseCsvLine(collectionLines[ci]);
            if (cFields.Count < 11) continue;

            var set = cFields[3].ToLowerInvariant();
            var number = cFields[9];
            var foilRaw = cFields[6].Trim().ToLowerInvariant();
            var foil = string.IsNullOrEmpty(foilRaw) ? "nonfoil" : "foil";
            var name = cFields[2];
            var lang = cFields[5];
            var rawLine = $"{set};{number};{(foil == "nonfoil" ? "" : foil)};{lang};{name}";

            collectionEntries.Add((set, number, foil, rawLine));
        }

        // Parse wants CSV — detect format from header row
        var wantsLines = File.ReadAllLines(wantsCsvPath);
        var wantedCards = new HashSet<(string Set, string Number, string Foil)>();

        if (wantsLines.Length > 0)
        {
            var wantsDelimiter = DetectDelimiter(wantsLines[0]);
            var headerFields = ParseDelimitedLine(wantsLines[0], wantsDelimiter);
            var colMap = DetectColumns(headerFields);

            for (int i = 1; i < wantsLines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(wantsLines[i])) continue;
                var fields = ParseDelimitedLine(wantsLines[i], wantsDelimiter);
                if (fields.Count <= colMap.SetCol || fields.Count <= colMap.NumberCol) continue;

                var set = fields[colMap.SetCol].Trim().TrimEnd('+').ToLowerInvariant();
                var collectorNumber = fields[colMap.NumberCol].Trim();
                var foil = "nonfoil";
                if (colMap.FoilCol >= 0 && colMap.FoilCol < fields.Count)
                {
                    var foilRaw = fields[colMap.FoilCol].Trim().ToLowerInvariant();
                    foil = string.IsNullOrEmpty(foilRaw) ? "nonfoil" : "foil";
                }

                wantedCards.Add((set, collectorNumber, foil));
            }
        }

        // Find matches
        var matches = new List<(string Set, string Number, string Foil, string RawLine)>();
        foreach (var entry in collectionEntries)
        {
            if (wantedCards.Contains((entry.Set, entry.Number, entry.Foil)))
                matches.Add(entry);
        }

        // Fetch prices — check local CardPriceStore cache first, only hit Scryfall for misses
        var maxAge = TimeSpan.FromDays(7);
        var now = DateTime.UtcNow;
        using var http = BinderViewModelHttpFactory.Create();
        var outputLines = new List<string>();
        int fetchedFromApi = 0;

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (set, number, foil, rawLine) = matches[i];
            progressCallback?.Invoke(i + 1, matches.Count);

            // Try local price cache first
            (decimal Price, string Currency)? price = null;
            var cached = CardPriceStore.GetWithTimestamp(set, number);
            if (cached.HasValue && (now - cached.Value.FetchedUtc) < maxAge)
            {
                price = (cached.Value.Price, cached.Value.Currency);
            }
            else
            {
                price = await FetchPriceAsync(http, set, number, foil == "foil", ct);
                fetchedFromApi++;
                if (price.HasValue)
                    CardPriceStore.Set(set, number, price.Value.Price, currency: price.Value.Currency);
            }

            var priceStr = price.HasValue
                ? price.Value.Price.ToString("0.00", CultureInfo.InvariantCulture)
                : "";
            var currencyStr = price.HasValue ? price.Value.Currency : "";
            outputLines.Add($"{rawLine};{priceStr};{currencyStr}");
        }

        if (fetchedFromApi > 0)
            CardPriceStore.SaveToDisk();

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

    private record ColumnMap(int SetCol, int NumberCol, int FoilCol);

    private static ColumnMap DetectColumns(List<string> headerFields)
    {
        int setCol = -1, numberCol = -1, foilCol = -1;

        for (int i = 0; i < headerFields.Count; i++)
        {
            var h = headerFields[i].Trim().ToLowerInvariant();
            if (setCol < 0 && (h == "edition" || h == "set" || h == "set code" || h == "setcode"))
                setCol = i;
            else if (numberCol < 0 && (h == "collector number" || h == "collectornumber" || h == "number" || h == "card number" || h == "no" || h == "no."))
                numberCol = i;
            else if (foilCol < 0 && (h == "foil" || h == "printing" || h == "finish"))
                foilCol = i;
        }

        // Fall back to Moxfield positions if headers not recognized
        if (setCol < 0) setCol = 3;
        if (numberCol < 0) numberCol = 9;
        if (foilCol < 0) foilCol = 6;

        return new ColumnMap(setCol, numberCol, foilCol);
    }

    private static char DetectDelimiter(string headerLine)
    {
        int commas = 0, semicolons = 0;
        foreach (var c in headerLine)
        {
            if (c == ',') commas++;
            else if (c == ';') semicolons++;
        }
        return semicolons > commas ? ';' : ',';
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        if (delimiter == ',')
            return ParseCsvLine(line);

        // Simple split for non-comma delimiters (semicolons don't typically use quoted fields)
        var parts = line.Split(delimiter);
        var fields = new List<string>(parts.Length);
        foreach (var p in parts)
            fields.Add(p.Trim('"', ' '));
        return fields;
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

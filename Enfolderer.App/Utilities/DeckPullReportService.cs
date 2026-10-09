using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Imaging;
using Enfolderer.App.Importing;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Parses a Goldfish-style deck list (qty name per line) and cross-references the local
/// collection databases to produce a pull report showing which editions to pull owned copies
/// from, and pricing the cheapest available printing for any missing cards.
/// </summary>
public static class DeckPullReportService
{
    public sealed record DeckEntry(int Qty, string Name);

    public sealed record PullReportResult(
        List<PullLine> Pulls,
        List<MissingLine> Missing,
        string OutputPath);

    public sealed record PullLine(
        string Name,
        int Needed,
        string Edition,
        string CollectorNumber,
        int PullQty);

    public sealed record MissingLine(
        string Name,
        int Needed,
        int Have,
        decimal? CheapestPrice,
        string? CheapestEdition,
        string? CheapestCollectorNumber,
        string? Currency);

    /// <summary>
    /// Parse a Goldfish-format deck file. Lines are "qty name"; blank lines separate sections.
    /// </summary>
    public static List<DeckEntry> ParseDeckFile(string path)
    {
        var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("//") || line.StartsWith("#")) continue;

            int spaceIdx = line.IndexOf(' ');
            if (spaceIdx <= 0) continue;
            if (!int.TryParse(line.AsSpan(0, spaceIdx), out int qty) || qty <= 0) continue;
            string name = line.Substring(spaceIdx + 1).Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (entries.ContainsKey(name))
                entries[name] += qty;
            else
                entries[name] = qty;
        }
        return entries.Select(kv => new DeckEntry(kv.Value, kv.Key)).ToList();
    }

    /// <summary>
    /// Generate a pull report for the given deck entries against the local collection.
    /// All owned copies are available to pull (no binder reserve).
    /// Missing cards get priced via the cached CardPriceStore and Scryfall API fallback.
    /// </summary>
    public static async Task<PullReportResult> GenerateReportAsync(
        List<DeckEntry> deck,
        string outputPath,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string mainDbPath = Path.Combine(exeDir, "mainDb.db");
        if (!File.Exists(mainDbPath))
            throw new FileNotFoundException("mainDb.db not found", mainDbPath);

        // 1. Load all cards from mainDb: name -> list of printings
        var cardsByName = new Dictionary<string, List<PrintingRow>>(StringComparer.OrdinalIgnoreCase);
        using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, name, edition, collectorNumberValue, MtgsId FROM Cards";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                string name = r.GetString(1);
                long id = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                string edition = r.IsDBNull(2) ? string.Empty : (r.GetString(2) ?? string.Empty);
                string colNum = r.IsDBNull(3) ? string.Empty : (r.GetString(3) ?? string.Empty);
                int? mtgsId = r.IsDBNull(4) ? null : r.GetInt32(4);

                if (!cardsByName.TryGetValue(name, out var list))
                {
                    list = new List<PrintingRow>();
                    cardsByName[name] = list;
                }
                list.Add(new PrintingRow(id, edition, colNum, mtgsId));
            }
        }

        // Front-face index for double-faced card matching
        var frontFaceIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullName in cardsByName.Keys)
        {
            string front = FrontFace(fullName);
            if (!string.Equals(front, fullName, StringComparison.OrdinalIgnoreCase))
            {
                if (!frontFaceIndex.TryGetValue(front, out var names))
                {
                    names = new List<string>();
                    frontFaceIndex[front] = names;
                }
                names.Add(fullName);
            }
        }

        // 2. Load collection quantities directly: MtgsId -> Qty from CollectionCards
        //    CardCollectionData.Quantities is keyed by (set,collector) mapped through Cards.id,
        //    but CollectionCards.CardId == Cards.MtgsId (not Cards.id), so the mapping is
        //    incomplete. Read CollectionCards directly and key by CardId (== MtgsId).
        //    The bin/Debug copy may be stale (symlink + PreserveNewest issue), so also
        //    try the repo root as a fallback.
        string collectionPath = FindCollectionPath(exeDir);
        var qtyByMtgs = new Dictionary<int, int>();
        if (File.Exists(collectionPath))
        {
            using var conCol = new SqliteConnection($"Data Source={collectionPath};Mode=ReadOnly");
            conCol.Open();
            using var cmdCol = conCol.CreateCommand();
            cmdCol.CommandText = "SELECT CardId, Qty FROM CollectionCards WHERE Qty > 0";
            using var rCol = cmdCol.ExecuteReader();
            while (rCol.Read())
            {
                if (rCol.IsDBNull(0) || rCol.IsDBNull(1)) continue;
                qtyByMtgs[rCol.GetInt32(0)] = rCol.GetInt32(1);
            }
        }

        // Basic land names to skip
        var basicLands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Plains", "Island", "Swamp", "Mountain", "Forest",
          "Snow-Covered Plains", "Snow-Covered Island", "Snow-Covered Swamp",
          "Snow-Covered Mountain", "Snow-Covered Forest",
          "Wastes" };

        // 3. For each deck entry, match owned copies and identify shortfall
        var pulls = new List<PullLine>();
        var missingEntries = new List<(DeckEntry Entry, int Needed, int Have, List<PrintingRow> AllPrintings)>();

        foreach (var entry in deck.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Skip basic lands
            if (basicLands.Contains(entry.Name)) continue;

            var printings = ResolvePrintings(entry.Name, cardsByName, frontFaceIndex);

            if (printings.Count == 0)
            {
                missingEntries.Add((entry, entry.Qty, 0, new List<PrintingRow>()));
                continue;
            }

            // Look up owned quantity per printing via MtgsId -> CollectionCards.CardId
            var sources = new List<(string Edition, string CollectorNumber, int OwnedQty)>();
            int totalOwned = 0;
            var seenMtgs = new HashSet<int>();

            foreach (var p in printings)
            {
                if (!p.MtgsId.HasValue || p.MtgsId.Value <= 0) continue;
                if (!seenMtgs.Add(p.MtgsId.Value)) continue;

                if (qtyByMtgs.TryGetValue(p.MtgsId.Value, out int ownedQty) && ownedQty > 0)
                {
                    totalOwned += ownedQty;
                    sources.Add((p.Edition, p.CollectorNumber, ownedQty));
                }
            }

            int stillNeeded = entry.Qty;

            // Greedily pull from editions with most copies first
            foreach (var src in sources.OrderByDescending(s => s.OwnedQty))
            {
                if (stillNeeded <= 0) break;
                int pullQty = Math.Min(stillNeeded, src.OwnedQty);
                pulls.Add(new PullLine(entry.Name, entry.Qty, src.Edition, src.CollectorNumber, pullQty));
                stillNeeded -= pullQty;
            }

            if (stillNeeded > 0)
                missingEntries.Add((entry, stillNeeded, totalOwned, printings));
        }

        // 4. Price missing cards — use CardPriceStore cache, fall back to Scryfall API
        CardPriceStore.LoadFromDisk();
        var maxAge = TimeSpan.FromDays(7);
        var now = DateTime.UtcNow;
        using var http = BinderViewModelHttpFactory.Create();
        int fetchedFromApi = 0;

        var missing = new List<MissingLine>();
        int progressDone = 0;
        int progressTotal = missingEntries.Sum(m => m.AllPrintings.Count > 0 ? m.AllPrintings.Count : 1);

        foreach (var (entry, needed, have, allPrintings) in missingEntries)
        {
            ct.ThrowIfCancellationRequested();

            if (allPrintings.Count == 0)
            {
                // Card not in DB at all — no price available
                missing.Add(new MissingLine(entry.Name, needed, have, null, null, null, null));
                progressDone++;
                progressCallback?.Invoke(progressDone, progressTotal);
                continue;
            }

            // Find cheapest printing across all known printings
            decimal? cheapest = null;
            string? cheapEdition = null;
            string? cheapCollector = null;
            string? cheapCurrency = null;

            foreach (var p in allPrintings)
            {
                ct.ThrowIfCancellationRequested();
                progressDone++;
                progressCallback?.Invoke(progressDone, progressTotal);

                if (string.IsNullOrEmpty(p.Edition)) continue;

                // Try cache first
                var cached = CardPriceStore.GetWithTimestamp(p.Edition, p.CollectorNumber);
                decimal? price = null;
                string? currency = null;

                if (cached.HasValue && (now - cached.Value.FetchedUtc) < maxAge)
                {
                    price = cached.Value.Price;
                    currency = cached.Value.Currency;
                }
                else
                {
                    // Fetch from Scryfall
                    var fetched = await FetchPriceAsync(http, p.Edition, p.CollectorNumber, ct);
                    if (fetched.HasValue)
                    {
                        price = fetched.Value.Price;
                        currency = fetched.Value.Currency;
                        CardPriceStore.Set(p.Edition, p.CollectorNumber, price.Value, currency: currency);
                        fetchedFromApi++;
                    }
                }

                if (price.HasValue && (cheapest == null || price.Value < cheapest.Value))
                {
                    cheapest = price.Value;
                    cheapEdition = p.Edition;
                    cheapCollector = p.CollectorNumber;
                    cheapCurrency = currency;
                }
            }

            missing.Add(new MissingLine(entry.Name, needed, have, cheapest, cheapEdition, cheapCollector, cheapCurrency));
        }

        if (fetchedFromApi > 0)
            CardPriceStore.SaveToDisk();

        // 5. Write report
        var sb = new StringBuilder();
        sb.AppendLine("=== DECK PULL REPORT ===");
        sb.AppendLine();

        if (pulls.Count > 0)
        {
            sb.AppendLine("--- CARDS TO PULL (by edition) ---");
            sb.AppendLine($"{"Card",-40} {"Edition",-8} {"Collector#",-12} {"Pull",5}");
            sb.AppendLine(new string('-', 68));
            foreach (var p in pulls.OrderBy(p => p.Edition, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{p.Name,-40} {p.Edition,-8} {p.CollectorNumber,-12} {p.PullQty,5}");
            }
            sb.AppendLine();
        }

        if (missing.Count > 0)
        {
            sb.AppendLine("--- MISSING CARDS (need to acquire) ---");
            sb.AppendLine($"{"Card",-40} {"Need",5} {"Have",5} {"Price",10} {"Cheapest Edition",-20}");
            sb.AppendLine(new string('-', 85));
            decimal totalMissingCost = 0;
            foreach (var m in missing.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                string priceStr;
                if (m.CheapestPrice.HasValue)
                {
                    string sym = string.Equals(m.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "\u20ac";
                    decimal lineCost = m.CheapestPrice.Value * m.Needed;
                    totalMissingCost += lineCost;
                    priceStr = $"{sym}{lineCost:0.00}";
                }
                else
                {
                    priceStr = "N/A";
                }
                string edInfo = m.CheapestEdition != null
                    ? $"{m.CheapestEdition} #{m.CheapestCollectorNumber}"
                    : "unknown";
                sb.AppendLine($"{m.Name,-40} {m.Needed,5} {m.Have,5} {priceStr,10} {edInfo,-20}");
            }
            sb.AppendLine();
            string totalSym = missing.Any(m => string.Equals(m.Currency, "USD", StringComparison.OrdinalIgnoreCase)) ? "$" : "\u20ac";
            sb.AppendLine($"Estimated total cost for missing cards: {totalSym}{totalMissingCost:0.00}");
            sb.AppendLine();
        }

        sb.AppendLine($"Total pull lines: {pulls.Count}  |  Total missing: {missing.Count}");

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);

        return new PullReportResult(pulls, missing, outputPath);
    }

    private static async Task<(decimal Price, string Currency)?> FetchPriceAsync(
        HttpClient http, string setCode, string number, CancellationToken ct)
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

            string[] keys = ["eur", "eur_foil", "usd", "usd_foil"];
            string[] currencies = ["EUR", "EUR", "USD", "USD"];
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

    private sealed record PrintingRow(long Id, string Edition, string CollectorNumber, int? MtgsId);

    private static List<PrintingRow> ResolvePrintings(
        string deckName,
        Dictionary<string, List<PrintingRow>> cardsByName,
        Dictionary<string, List<string>> frontFaceIndex)
    {
        // Direct match
        if (cardsByName.TryGetValue(deckName, out var direct))
            return direct;

        // Try front-face match (deck lists typically use only the front face name)
        if (frontFaceIndex.TryGetValue(deckName, out var fullNames))
        {
            var result = new List<PrintingRow>();
            foreach (var fn in fullNames)
            {
                if (cardsByName.TryGetValue(fn, out var entries))
                    result.AddRange(entries);
            }
            if (result.Count > 0) return result;
        }

        return new List<PrintingRow>();
    }

    private static string FrontFace(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        // Handle " // " separator for double-faced cards
        int idx = name.IndexOf(" // ", StringComparison.Ordinal);
        if (idx > 0) return name.Substring(0, idx).Trim();
        // Also handle bare "/" without spaces
        int slashIdx = name.IndexOf('/');
        if (slashIdx > 0) return name.Substring(0, slashIdx).Trim();
        return name;
    }

    /// <summary>
    /// Find the best mtgstudio.collection path. The bin/Debug copy may be stale when the
    /// repo root file is a symlink (PreserveNewest doesn't re-copy if the symlink timestamp
    /// hasn't changed). Walk up from exeDir looking for Enfolderer.sln to find the repo root
    /// and prefer that copy if it exists and is larger.
    /// </summary>
    private static string FindCollectionPath(string exeDir)
    {
        const string fileName = "mtgstudio.collection";
        string exeCopy = Path.Combine(exeDir, fileName);

        // Walk up from exe dir looking for repo root (contains .sln)
        var dir = new DirectoryInfo(exeDir);
        while (dir != null)
        {
            if (Directory.GetFiles(dir.FullName, "*.sln").Length > 0)
            {
                string repoRootCopy = Path.Combine(dir.FullName, fileName);
                if (File.Exists(repoRootCopy))
                {
                    // Prefer the repo root if it's larger (the exe copy may be a stale shallow copy)
                    long repoSize = new FileInfo(repoRootCopy).Length;
                    long exeSize = File.Exists(exeCopy) ? new FileInfo(exeCopy).Length : 0;
                    if (repoSize > exeSize)
                        return repoRootCopy;
                }
                break;
            }
            dir = dir.Parent;
        }

        return exeCopy;
    }
}

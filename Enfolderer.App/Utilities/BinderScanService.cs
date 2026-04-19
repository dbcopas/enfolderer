using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Enfolderer.App.Importing;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Utilities;

public static class BinderScanService
{
    public record ScannedCard(string Set, string Number, string Name);
    public record ScanResult(int ImagesProcessed, int CardsFound, int LookupFailures, string OutputPath);

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"];
    private static readonly string[] TokenScopes = ["https://cognitiveservices.azure.com/.default"];

    // Matches "SET . EN" or "SET · EN" pattern at bottom of MTG cards
    private static readonly Regex SetLangPattern = new(
        @"\b([A-Z][A-Z0-9]{1,4})\s*[.·~]\s*EN\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches collector number pattern: "NNN/NNN" (e.g. "167/271")
    private static readonly Regex CollectorSlashPattern = new(
        @"\b(\d{1,4})\s*/\s*\d{1,4}\b",
        RegexOptions.Compiled);

    // Standalone set code (2-5 uppercase alphanumeric)
    private static readonly Regex SetCodePattern = new(
        @"\b([A-Z][A-Z0-9]{1,4})\b",
        RegexOptions.Compiled);

    public static async Task<ScanResult> ScanFolderAsync(
        string folderPath,
        string endpoint,
        string tenantId,
        string clientId,
        string clientSecret,
        string? outputPath = null,
        Action<string>? statusCallback = null,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        outputPath ??= Path.Combine(folderPath, "scanned_cards.csv");
        var debugLogPath = Path.ChangeExtension(outputPath, "_scan_debug.txt");
        var dbg = new StringBuilder();
        dbg.AppendLine($"=== Binder Scan Debug Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        dbg.AppendLine($"Folder: {folderPath}");
        dbg.AppendLine();

        var imageFiles = Directory.GetFiles(folderPath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
            throw new InvalidOperationException("No image files found in the selected folder.");

        dbg.AppendLine($"Images found: {imageFiles.Count}");
        dbg.AppendLine();

        using var http = BinderViewModelHttpFactory.Create();
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var allCards = new List<ScannedCard>();
        int lookupFailures = 0;

        for (int i = 0; i < imageFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = imageFiles[i];
            statusCallback?.Invoke($"OCR: {Path.GetFileName(file)} ({i + 1}/{imageFiles.Count})...");
            progressCallback?.Invoke(i + 1, imageFiles.Count);

            dbg.AppendLine($"--- IMAGE: {Path.GetFileName(file)} ---");
            var candidates = await ExtractCardCandidatesAsync(http, file, endpoint, credential, dbg, ct);
            statusCallback?.Invoke($"Found {candidates.Count} card candidates in {Path.GetFileName(file)}");
            dbg.AppendLine($"  Candidates extracted: {candidates.Count}");

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                ScannedCard? card = null;
                dbg.AppendLine($"  CANDIDATE: set={candidate.SetCode ?? "(null)"} num={candidate.CollectorNumber ?? "(null)"} name={candidate.Name ?? "(null)"}");

                // 1. Exact lookup by set + collector number
                if (!string.IsNullOrEmpty(candidate.SetCode) && !string.IsNullOrEmpty(candidate.CollectorNumber))
                {
                    statusCallback?.Invoke($"Looking up: {candidate.SetCode}/{candidate.CollectorNumber}");
                    card = await LookupBySetNumberAsync(http, candidate.SetCode, candidate.CollectorNumber, dbg, ct);
                }

                // 2. Name + set lookup (right printing via set, right card via name)
                if (card == null && !string.IsNullOrEmpty(candidate.Name) && !string.IsNullOrEmpty(candidate.SetCode))
                {
                    statusCallback?.Invoke($"Name+set lookup: {candidate.Name} [{candidate.SetCode}]");
                    card = await LookupByNameAndSetAsync(http, candidate.Name, candidate.SetCode, dbg, ct);
                }

                // 3. Fuzzy name search — try primary name
                if (card == null && !string.IsNullOrEmpty(candidate.Name))
                {
                    statusCallback?.Invoke($"Fuzzy lookup: {candidate.Name}");
                    card = await LookupByNameAsync(http, candidate.Name, dbg, ct);
                }

                // 4. Try alternate name (e.g. reskin/real name pair on crossover cards)
                if (card == null && !string.IsNullOrEmpty(candidate.AlternateName))
                {
                    dbg.AppendLine($"    Trying alternate name: {candidate.AlternateName}");
                    statusCallback?.Invoke($"Fuzzy lookup (alt): {candidate.AlternateName}");
                    card = await LookupByNameAsync(http, candidate.AlternateName, dbg, ct);
                }

                if (card != null)
                {
                    allCards.Add(card);
                    dbg.AppendLine($"    => MATCHED: {card.Set}/{card.Number} — {card.Name}");
                }
                else
                {
                    lookupFailures++;
                    dbg.AppendLine($"    => FAILED");
                }
            }
            dbg.AppendLine();
        }

        var lines = allCards.Select(c => $"{c.Set};{c.Number};;en;{c.Name}");
        File.WriteAllLines(outputPath, lines);

        dbg.AppendLine($"=== SUMMARY: {allCards.Count} matched, {lookupFailures} failed ===");
        File.WriteAllText(debugLogPath, dbg.ToString());

        return new ScanResult(imageFiles.Count, allCards.Count, lookupFailures, outputPath);
    }

    private record CardCandidate(string? SetCode, string? CollectorNumber, string? Name, string? AlternateName = null);

    /// <summary>
    /// Sends an image to Azure Vision 4.0 OCR, extracts all text lines with positions,
    /// then finds card collector info (number + set code) and card names.
    /// Works with any layout — binder pages, cards on a table, etc.
    /// </summary>
    private static async Task<List<CardCandidate>> ExtractCardCandidatesAsync(
        HttpClient http, string imagePath, string endpoint, TokenCredential credential, StringBuilder dbg, CancellationToken ct)
    {
        endpoint = endpoint.TrimEnd('/');
        var url = $"{endpoint}/computervision/imageanalysis:analyze?api-version=2024-02-01&features=read";

        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var tokenResult = await credential.GetTokenAsync(new TokenRequestContext(TokenScopes), ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);
        request.Content = new ByteArrayContent(imageBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Azure Vision error {(int)resp.StatusCode}: {body}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract all text lines with bounding box info
        var textLines = new List<OcrLine>();

        if (root.TryGetProperty("readResult", out var readResult)
            && readResult.TryGetProperty("blocks", out var blocks)
            && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var line in lines.EnumerateArray())
                {
                    var text = line.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (line.TryGetProperty("boundingPolygon", out var poly) && poly.ValueKind == JsonValueKind.Array)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        double sumX = 0, sumY = 0;
                        int count = 0;
                        foreach (var pt in poly.EnumerateArray())
                        {
                            if (pt.TryGetProperty("x", out var xProp) && pt.TryGetProperty("y", out var yProp))
                            {
                                var x = xProp.GetDouble();
                                var y = yProp.GetDouble();
                                sumX += x; sumY += y; count++;
                                if (x < minX) minX = x; if (x > maxX) maxX = x;
                                if (y < minY) minY = y; if (y > maxY) maxY = y;
                            }
                        }
                        if (count > 0)
                            textLines.Add(new OcrLine(text, sumX / count, sumY / count, minX, minY, maxX, maxY));
                    }
                }
            }
        }

        if (textLines.Count == 0)
        {
            dbg.AppendLine("  (no OCR text detected)");
            return [];
        }

        // Log all OCR lines for debugging
        dbg.AppendLine($"  OCR lines ({textLines.Count}):");
        foreach (var tl in textLines)
            dbg.AppendLine($"    [{tl.MinX:F0},{tl.MinY:F0}]-[{tl.MaxX:F0},{tl.MaxY:F0}] \"{tl.Text}\"");
        dbg.AppendLine();

        // Step 1: Find type lines (contain type keywords AND have >100px gap above = art gap)
        var allSorted = textLines.OrderBy(l => l.CenterY).ToList();
        var typeLineInfos = new List<(OcrLine Line, int GlobalIndex)>();

        for (int i = 0; i < allSorted.Count; i++)
        {
            if (!ContainsTypeKeyword(allSorted[i].Text)) continue;
            if (i == 0) continue;
            double gapAbove = allSorted[i].MinY - allSorted[i - 1].MaxY;
            if (gapAbove > 100)
            {
                typeLineInfos.Add((allSorted[i], i));
                dbg.AppendLine($"  TYPE LINE: \"{allSorted[i].Text}\" y={allSorted[i].CenterY:F0} cx={allSorted[i].CenterX:F0} (gap above: {gapAbove:F0}px)");
            }
        }

        if (typeLineInfos.Count == 0)
        {
            dbg.AppendLine("  No type lines with art gap found");
            return [];
        }

        // Step 2: Cluster type lines by CenterX to find columns
        var typeCentersX = typeLineInfos.Select(t => t.Line.CenterX).OrderBy(v => v).ToList();
        var columnBands = FindGapClusters(typeCentersX, minGap: 80);
        var columnCenters = columnBands.Select(b => (b.Low + b.High) / 2).ToList();
        dbg.AppendLine($"  Columns detected: {columnCenters.Count}");

        // Step 3: Assign ALL lines to nearest column
        var columns = Enumerable.Range(0, columnCenters.Count).Select(_ => new List<OcrLine>()).ToList();
        foreach (var line in textLines)
        {
            int bestCol = 0;
            double bestDist = double.MaxValue;
            for (int c = 0; c < columnCenters.Count; c++)
            {
                var dist = Math.Abs(line.CenterX - columnCenters[c]);
                if (dist < bestDist) { bestDist = dist; bestCol = c; }
            }
            columns[bestCol].Add(line);
        }

        // Step 4: Within each column, find cards using validated type lines
        var candidates = new List<CardCandidate>();

        for (int colIdx = 0; colIdx < columns.Count; colIdx++)
        {
            var col = columns[colIdx].OrderBy(l => l.CenterY).ToList();
            dbg.AppendLine($"  Column {colIdx + 1}: {col.Count} lines");

            // Find validated type lines in this column
            var colTypeLines = new List<int>(); // indices into col
            for (int i = 1; i < col.Count; i++)
            {
                if (!ContainsTypeKeyword(col[i].Text)) continue;
                double gap = col[i].MinY - col[i - 1].MaxY;
                if (gap > 100)
                    colTypeLines.Add(i);
            }

            for (int t = 0; t < colTypeLines.Count; t++)
            {
                int typeIdx = colTypeLines[t];
                var typeLine = col[typeIdx];
                dbg.AppendLine($"    TYPE [{typeIdx}]: \"{typeLine.Text}\"");

                // Find name lines: above the art gap, stop at previous card's body text
                int stopAt = (t > 0) ? colTypeLines[t - 1] : -1;
                var nameLines = new List<OcrLine>();

                for (int j = typeIdx - 1; j > stopAt && j >= 0; j--)
                {
                    double gapBelow = (j == typeIdx - 1)
                        ? typeLine.MinY - col[j].MaxY
                        : col[j + 1].MinY - col[j].MaxY;

                    if (gapBelow > 100)
                    {
                        // Art gap found — collect name lines from here upward
                        // until we hit another large gap (previous card's credit line)
                        for (int k = j; k > stopAt && k >= 0; k--)
                        {
                            nameLines.Insert(0, col[k]);
                            if (k > 0 && col[k].MinY - col[k - 1].MaxY > 80)
                                break;
                        }
                        break;
                    }
                }

                // Filter junk from name lines (standalone numbers, P/T, very short)
                nameLines = nameLines.Where(l =>
                {
                    var txt = l.Text.Trim();
                    if (txt.Length < 2) return false;
                    if (Regex.IsMatch(txt, @"^\d{1,4}$")) return false; // standalone number like "400"
                    if (Regex.IsMatch(txt, @"^[\d\s/+\-]+$")) return false; // P/T like "3/2"
                    return true;
                }).ToList();

                // Primary name: topmost name line (reskin/IP name for crossover cards)
                // — unique to the set, better for Scryfall lookup to find correct printing
                // Alternate name: bottommost (real MTG name)
                string? primaryName = nameLines.Count > 0 ? CleanCardName(nameLines[0].Text) : null;
                string? alternateName = nameLines.Count > 1 ? CleanCardName(nameLines[^1].Text) : null;

                // If names look the same after cleaning, no alternate
                if (primaryName != null && alternateName != null
                    && string.Equals(primaryName, alternateName, StringComparison.OrdinalIgnoreCase))
                    alternateName = null;

                dbg.AppendLine($"      Names: primary=\"{primaryName}\", alt=\"{alternateName}\"" +
                    $" (from {nameLines.Count} name lines)");

                // Find set code from bottom of card region
                int nextCardNameStart = (t + 1 < colTypeLines.Count)
                    ? FindNameStartAboveTypeIdx(col, colTypeLines[t + 1])
                    : col.Count;

                string? setCode = null;
                for (int j = nextCardNameStart - 1; j > typeIdx; j--)
                {
                    // Try "SET . EN" pattern
                    var setMatch = SetLangPattern.Match(col[j].Text);
                    if (setMatch.Success)
                    {
                        setCode = setMatch.Groups[1].Value.ToLowerInvariant();
                        dbg.AppendLine($"      Set from SET.EN: {setCode} (line: \"{col[j].Text}\")");
                        break;
                    }
                }

                dbg.AppendLine($"      Result: name={primaryName}, alt={alternateName}, set={setCode ?? "(none)"}");

                if (!string.IsNullOrEmpty(primaryName))
                    candidates.Add(new CardCandidate(setCode, null, primaryName, alternateName));
            }
        }

        if (candidates.Count == 0)
            dbg.AppendLine("  NO CANDIDATES FOUND in this image");

        return candidates;
    }

    private record OcrLine(string Text, double CenterX, double CenterY,
        double MinX, double MinY, double MaxX, double MaxY);

    private static bool ContainsTypeKeyword(string text) =>
        text.Contains("Creature", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Instant", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Sorcery", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Enchantment", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Artifact", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Planeswalker", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Vehicle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strip trailing price tags, trailing garbage, and extra whitespace from card names.
    /// </summary>
    private static string? CleanCardName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        // Strip trailing price/garbage like "30€", "$5", "£", stray numbers
        cleaned = Regex.Replace(cleaned, @"\s*\d*[€$£]+\d*\s*$", "");
        cleaned = Regex.Replace(cleaned, @"\s+\d{1,3}\s*$", "");
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned.Trim();
    }

    /// <summary>
    /// Given a type line index in a column, find where the card name starts above it.
    /// Returns the index of the first name line (above the art gap).
    /// </summary>
    private static int FindNameStartAboveTypeIdx(List<OcrLine> col, int typeIdx)
    {
        for (int j = typeIdx - 1; j >= 0; j--)
        {
            double gap = (j == typeIdx - 1)
                ? col[typeIdx].MinY - col[j].MaxY
                : col[j + 1].MinY - col[j].MaxY;

            if (gap > 100)
            {
                // Walk up from j to find start of name cluster
                int start = j;
                for (int k = j - 1; k >= 0; k--)
                {
                    if (col[k + 1].MinY - col[k].MaxY > 80) break;
                    start = k;
                }
                return start;
            }
        }
        return typeIdx;
    }

    /// <summary>
    /// Groups sorted values into clusters separated by significant gaps.
    /// </summary>
    private static List<(double Low, double High)> FindGapClusters(List<double> sortedValues, double minGap = 40)
    {
        if (sortedValues.Count == 0) return [];
        if (sortedValues.Count == 1) return [(sortedValues[0], sortedValues[0])];

        var gaps = new List<double>();
        for (int i = 1; i < sortedValues.Count; i++)
            gaps.Add(sortedValues[i] - sortedValues[i - 1]);

        var medianGap = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
        var threshold = Math.Max(medianGap * 3.0, minGap);

        var clusters = new List<(double Low, double High)>();
        double clusterStart = sortedValues[0];

        for (int i = 0; i < gaps.Count; i++)
        {
            if (gaps[i] > threshold)
            {
                clusters.Add((clusterStart, sortedValues[i]));
                clusterStart = sortedValues[i + 1];
            }
        }
        clusters.Add((clusterStart, sortedValues[^1]));

        return clusters;
    }

    /// <summary>
    /// Look up a card by set code and collector number on Scryfall (exact printing match).
    /// </summary>
    private static async Task<ScannedCard?> LookupBySetNumberAsync(
        HttpClient http, string setCode, string number, StringBuilder dbg, CancellationToken ct)
    {
        try
        {
            var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
            dbg.AppendLine($"    API: {url}");
            if (string.IsNullOrEmpty(url)) { dbg.AppendLine("    (empty URL)"); return null; }

            await ApiRateLimiter.WaitAsync();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            dbg.AppendLine($"    HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var set = root.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "";
            var num = root.TryGetProperty("collector_number", out var c) ? c.GetString() ?? "" : "";

            dbg.AppendLine($"    Scryfall returned: {set}/{num} — {name}");
            if (string.IsNullOrEmpty(name)) return null;
            return new ScannedCard(set, num, name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { dbg.AppendLine($"    EXCEPTION: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Look up a card by name and set code on Scryfall (right printing via set constraint).
    /// </summary>
    private static async Task<ScannedCard?> LookupByNameAndSetAsync(
        HttpClient http, string cardName, string setCode, StringBuilder dbg, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.scryfall.com/cards/named?fuzzy={Uri.EscapeDataString(cardName)}&set={Uri.EscapeDataString(setCode)}";
            dbg.AppendLine($"    NAME+SET API: {url}");
            await ApiRateLimiter.WaitAsync();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            dbg.AppendLine($"    HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var set = root.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "";
            var number = root.TryGetProperty("collector_number", out var c) ? c.GetString() ?? "" : "";

            dbg.AppendLine($"    Scryfall returned: {set}/{number} — {name}");
            if (string.IsNullOrEmpty(name)) return null;
            return new ScannedCard(set, number, name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { dbg.AppendLine($"    EXCEPTION: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fallback: look up a card by name using Scryfall fuzzy search.
    /// </summary>
    private static async Task<ScannedCard?> LookupByNameAsync(
        HttpClient http, string cardName, StringBuilder dbg, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.scryfall.com/cards/named?fuzzy={Uri.EscapeDataString(cardName)}";
            dbg.AppendLine($"    FUZZY API: {url}");
            await ApiRateLimiter.WaitAsync();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            dbg.AppendLine($"    HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var set = root.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "";
            var number = root.TryGetProperty("collector_number", out var c) ? c.GetString() ?? "" : "";

            dbg.AppendLine($"    Scryfall returned: {set}/{number} — {name}");
            if (string.IsNullOrEmpty(name)) return null;
            return new ScannedCard(set, number, name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { dbg.AppendLine($"    EXCEPTION: {ex.Message}"); return null; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Enfolderer.App.Imaging;
using Enfolderer.App.Core;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Binder;
using Enfolderer.App.Metadata; // for FetchSpec
namespace Enfolderer.App.Metadata;

public sealed class BinderFileParser
{
    private readonly BinderThemeService _theme;
    private readonly CardMetadataResolver _metadataResolver;
    private readonly Func<bool, string?> _resolveLocalBackImagePath;
    private readonly Func<string, bool> _isCacheComplete;

    public BinderFileParser(BinderThemeService theme,
                            CardMetadataResolver metadataResolver,
                            Func<bool, string?> resolveLocalBackImagePath,
                            Func<string,bool> isCacheComplete)
    {
        _theme = theme;
        _metadataResolver = metadataResolver;
        _resolveLocalBackImagePath = resolveLocalBackImagePath;
        _isCacheComplete = isCacheComplete;
    }

    // Precompiled regex patterns to avoid reparsing on large binder files (Phase 4 perf pass)
    private static readonly Regex BackfaceLineRegex = new(@"^(?<count>\d+)\s*;\s*backface$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrefixRangeRegex = new(@"^(?<pfx>[A-Za-z]{1,8})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex AttachedPrefixRegex = new(@"^(?<pfx>(?=.*[A-Za-z])[A-Za-z0-9\-]{1,24}?)(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex SpacedPrefixRegex = new(@"^(?<pfx>[A-Za-z0-9\-]{1,24})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex RangeSuffixRegex = new(@"^(?<start>\d+)-(?: (?<endSpace>\d+)|(?<end>\d+))(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
    private static readonly Regex PureNumericRangeRegex = new(@"^\d+-\d+$", RegexOptions.Compiled);
    private static readonly Regex SingleSuffixRegex = new(@"^(?<num>\d+)(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
    private static readonly Regex RangeVariantRegex = new(@"^(?<start>\d+)-(?:)(?<end>\d+)\+(?<lang>[A-Za-z]{1,8})$", RegexOptions.Compiled);
    private static readonly Regex PlusVariantRegex = new(@"^(?<base>[A-Za-z0-9]+)\+(?<seg>[A-Za-z]{1,8})$", RegexOptions.Compiled);

    public async Task<BinderParseResult> ParseAsync(string path, int slotsPerPage, CancellationToken ct = default)
    {
    var lines = await File.ReadAllLinesAsync(path, ct);
        _theme.Reset(path + (new FileInfo(path).LastWriteTimeUtc.Ticks));
        int? pagesPerBinderOverride = null;
        string? layoutModeOverride = null;
        bool enableHttpDebug = false;
        foreach (var dirLine in lines)
        {
            if (string.IsNullOrWhiteSpace(dirLine)) continue;
            var tl = dirLine.Trim();
            if (tl.StartsWith('#')) continue;
            var dr = _theme.ApplyDirectiveLine(tl);
            if (dr.PagesPerBinder.HasValue) pagesPerBinderOverride = dr.PagesPerBinder.Value;
            if (!string.IsNullOrEmpty(dr.LayoutMode)) layoutModeOverride = dr.LayoutMode;
            if (dr.EnableHttpDebug) enableHttpDebug = true;
            break;
        }
    string fileHash = ComputeJoinedLineHash(lines);
        var cachedCards = new List<CardEntry>();
        if (_isCacheComplete(fileHash) && _metadataResolver.TryLoadMetadataCache(fileHash, cachedCards))
        {
            // Re-create backface placeholder mapping(s) because the original parse path (which sets CardImageUrlStore)
            // is skipped on cache hits.
            try
            {
                string? localBack = _resolveLocalBackImagePath(true);
                // System.Diagnostics.Debug.WriteLine($"[BinderFileParser] CacheHit back resolve returned: {localBack ?? "<null>"}");
                bool hasLocalBack = localBack != null && (File.Exists(localBack) || localBack.StartsWith("pack://", StringComparison.OrdinalIgnoreCase));
                var fallbackPack = CardBackImageService.GetEmbeddedFallback() ?? "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg";
                foreach (var ce in cachedCards)
                {
                    if (string.Equals(ce.Set, "__BACK__", StringComparison.OrdinalIgnoreCase) && string.Equals(ce.EffectiveNumber, "BACK", StringComparison.OrdinalIgnoreCase))
                    {
                        var chosen = hasLocalBack ? localBack! : fallbackPack;
                        CardImageUrlStore.Set("__BACK__", "BACK", chosen, chosen);
                        System.Diagnostics.Debug.WriteLine($"[BinderFileParser] (CacheHit) Registered backface mapping front/back={chosen}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinderFileParser] Failed to restore backface mappings on cache hit: {ex.Message}");
            }
            return BinderParseResult.CacheHitResult(fileHash, cachedCards, pagesPerBinderOverride, layoutModeOverride, enableHttpDebug, Path.GetDirectoryName(path));
        }
    // Estimate capacities with a lightweight scan to reduce dynamic List growth
    EstimateCapacities(lines, slotsPerPage, out int specCap, out int fetchCap, out int variantPairCap);
    var parsedSpecs = new List<BinderParsedSpec>(specCap);
    var fetchList = new List<FetchSpec>(fetchCap);
    var pendingVariantPairs = new List<(string set,string baseNum,string variantNum)>(variantPairCap);
        string? localBackImagePath = null;
        string? currentSet = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith("**")) continue;
            if (line.StartsWith('#')) continue;
            var backfaceMatch = BackfaceLineRegex.Match(line);
            if (backfaceMatch.Success)
            {
                if (int.TryParse(backfaceMatch.Groups["count"].Value, out int backCount) && backCount > 0)
                {
                    if (localBackImagePath == null)
                        localBackImagePath = _resolveLocalBackImagePath(true);
                    bool hasLocal = localBackImagePath != null && (File.Exists(localBackImagePath) || localBackImagePath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase));
                    System.Diagnostics.Debug.WriteLine($"[BinderFileParser] Parse path back resolve returned: {localBackImagePath ?? "<null>"} hasLocal={hasLocal}");
                    for (int bi = 0; bi < backCount; bi++)
                    {
                        var spec = new BinderParsedSpec("__BACK__", "BACK", null, true, null, null);
                        parsedSpecs.Add(spec);
                        var entry = new CardEntry("Backface", "BACK", "__BACK__", false, true, null, null, string.Empty);
                        var frontUrl = hasLocal ? localBackImagePath! : (CardBackImageService.GetEmbeddedFallback() ?? "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg");
                        // Register image URL mapping for synthetic backface so CardSlot can load embedded/local resource.
                        CardImageUrlStore.Set("__BACK__", "BACK", frontUrl, frontUrl);
                        System.Diagnostics.Debug.WriteLine($"[BinderFileParser] Registered backface mapping front/back={frontUrl}");
                        parsedSpecs[^1] = parsedSpecs[^1] with { Resolved = entry };
                    }
                }
                continue;
            }
            if (line.StartsWith('=') && line.Length > 1)
            {
                currentSet = line.Substring(1).Trim();
                continue;
            }
            if (currentSet == null) continue;
            if (line.Count(c => c==';') >= 2)
            {
                var parts = line.Split(';', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3)
                {
                    string possibleName = parts[0];
                    string possibleSet = parts[1].ToUpperInvariant();
                    string possibleNumber = parts[2];
                    if (int.TryParse(possibleNumber, out _))
                    {
                        parsedSpecs.Add(new BinderParsedSpec(possibleSet, possibleNumber, possibleName, true, null, null));
                        continue;
                    }
                }
            }
            string? nameOverride = null;
            var semiIdx = line.IndexOf(';');
            string numberPart = line;
            if (semiIdx >= 0)
            {
                numberPart = line[..semiIdx].Trim();
                nameOverride = line[(semiIdx+1)..].Trim();
                if (string.IsNullOrEmpty(nameOverride)) nameOverride = null;
            }
            var prefixRangeMatch = PrefixRangeRegex.Match(numberPart);
            if (prefixRangeMatch.Success)
            {
                var pfx = prefixRangeMatch.Groups["pfx"].Value.Trim();
                var startStr = prefixRangeMatch.Groups["start"].Value;
                var endGrp = prefixRangeMatch.Groups["end"];
                if (endGrp.Success && int.TryParse(startStr, out int ps) && int.TryParse(endGrp.Value, out int pe) && ps <= pe)
                {
                    for (int n = ps; n <= pe; n++)
                    {
                        var fullNum = pfx + n.ToString();
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                        fetchList.Add(new FetchSpec(currentSet, fullNum, null, parsedSpecs.Count-1));
                    }
                    continue;
                }
                else
                {
                    var fullNum = pfx + startStr;
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add(new FetchSpec(currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
                    continue;
                }
            }
            if (numberPart.Contains("&&"))
            {
                static List<string> ExpandSimpleNumericRange(string text)
                {
                    var list = new List<string>();
                    text = text.Trim();
                    if (string.IsNullOrEmpty(text)) return list;
                    if (text.Contains('-'))
                    {
                        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e) && s <= e)
                        { for (int n=s; n<=e; n++) list.Add(n.ToString()); return list; }
                    }
                    if (int.TryParse(text, out int single)) list.Add(single.ToString());
                    return list;
                }
                var pairSegs = numberPart.Split("&&", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pairSegs.Length == 2)
                {
                    var primaryList = ExpandSimpleNumericRange(pairSegs[0]);
                    var secondaryList = ExpandSimpleNumericRange(pairSegs[1]);
                    if (primaryList.Count > 0 && primaryList.Count == secondaryList.Count)
                    {
                        for (int i = 0; i < primaryList.Count; i++)
                        {
                            var prim = primaryList[i];
                            var sec = secondaryList[i];
                            var numberDisplay = prim + "(" + sec + ")";
                            parsedSpecs.Add(new BinderParsedSpec(currentSet, prim, null, false, numberDisplay, null));
                            fetchList.Add(new FetchSpec(currentSet, prim, null, parsedSpecs.Count -1));
                        }
                        continue;
                    }
                }
            }
            bool isPureNumericRange = PureNumericRangeRegex.IsMatch(numberPart);
            if (!isPureNumericRange)
            {
                var attachedPrefixMatch = AttachedPrefixRegex.Match(numberPart);
                if (attachedPrefixMatch.Success)
                {
                    var pfx = attachedPrefixMatch.Groups["pfx"].Value;
                    var startStr = attachedPrefixMatch.Groups["start"].Value;
                    var endGrp = attachedPrefixMatch.Groups["end"];
                    int width = startStr.Length;
                    if (endGrp.Success && int.TryParse(startStr, out int aps) && int.TryParse(endGrp.Value, out int ape) && aps <= ape)
                    {
                        for (int n = aps; n <= ape; n++)
                        {
                            var fullNum = pfx + n.ToString().PadLeft(width,'0');
                            parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                            fetchList.Add(new FetchSpec(currentSet, fullNum, null, parsedSpecs.Count -1));
                        }
                        continue;
                    }
                    else
                    {
                        var fullNum = pfx + startStr;
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                        fetchList.Add(new FetchSpec(currentSet, fullNum, nameOverride, parsedSpecs.Count -1));
                        continue;
                    }
                }
            }
            var spacedPrefixMatch = SpacedPrefixRegex.Match(numberPart);
            if (spacedPrefixMatch.Success)
            {
                var pfx = spacedPrefixMatch.Groups["pfx"].Value;
                var startStr = spacedPrefixMatch.Groups["start"].Value;
                var endGrp = spacedPrefixMatch.Groups["end"];
                int width = startStr.Length;
                if (endGrp.Success && int.TryParse(startStr, out int sps) && int.TryParse(endGrp.Value, out int spe) && sps <= spe)
                {
                    for (int n = sps; n <= spe; n++)
                    {
                        var fullNum = pfx + n.ToString().PadLeft(width,'0');
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                        fetchList.Add(new FetchSpec(currentSet, fullNum, null, parsedSpecs.Count-1));
                    }
                    continue;
                }
                else
                {
                    var fullNum = pfx + startStr;
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add(new FetchSpec(currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
                    continue;
                }
            }
            var rangeSuffixMatch = RangeSuffixRegex.Match(numberPart);
            if (rangeSuffixMatch.Success)
            {
                string startStr = rangeSuffixMatch.Groups["start"].Value;
                string endStr = rangeSuffixMatch.Groups["end"].Success ? rangeSuffixMatch.Groups["end"].Value : rangeSuffixMatch.Groups["endSpace"].Value;
                string suffix = rangeSuffixMatch.Groups["suffix"].Value;
                if (int.TryParse(startStr, out int rs) && int.TryParse(endStr, out int re) && rs <= re)
                {
                    int width = startStr.Length;
                    for (int n = rs; n <= re; n++)
                    {
                        var fullNum = n.ToString().PadLeft(width,'0') + suffix;
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                            fetchList.Add(new FetchSpec(currentSet, fullNum, null, parsedSpecs.Count -1));
                    }
                    continue;
                }
            }
            var singleSuffixMatch = SingleSuffixRegex.Match(numberPart);
            if (singleSuffixMatch.Success)
            {
                var numStr = singleSuffixMatch.Groups["num"].Value;
                var suffix = singleSuffixMatch.Groups["suffix"].Value;
                var fullNum = numStr + suffix;
                parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                fetchList.Add(new FetchSpec(currentSet, fullNum, nameOverride, parsedSpecs.Count -1));
                continue;
            }
            if (numberPart.StartsWith('★'))
            {
                var starBody = numberPart[1..].Trim();
                if (starBody.Contains('-'))
                {
                    var dashIdx = starBody.IndexOf('-');
                    if (dashIdx > 0 && dashIdx < starBody.Length - 1)
                    {
                        var leftStr = starBody.Substring(0, dashIdx).Trim();
                        var rightStr = starBody.Substring(dashIdx + 1).Trim();
                        if (int.TryParse(leftStr, out int s) && int.TryParse(rightStr, out int e) && s <= e)
                        {
                            for (int n = s; n <= e; n++)
                            {
                                var fullNum = n.ToString() + '★';
                                parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                                fetchList.Add(new FetchSpec(currentSet, fullNum, null, parsedSpecs.Count - 1));
                            }
                            continue;
                        }
                    }
                }
                if (int.TryParse(starBody, out int singleStar))
                {
                    var fullNum = singleStar.ToString() + '★';
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add(new FetchSpec(currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
                    continue;
                }
            }
            if (numberPart.Contains("||", StringComparison.Ordinal))
            {
                var segments = numberPart.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1)
                {
                    var lists = new List<List<string>>();
                    foreach (var seg in segments)
                    {
                        var segPrefixMatch = PrefixRangeRegex.Match(seg);
                        if (segPrefixMatch.Success)
                        {
                            var pfx = segPrefixMatch.Groups["pfx"].Value;
                            var sStr = segPrefixMatch.Groups["start"].Value;
                            var eGrp = segPrefixMatch.Groups["end"];
                            if (eGrp.Success && int.TryParse(sStr, out int sNum) && int.TryParse(eGrp.Value, out int eNum) && sNum <= eNum)
                            {
                                var l = new List<string>();
                                for (int n = sNum; n <= eNum; n++) l.Add(pfx + n.ToString());
                                lists.Add(l);
                            }
                            else
                            {
                                lists.Add(new List<string>{ pfx + sStr });
                            }
                        }
                        else if (seg.Contains('-', StringComparison.Ordinal))
                        {
                            var pieces = seg.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (pieces.Length==2 && int.TryParse(pieces[0], out int s) && int.TryParse(pieces[1], out int e) && s<=e)
                            {
                                var l = new List<string>();
                                for (int n=s; n<=e; n++) l.Add(n.ToString());
                                lists.Add(l);
                            }
                        }
                        else if (int.TryParse(seg, out int singleNum))
                        {
                            lists.Add(new List<string>{ singleNum.ToString() });
                        }
                    }
                    if (lists.Count > 0)
                    {
                        bool anyLeft;
                        do
                        {
                            anyLeft = false;
                            foreach (var l in lists)
                            {
                                if (l.Count == 0) continue;
                                var firstVal = l[0];
                                parsedSpecs.Add(new BinderParsedSpec(currentSet, firstVal, null, false, null, null));
                                fetchList.Add(new FetchSpec(currentSet, firstVal, null, parsedSpecs.Count-1));
                                l.RemoveAt(0);
                                if (l.Count > 0) anyLeft = true;
                            }
                            anyLeft = lists.Exists(x => x.Count > 0);
                        } while (anyLeft);
                        continue;
                    }
                }
            }
            var rangeVariantMatch = RangeVariantRegex.Match(numberPart);
            if (rangeVariantMatch.Success)
            {
                var startStr = rangeVariantMatch.Groups["start"].Value;
                var endStr = rangeVariantMatch.Groups["end"].Value;
                var lang = rangeVariantMatch.Groups["lang"].Value.ToLowerInvariant();
                if (int.TryParse(startStr, out int rs) && int.TryParse(endStr, out int re) && rs <= re)
                {
                    int padWidth = (startStr.StartsWith('0') && startStr.Length == endStr.Length) ? startStr.Length : 0;
                    for (int k = rs; k <= re; k++)
                    {
                        var baseNum = padWidth>0 ? k.ToString().PadLeft(padWidth,'0') : k.ToString();
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, baseNum, nameOverride, false, null, null));
                        fetchList.Add(new FetchSpec(currentSet, baseNum, nameOverride, parsedSpecs.Count-1));
                        var variantNumber = baseNum + "/" + lang;
                        var variantDisplay = baseNum + " (" + lang + ")";
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, variantNumber, nameOverride, false, variantDisplay, null));
                        fetchList.Add(new FetchSpec(currentSet, variantNumber, nameOverride, parsedSpecs.Count-1));
                        try { pendingVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch (System.Exception) { throw; }
                    }
                    continue;
                }
            }
            if (TryParseSimpleNumericRange(numberPart, out int rStart, out int rEnd, out int rPad))
            {
                for (int n = rStart; n <= rEnd; n++)
                {
                    var numStr = rPad > 0 ? n.ToString().PadLeft(rPad, '0') : n.ToString();
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, numStr, null, false, null, null));
                    fetchList.Add(new FetchSpec(currentSet, numStr, null, parsedSpecs.Count - 1));
                }
                continue;
            }
            var plusVariantMatch = PlusVariantRegex.Match(numberPart);
            if (plusVariantMatch.Success)
            {
                var baseNum = plusVariantMatch.Groups["base"].Value;
                var seg = plusVariantMatch.Groups["seg"].Value.ToLowerInvariant();
                parsedSpecs.Add(new BinderParsedSpec(currentSet, baseNum, nameOverride, false, null, null));
                fetchList.Add(new FetchSpec(currentSet, baseNum, nameOverride, parsedSpecs.Count-1));
                var variantNumber = baseNum + "/" + seg;
                var variantDisplay = baseNum + " (" + seg + ")";
                parsedSpecs.Add(new BinderParsedSpec(currentSet, variantNumber, nameOverride, false, variantDisplay, null));
                fetchList.Add(new FetchSpec(currentSet, variantNumber, nameOverride, parsedSpecs.Count-1));
                try { pendingVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch (System.Exception) { throw; }
                continue;
            }
            var numFinal = numberPart.Trim();
            if (numFinal.Length>0)
            {
                parsedSpecs.Add(new BinderParsedSpec(currentSet, numFinal, nameOverride, false, null, null));
                fetchList.Add(new FetchSpec(currentSet, numFinal, nameOverride, parsedSpecs.Count-1));
            }
        }
        int needed = slotsPerPage * 2;
        var initialIndexes = new HashSet<int>();
        for (int i = 0; i < parsedSpecs.Count && initialIndexes.Count < needed; i++) initialIndexes.Add(i);
        return BinderParseResult.Success(fileHash, parsedSpecs, fetchList, initialIndexes, pendingVariantPairs, Path.GetDirectoryName(path), localBackImagePath, pagesPerBinderOverride, layoutModeOverride, enableHttpDebug);
    }

    private static string ComputeJoinedLineHash(string[] lines)
    {
        using var sha = SHA256.Create();
        // Reuse a single pooled buffer for most lines (UTF8 expansion worst-case 4x, typical ASCII ~1x)
        byte[] buffer = ArrayPool<byte>.Shared.Rent(2048);
        Span<byte> nl = stackalloc byte[1]; nl[0] = (byte)'\n';
    // Single shared newline buffer
    byte[] newline = new byte[]{ (byte)'\n' };
    foreach (var line in lines)
        {
            int needed = Encoding.UTF8.GetByteCount(line);
            if (needed <= buffer.Length)
            {
                int written = Encoding.UTF8.GetBytes(line, 0, line.Length, buffer, 0);
                sha.TransformBlock(buffer, 0, written, null, 0);
            }
            else
            {
                // Rare very long line path: rent a larger temporary
                byte[] tmp = ArrayPool<byte>.Shared.Rent(needed);
                try
                {
                    int written = Encoding.UTF8.GetBytes(line, 0, line.Length, tmp, 0);
                    sha.TransformBlock(tmp, 0, written, null, 0);
                }
                finally { ArrayPool<byte>.Shared.Return(tmp); }
            }
            sha.TransformBlock(newline, 0, 1, null, 0); // normalized newline
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        ArrayPool<byte>.Shared.Return(buffer);
        return Convert.ToHexString(sha.Hash!);
    }

    // Lightweight capacity estimator to reduce List<T> growth reallocations
    private static void EstimateCapacities(string[] lines, int slotsPerPage, out int specCap, out int fetchCap, out int variantPairCap)
    {
        int specs = 0; int fetch = 0; int variantPairs = 0; string? currentSet = null;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            if (line.StartsWith("**")) continue; // directive-like skip
            if (line.StartsWith('=')) { currentSet = line.Length>1 ? line.Substring(1).Trim() : currentSet; continue; }
            if (currentSet == null) continue;
            // Backface pattern: "<count>; backface" – specs but no fetch entries
            if (line.EndsWith("backface", StringComparison.OrdinalIgnoreCase))
            {
                int semi = line.IndexOf(';');
                if (semi > 0 && int.TryParse(line.AsSpan(0, semi), out int backCount) && backCount > 0)
                { specs += backCount; continue; }
            }
            // Extract numberPart (before first ';') similar to main parser
            var semiIdx = line.IndexOf(';');
            string numberPart = semiIdx >= 0 ? line.Substring(0, semiIdx).Trim() : line;
            int added = 1; // default one spec
            // Simple numeric range a-b
            int dash = numberPart.IndexOf('-');
            if (dash > 0 && dash < numberPart.Length - 1 && numberPart.IndexOf('-', dash + 1) < 0)
            {
                // Attempt prefix+range or pure numeric
                int rStartDigitsPos = dash;
                // Find start digits sequence
                int leftDigitStart = -1; int leftDigitEnd = -1;
                for (int i = dash - 1; i >= 0; i--)
                {
                    if (char.IsDigit(numberPart[i])) { leftDigitStart = i; }
                    else { if (leftDigitStart >= 0) { leftDigitEnd = i + 1; break; } }
                }
                if (leftDigitStart >= 0 && leftDigitEnd == -1) leftDigitEnd = 0;
                if (leftDigitStart >= 0)
                {
                    // Extract left digits
                    int len = dash - leftDigitStart;
                    if (int.TryParse(numberPart.AsSpan(leftDigitStart, len), out int leftVal))
                    {
                        // Right side digits
                        int rightStart = dash + 1; int rightEnd = rightStart;
                        while (rightEnd < numberPart.Length && char.IsDigit(numberPart[rightEnd])) rightEnd++;
                        if (rightEnd > rightStart && int.TryParse(numberPart.AsSpan(rightStart, rightEnd - rightStart), out int rightVal) && rightVal >= leftVal)
                        {
                            int count = (rightVal - leftVal + 1);
                            if (count > 1) added = count;
                        }
                    }
                }
            }
            // Variant duplication heuristic (+lang or +seg doubles each base)
            if (numberPart.Contains('+') && added > 0)
            {
                // Range + variant => doubled; single + variant => 2 specs
                added = added * 2;
                variantPairs += added / 2; // approximate
            }
            specs += added;
            fetch += added; // almost every spec (except backfaces handled earlier) needs fetch
        }
        // Provide a modest buffer headroom and minimum baselines
        if (specs < lines.Length) specs = lines.Length;
        specCap = specs + Math.Min(256, specs / 8 + 8);
        fetchCap = fetch + Math.Min(256, fetch / 8 + 8);
        variantPairCap = Math.Max(32, variantPairs + 4);
    }

    // Lightweight parser for pattern: Name;Set;Number[;...]
    private static bool TryParseSemicolonTriple(string line, out string name, out string set, out string number)
    {
        name = set = number = string.Empty;
        int first = line.IndexOf(';'); if (first <= 0) return false;
        int second = line.IndexOf(';', first + 1); if (second <= first + 1) return false;
        int third = line.IndexOf(';', second + 1); // may be -1 (we only need first three tokens)
        name = line.Substring(0, first).Trim();
        set = line.Substring(first + 1, second - first - 1).Trim();
        int numEnd = third >= 0 ? third : line.Length;
        number = line.Substring(second + 1, numEnd - second - 1).Trim();
        if (name.Length == 0 || set.Length == 0 || number.Length == 0) return false;
        return true;
    }

    private static bool TryParseSemicolonTripleFast(string line, out string name, out string set, out string number)
    {
        name = set = number = string.Empty;
        int len = line.Length;
        int first = -1; int second = -1; int found = 0;
        for (int i = 0; i < len && found < 2; i++)
        {
            if (line[i] == ';')
            {
                if (first < 0) first = i; else { second = i; break; }
                found++;
            }
        }
        if (first <= 0 || second <= first + 1) return false;
        // quick scan for third only if present
        int third = -1;
        for (int i = second + 1; i < len; i++) { if (line[i] == ';') { third = i; break; } }
        name = line.AsSpan(0, first).Trim().ToString();
        set = line.AsSpan(first + 1, second - first - 1).Trim().ToString();
        int numEnd = third >= 0 ? third : len;
        number = line.AsSpan(second + 1, numEnd - second - 1).Trim().ToString();
        return name.Length > 0 && set.Length > 0 && number.Length > 0;
    }

    private static bool TryParseSimpleNumericRange(string text, out int start, out int end, out int padWidth)
    {
        start = end = 0; padWidth = 0;
        int dash = text.IndexOf('-');
        if (dash <= 0 || dash >= text.Length - 1) return false;
        // reject if extra dash present (avoid mis-parsing more complex forms)
        if (text.IndexOf('-', dash + 1) >= 0) return false;
        var left = text.AsSpan(0, dash).Trim();
        var right = text.AsSpan(dash + 1).Trim();
        if (!int.TryParse(left, out start) || !int.TryParse(right, out end) || start > end) return false;
        padWidth = (left.Length == right.Length && left.Length > 1 && left[0] == '0') ? left.Length : 0;
        return true;
    }
}

public sealed record BinderParseResult(
    string FileHash,
    bool CacheHit,
    List<CardEntry> CachedCards,
    List<BinderParsedSpec> Specs,
    List<FetchSpec> FetchList,
    HashSet<int> InitialSpecIndexes,
    List<(string set,string baseNum,string variantNum)> PendingVariantPairs,
    string? CollectionDir,
    string? LocalBackImagePath,
    int? PagesPerBinderOverride,
    string? LayoutModeOverride,
    bool HttpDebugEnabled)
{
    public static BinderParseResult CacheHitResult(string hash, List<CardEntry> cached, int? pagesOverride, string? layoutOverride, bool httpDebug, string? dir) =>
        new BinderParseResult(hash, true, cached, new List<BinderParsedSpec>(), new List<FetchSpec>(), new HashSet<int>(), new List<(string set,string baseNum,string variantNum)>(), dir, null, pagesOverride, layoutOverride, httpDebug);
    public static BinderParseResult Success(string hash, List<BinderParsedSpec> specs, List<FetchSpec> fetch, HashSet<int> initial, List<(string set,string baseNum,string variantNum)> pendingPairs, string? dir, string? backImage, int? pagesOverride, string? layoutOverride, bool httpDebug) =>
        new BinderParseResult(hash, false, new List<CardEntry>(), specs, fetch, initial, pendingPairs, dir, backImage, pagesOverride, layoutOverride, httpDebug);
}

public sealed record BinderParsedSpec(string SetCode, string Number, string? OverrideName, bool ExplicitEntry, string? NumberDisplayOverride, CardEntry? Resolved);

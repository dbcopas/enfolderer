using System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Enfolderer.App;

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
        string fileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        var cachedCards = new List<CardEntry>();
        if (_isCacheComplete(fileHash) && _metadataResolver.TryLoadMetadataCache(fileHash, cachedCards))
        {
            // Re-create backface placeholder mapping(s) because the original parse path (which sets CardImageUrlStore)
            // is skipped on cache hits.
            try
            {
                string? localBack = _resolveLocalBackImagePath(true);
                bool hasLocalBack = localBack != null && (File.Exists(localBack) || localBack.StartsWith("pack://", StringComparison.OrdinalIgnoreCase));
                var fallbackPack = "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg";
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
        var parsedSpecs = new List<BinderParsedSpec>();
        var fetchList = new List<(string setCode,string number,string? nameOverride,int specIndex)>();
        var pendingVariantPairs = new List<(string set,string baseNum,string variantNum)>();
        string? localBackImagePath = null;
        string? currentSet = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith("**")) continue;
            if (line.StartsWith('#')) continue;
            var backfaceMatch = Regex.Match(line, @"^(?<count>\d+)\s*;\s*backface$", RegexOptions.IgnoreCase);
            if (backfaceMatch.Success)
            {
                if (int.TryParse(backfaceMatch.Groups["count"].Value, out int backCount) && backCount > 0)
                {
                    if (localBackImagePath == null)
                        localBackImagePath = _resolveLocalBackImagePath(true);
                    bool hasLocal = localBackImagePath != null && (File.Exists(localBackImagePath) || localBackImagePath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase));
                    for (int bi = 0; bi < backCount; bi++)
                    {
                        var spec = new BinderParsedSpec("__BACK__", "BACK", null, true, null, null);
                        parsedSpecs.Add(spec);
                        var entry = new CardEntry("Backface", "BACK", "__BACK__", false, true, null, null, string.Empty);
                        var frontUrl = hasLocal ? localBackImagePath! : "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg";
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
            var prefixRangeMatch = Regex.Match(numberPart, @"^(?<pfx>[A-Za-z]{1,8})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
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
                        fetchList.Add((currentSet, fullNum, null, parsedSpecs.Count-1));
                    }
                    continue;
                }
                else
                {
                    var fullNum = pfx + startStr;
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add((currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
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
                            fetchList.Add((currentSet, prim, null, parsedSpecs.Count -1));
                        }
                        continue;
                    }
                }
            }
            bool isPureNumericRange = Regex.IsMatch(numberPart, @"^\d+-\d+$");
            if (!isPureNumericRange)
            {
                var attachedPrefixMatch = Regex.Match(numberPart, @"^(?<pfx>(?=.*[A-Za-z])[A-Za-z0-9\-]{1,24}?)(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
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
                            fetchList.Add((currentSet, fullNum, null, parsedSpecs.Count -1));
                        }
                        continue;
                    }
                    else
                    {
                        var fullNum = pfx + startStr;
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                        fetchList.Add((currentSet, fullNum, nameOverride, parsedSpecs.Count -1));
                        continue;
                    }
                }
            }
            var spacedPrefixMatch = Regex.Match(numberPart, @"^(?<pfx>[A-Za-z0-9\-]{1,24})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
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
                        fetchList.Add((currentSet, fullNum, null, parsedSpecs.Count-1));
                    }
                    continue;
                }
                else
                {
                    var fullNum = pfx + startStr;
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add((currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
                    continue;
                }
            }
            var rangeSuffixMatch = Regex.Match(numberPart, @"^(?<start>\d+)-(?: (?<endSpace>\d+)|(?<end>\d+))(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
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
                        fetchList.Add((currentSet, fullNum, null, parsedSpecs.Count -1));
                    }
                    continue;
                }
            }
            var singleSuffixMatch = Regex.Match(numberPart, @"^(?<num>\d+)(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
            if (singleSuffixMatch.Success)
            {
                var numStr = singleSuffixMatch.Groups["num"].Value;
                var suffix = singleSuffixMatch.Groups["suffix"].Value;
                var fullNum = numStr + suffix;
                parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                fetchList.Add((currentSet, fullNum, nameOverride, parsedSpecs.Count -1));
                continue;
            }
            if (numberPart.StartsWith('★'))
            {
                var starBody = numberPart[1..].Trim();
                if (starBody.Contains('-'))
                {
                    var pieces = starBody.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (pieces.Length == 2 && int.TryParse(pieces[0], out int s) && int.TryParse(pieces[1], out int e) && s <= e)
                    {
                        for (int n=s; n<=e; n++)
                        {
                            var fullNum = n.ToString() + '★';
                            parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, null, false, null, null));
                            fetchList.Add((currentSet, fullNum, null, parsedSpecs.Count-1));
                        }
                        continue;
                    }
                }
                if (int.TryParse(starBody, out int singleStar))
                {
                    var fullNum = singleStar.ToString() + '★';
                    parsedSpecs.Add(new BinderParsedSpec(currentSet, fullNum, nameOverride, false, null, null));
                    fetchList.Add((currentSet, fullNum, nameOverride, parsedSpecs.Count-1));
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
                        var segPrefixMatch = Regex.Match(seg, @"^(?<pfx>[A-Za-z]{1,8})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
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
                                fetchList.Add((currentSet, firstVal, null, parsedSpecs.Count-1));
                                l.RemoveAt(0);
                                if (l.Count > 0) anyLeft = true;
                            }
                            anyLeft = lists.Exists(x => x.Count > 0);
                        } while (anyLeft);
                        continue;
                    }
                }
            }
            var rangeVariantMatch = Regex.Match(numberPart, @"^(?<start>\d+)-(?:)(?<end>\d+)\+(?<lang>[A-Za-z]{1,8})$", RegexOptions.Compiled);
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
                        fetchList.Add((currentSet, baseNum, nameOverride, parsedSpecs.Count-1));
                        var variantNumber = baseNum + "/" + lang;
                        var variantDisplay = baseNum + " (" + lang + ")";
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, variantNumber, nameOverride, false, variantDisplay, null));
                        fetchList.Add((currentSet, variantNumber, nameOverride, parsedSpecs.Count-1));
                        try { pendingVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch { }
                    }
                    continue;
                }
            }
            if (numberPart.Contains('-', StringComparison.Ordinal))
            {
                var pieces = numberPart.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length==2 && int.TryParse(pieces[0], out int startNum) && int.TryParse(pieces[1], out int endNum) && startNum<=endNum)
                {
                    int padWidth = (pieces[0].StartsWith('0') && pieces[0].Length == pieces[1].Length) ? pieces[0].Length : 0;
                    for (int n=startNum; n<=endNum; n++)
                    {
                        var numStr = padWidth>0 ? n.ToString().PadLeft(padWidth,'0') : n.ToString();
                        parsedSpecs.Add(new BinderParsedSpec(currentSet, numStr, null, false, null, null));
                        fetchList.Add((currentSet, numStr, null, parsedSpecs.Count-1));
                    }
                }
                continue;
            }
            var plusVariantMatch = Regex.Match(numberPart, @"^(?<base>[A-Za-z0-9]+)\+(?<seg>[A-Za-z]{1,8})$", RegexOptions.Compiled);
            if (plusVariantMatch.Success)
            {
                var baseNum = plusVariantMatch.Groups["base"].Value;
                var seg = plusVariantMatch.Groups["seg"].Value.ToLowerInvariant();
                parsedSpecs.Add(new BinderParsedSpec(currentSet, baseNum, nameOverride, false, null, null));
                fetchList.Add((currentSet, baseNum, nameOverride, parsedSpecs.Count-1));
                var variantNumber = baseNum + "/" + seg;
                var variantDisplay = baseNum + " (" + seg + ")";
                parsedSpecs.Add(new BinderParsedSpec(currentSet, variantNumber, nameOverride, false, variantDisplay, null));
                fetchList.Add((currentSet, variantNumber, nameOverride, parsedSpecs.Count-1));
                try { pendingVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch { }
                continue;
            }
            var numFinal = numberPart.Trim();
            if (numFinal.Length>0)
            {
                parsedSpecs.Add(new BinderParsedSpec(currentSet, numFinal, nameOverride, false, null, null));
                fetchList.Add((currentSet, numFinal, nameOverride, parsedSpecs.Count-1));
            }
        }
        int needed = slotsPerPage * 2;
        var initialIndexes = new HashSet<int>();
        for (int i = 0; i < parsedSpecs.Count && initialIndexes.Count < needed; i++) initialIndexes.Add(i);
        return BinderParseResult.Success(fileHash, parsedSpecs, fetchList, initialIndexes, pendingVariantPairs, Path.GetDirectoryName(path), localBackImagePath, pagesPerBinderOverride, layoutModeOverride, enableHttpDebug);
    }
}

public sealed record BinderParseResult(
    string FileHash,
    bool CacheHit,
    List<CardEntry> CachedCards,
    List<BinderParsedSpec> Specs,
    List<(string setCode,string number,string? nameOverride,int specIndex)> FetchList,
    HashSet<int> InitialSpecIndexes,
    List<(string set,string baseNum,string variantNum)> PendingVariantPairs,
    string? CollectionDir,
    string? LocalBackImagePath,
    int? PagesPerBinderOverride,
    string? LayoutModeOverride,
    bool HttpDebugEnabled)
{
    public static BinderParseResult CacheHitResult(string hash, List<CardEntry> cached, int? pagesOverride, string? layoutOverride, bool httpDebug, string? dir) =>
        new BinderParseResult(hash, true, cached, new List<BinderParsedSpec>(), new List<(string setCode,string number,string? nameOverride,int specIndex)>(), new HashSet<int>(), new List<(string set,string baseNum,string variantNum)>(), dir, null, pagesOverride, layoutOverride, httpDebug);
    public static BinderParseResult Success(string hash, List<BinderParsedSpec> specs, List<(string setCode,string number,string? nameOverride,int specIndex)> fetch, HashSet<int> initial, List<(string set,string baseNum,string variantNum)> pendingPairs, string? dir, string? backImage, int? pagesOverride, string? layoutOverride, bool httpDebug) =>
        new BinderParseResult(hash, false, new List<CardEntry>(), specs, fetch, initial, pendingPairs, dir, backImage, pagesOverride, layoutOverride, httpDebug);
}

public sealed record BinderParsedSpec(string SetCode, string Number, string? OverrideName, bool ExplicitEntry, string? NumberDisplayOverride, CardEntry? Resolved);

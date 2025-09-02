using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Enfolderer.App;

public class CardMetadataResolver
{
    private readonly HashSet<string> _physicallyTwoSidedLayouts;
    private readonly string _cacheRoot;
    private readonly int _schemaVersion;
    private static readonly bool CacheDebug = Environment.GetEnvironmentVariable("ENFOLDERER_CACHE_DEBUG") == "1";
    private static void CacheLog(string msg) { if (CacheDebug) Debug.WriteLine("[Cache][Diag] " + msg); }

    public CardMetadataResolver(string cacheRoot, IEnumerable<string> physicallyTwoSidedLayouts, int schemaVersion)
    {
        _cacheRoot = cacheRoot;
        _physicallyTwoSidedLayouts = new HashSet<string>(physicallyTwoSidedLayouts, StringComparer.OrdinalIgnoreCase);
        _schemaVersion = schemaVersion;
    }

    private string MetaCacheDir => Path.Combine(_cacheRoot, "meta");
    private string MetaCachePath(string hash) => Path.Combine(MetaCacheDir, hash + ".json");
    private string MetaCacheDonePath(string hash) => Path.Combine(MetaCacheDir, hash + ".done");
    private string CardCacheDir => Path.Combine(MetaCacheDir, "cards");
    private string CardCachePath(string setCode, string number)
    {
        var safeSet = (setCode ?? string.Empty).ToLowerInvariant();
        var safeNum = number.Replace('/', '_').Replace('\\', '_').Replace(':','_');
        return Path.Combine(CardCacheDir, safeSet + "-" + safeNum + ".json");
    }

    private record CardCacheEntry(string Set, string Number, string Name, bool IsMfc, string? FrontRaw, string? BackRaw, string? FrontImageUrl, string? BackImageUrl, string? Layout, int SchemaVersion, DateTime FetchedUtc);
    public record CachedFace(string Name,string Number,string? Set,bool IsMfc,bool IsBack,string? FrontRaw,string? BackRaw,string? FrontImageUrl,string? BackImageUrl,string? Layout,int SchemaVersion);

    public bool TryLoadCardFromCache(string setCode, string number, out CardEntry? entry)
    {
        entry = null;
        try
        {
            var path = CardCachePath(setCode, number);
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<CardCacheEntry>(json);
            if (data == null) return false;
            if (string.IsNullOrEmpty(data.Layout)) return false;
            bool physTwoSided = data.Layout != null && _physicallyTwoSidedLayouts.Contains(data.Layout);
            bool effectiveMfc = data.IsMfc && physTwoSided;
            var ce = new CardEntry(data.Name, data.Number, data.Set, effectiveMfc, false, data.FrontRaw, data.BackRaw, null);
            entry = ce;
            CardImageUrlStore.Set(data.Set, data.Number, data.FrontImageUrl, data.BackImageUrl);
            CardLayoutStore.Set(data.Set, data.Number, data.Layout);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerCardCache] Failed to load {setCode} {number}: {ex.Message}");
            return false;
        }
    }

    public void PersistCardToCache(CardEntry ce)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ce.Set) || string.IsNullOrWhiteSpace(ce.Number)) return;
            Directory.CreateDirectory(CardCacheDir);
            var (frontImg, backImg) = CardImageUrlStore.Get(ce.Set, ce.Number);
            var layout = CardLayoutStore.Get(ce.Set!, ce.Number);
            var data = new CardCacheEntry(ce.Set!, ce.Number, ce.Name, ce.IsModalDoubleFaced && !ce.IsBackFace, ce.FrontRaw, ce.BackRaw, frontImg, backImg, layout, _schemaVersion, DateTime.UtcNow);
            var path = CardCachePath(ce.Set!, ce.Number);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(data));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerCardCache] Persist failed {ce.Set} {ce.Number}: {ex.Message}");
        }
    }

    public bool TryLoadMetadataCache(string hash, List<CardEntry> intoCards)
    {
        try
        {
            var path = MetaCachePath(hash);
            if (!File.Exists(path)) { CacheLog($"MISS hash={hash} reason=file_missing path={path}"); return false; }
            var json = File.ReadAllText(path);
            var faces = JsonSerializer.Deserialize<List<CachedFace>>(json);
            if (faces == null) { CacheLog($"MISS hash={hash} reason=deser_null"); return false; }
            if (faces.Count == 0) { CacheLog($"MISS hash={hash} reason=empty_list"); return false; }
            // Schema version diagnostic (not enforced currently)
            var firstVersion = faces[0].SchemaVersion;
            if (firstVersion != _schemaVersion)
            {
                CacheLog($"NOTE hash={hash} schema_mismatch cached={firstVersion} current={_schemaVersion} (still attempting use)");
            }
            var missingLayout = faces.Where(f => string.IsNullOrEmpty(f.Layout) && !(string.Equals(f.Set, "__BACK__", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Number, "BACK", StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
            if (missingLayout.Count > 0)
            {
                CacheLog($"MISS hash={hash} reason=missing_layout count={missingLayout.Count} sample=" + string.Join(',', missingLayout.Select(f => (f.Set??"?")+":"+f.Number)));
                return false;
            }
            intoCards.Clear();
            foreach (var f in faces)
            {
                bool physTwoSided = f.Layout != null && _physicallyTwoSidedLayouts.Contains(f.Layout);
                bool effectiveMfc = f.IsMfc && physTwoSided && !f.IsBack;
                var ce = new CardEntry(f.Name, f.Number, f.Set, effectiveMfc, f.IsBack, f.FrontRaw, f.BackRaw, null);
                intoCards.Add(ce);
                if (!f.IsBack)
                    CardImageUrlStore.Set(f.Set ?? string.Empty, f.Number, f.FrontImageUrl, f.BackImageUrl);
                if (!string.IsNullOrEmpty(f.Layout) && f.Set != null)
                    CardLayoutStore.Set(f.Set, f.Number, f.Layout);
            }
            CacheLog($"HIT hash={hash} faces={faces.Count} schema={firstVersion}");
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to load metadata cache: {ex.Message}"); return false; }
    }

    public void PersistMetadataCache(string? hash, List<CardEntry> cards)
    {
        if (string.IsNullOrEmpty(hash)) return;
        try
        {
            Directory.CreateDirectory(MetaCacheDir);
            var list = new List<CachedFace>();
            foreach (var c in cards)
            {
                var (frontImg, backImg) = CardImageUrlStore.Get(c.Set ?? string.Empty, c.Number);
                var layout = c.Set != null ? CardLayoutStore.Get(c.Set, c.Number) : null;
                if (layout == null && string.Equals(c.Set, "__BACK__", StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, "BACK", StringComparison.OrdinalIgnoreCase))
                {
                    layout = "back_placeholder"; // synthetic layout so cache can be reused
                }
                list.Add(new CachedFace(c.Name, c.Number, c.Set, c.IsModalDoubleFaced, c.IsBackFace, c.FrontRaw, c.BackRaw, frontImg, backImg, layout, _schemaVersion));
            }
            var json = JsonSerializer.Serialize(list);
            var path = MetaCachePath(hash);
            bool existed = File.Exists(path);
            File.WriteAllText(path, json);
            if (CacheDebug)
            {
                Debug.WriteLine($"[Cache][Diag] Persist hash={hash} faces={list.Count} overwrite={existed}");
                var missingLayout = list.Where(f => string.IsNullOrEmpty(f.Layout)).Take(5).ToList();
                if (missingLayout.Count>0) Debug.WriteLine($"[Cache][Diag] Persist missing_layout count={missingLayout.Count} sample=" + string.Join(',', missingLayout.Select(f => (f.Set??"?")+":"+f.Number)));
            }
            Debug.WriteLine($"[Cache] Wrote metadata cache {hash} faces={list.Count}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to write metadata cache: {ex.Message}"); }
    }

    public void MarkCacheComplete(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        try
        {
            File.WriteAllText(MetaCacheDonePath(hash), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to mark cache complete: {ex.Message}"); }
    }

    public async Task ResolveSpecsAsync(
        List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList,
        HashSet<int> targetIndexes,
        Func<int,int> updateCallback,
        Func<int,CardEntry?,(CardEntry? backFace,bool persist)> onCardResolved,
        Func<string,string,string?,Task<CardEntry?>> fetchCard)
    {
        int total = targetIndexes.Count;
        int done = 0;
        var concurrency = new SemaphoreSlim(6);
        var tasks = new List<Task>();
        foreach (var f in fetchList)
        {
            if (!targetIndexes.Contains(f.specIndex)) continue;
            await concurrency.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    CardEntry? resolved = null;
                    // Try per-card cache first
                    if (!TryLoadCardFromCache(f.setCode, f.number, out resolved) || resolved == null)
                    {
                        resolved = await fetchCard(f.setCode, f.number, f.nameOverride);
                        if (resolved != null)
                        {
                            PersistCardToCache(resolved);
                        }
                    }
                    if (resolved != null)
                    {
                        // If MFC and has both faces, synthesize back entry for persistence
                        CardEntry? backFace = null;
                        if (resolved.IsModalDoubleFaced && !string.IsNullOrEmpty(resolved.FrontRaw) && !string.IsNullOrEmpty(resolved.BackRaw))
                        {
                            var backDisplay = $"{resolved.BackRaw} ({resolved.FrontRaw})";
                            backFace = new CardEntry(backDisplay, resolved.Number, resolved.Set, true, true, resolved.FrontRaw, resolved.BackRaw);
                            PersistCardToCache(backFace);
                        }
                        onCardResolved(f.specIndex, resolved);
                        if (backFace != null) onCardResolved(f.specIndex, backFace);
                    }
                    else
                    {
                        onCardResolved(f.specIndex, null);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResolveSpecs] Failed {f.setCode} {f.number}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref done);
                    updateCallback(done);
                    concurrency.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
    }
}

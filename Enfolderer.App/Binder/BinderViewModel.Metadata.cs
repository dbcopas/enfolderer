using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Layout;
using Enfolderer.App.Metadata;

namespace Enfolderer.App;

/// <summary>
/// BinderViewModel partial: Metadata loading, cache management, and card list building.
/// Handles file loading, metadata orchestration, display override recovery, and spec-to-card resolution.
/// </summary>
public partial class BinderViewModel
{
    private const int CacheSchemaVersion = 5; // bump: refined two-sided classification & invalidating prior misclassification cache
    
    private static readonly HashSet<string> PhysicallyTwoSidedLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"
    };
    
    private static readonly HashSet<string> SingleFaceMultiLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "split","aftermath","adventure","meld","flip","leveler","saga","class","plane","planar","scheme","vanguard","token","emblem","art_series"
    };

    // On metadata cache hits we only have raw Numbers. Re-parse binder file just enough to recover DisplayNumber overrides (e.g. parallel ranges A-B&&C-D => A(C)).
    private static void ReapplyDisplayOverridesFromFile(string path, List<CardEntry> cards)
    {
        if (!File.Exists(path) || cards.Count == 0) return;
        var lines = File.ReadAllLines(path);
        string? currentSet = null;
        var overrides = new List<(string set,string primary,string display)>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith("**")) continue;
            if (line.StartsWith('=')) { currentSet = line.Substring(1).Trim(); continue; }
            if (currentSet == null) continue;
            if (!line.Contains("&&")) continue;
            var seg = line.Split(';')[0].Trim(); // ignore optional name override part
            if (!seg.Contains("&&")) continue;
            var pairSegs = seg.Split("&&", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pairSegs.Length != 2) continue;
            static List<string> Expand(string text)
            {
                var list = new List<string>();
                if (string.IsNullOrWhiteSpace(text)) return list;
                if (text.Contains('-'))
                {
                    var parts = text.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e) && s <= e)
                    { for (int n=s;n<=e;n++) list.Add(n.ToString()); return list; }
                }
                if (int.TryParse(text, out int single)) list.Add(single.ToString());
                return list;
            }
            var primList = Expand(pairSegs[0]);
            var secList = Expand(pairSegs[1]);
            if (primList.Count == 0 || primList.Count != secList.Count) continue;
            for (int i=0;i<primList.Count;i++)
            {
                var disp = primList[i] + "(" + secList[i] + ")";
                overrides.Add((currentSet, primList[i], disp));
            }
        }
        if (overrides.Count == 0) return;
        // Apply: find matching card entries by Set & Number (primary) and assign DisplayNumber if not already present
        foreach (var ov in overrides)
        {
            foreach (var idx in Enumerable.Range(0, cards.Count))
            {
                var c = cards[idx];
                if (c.Set != null && c.DisplayNumber == null && string.Equals(c.Set, ov.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, ov.primary, StringComparison.OrdinalIgnoreCase))
                {
                    cards[idx] = c with { DisplayNumber = ov.display };
                }
            }
        }
    }

    // Load binder file (async). Supported lines: comments (#), set headers (=SET), single numbers, ranges start-end, optional ;name overrides.
    public async Task LoadFromFileAsync(string path, bool awaitFullMetadata = false)
    {
        var load = await _binderLoadService.LoadAsync(path, SlotsPerPage);
        Debug.WriteLine($"[Binder] LoadFromFileAsync start path={path}");
        // Auto-load collection databases early (before initial spec build) so enrichment has quantity keys ready
        try
        {
            // Always load from fixed exe directory now.
            _collection.Load(_currentCollectionDir);
            if (Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug)
                Debug.WriteLine($"[Binder] Collection auto-load (exe dir) invoked: loaded={_collection.IsLoaded} qtyKeys={_collection.Quantities.Count} dir={_currentCollectionDir}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Binder] Collection auto-load failed: {ex.Message}"); }
        
        _session.CurrentFileHash = load.FileHash;
        // Ignore binder-derived directory (we always use exe directory now).
        if (load.PagesPerBinderOverride.HasValue) PagesPerBinder = load.PagesPerBinderOverride.Value;
        if (!string.IsNullOrEmpty(load.LayoutModeOverride)) LayoutMode = load.LayoutModeOverride;
        // load.HttpDebugEnabled no-op (deprecated debug flag removed).
        _session.LocalBackImagePath = load.LocalBackImagePath;
        
        _cards.Clear(); _specs.Clear(); _mfcBacks.Clear(); _orderedFaces.Clear(); _pendingExplicitVariantPairs.Clear();
        if (load.CacheHit)
        {
            _cards.AddRange(load.CachedCards);
            // Reconstruct DisplayNumber overrides (e.g., parallel ranges 296(361)) lost in cached metadata (cache only stores raw Number)
            try { ReapplyDisplayOverridesFromFile(path, _cards); } catch (Exception ex) { Debug.WriteLine($"[Binder] ReapplyDisplayOverrides failed: {ex.Message}"); }
            // Perform quantity enrichment on cache hit (mirrors non-cache initial path)
            try { (_quantityService as Quantity.CardQuantityService)?.ApplyAll(_collection, _cards); } catch (Exception ex) { Debug.WriteLine($"[Binder] CacheHit enrichment failed: {ex.Message}"); }
            Status = "Loaded metadata from cache.";
            BuildOrderedFaces(); _nav.ResetIndex(); RebuildViews(); Refresh();
            return;
        }
        
        await _metadataOrchestrator.RunInitialAsync(
            load,
            _currentCollectionDir,
            _specs,
            _mfcBacks,
            _cards,
            _orderedFaces,
            _specs,
            _pendingExplicitVariantPairs,
            s => Status = s,
            () => _collection.IsLoaded,
            _collection,
            RebuildCardListFromSpecs,
            BuildOrderedFaces,
            () => { _nav.ResetIndex(); RebuildViews(); },
            Refresh,
            () => _cachePersistence.Persist(_session.CurrentFileHash!, _cards),
            () => _cachePersistence.MarkComplete(_session.CurrentFileHash!),
            awaitFullMetadata
        );
        Debug.WriteLine("[Binder] LoadFromFileAsync complete");
    }

    private void RebuildCardListFromSpecs()
    {
        var arranger = _arrangementService ?? new Enfolderer.App.Core.Arrangement.CardArrangementService(_variantPairing);
        var (cards, pairMap) = arranger.Build(_specs, _mfcBacks, _pendingExplicitVariantPairs);
        _cards.Clear(); _cards.AddRange(cards);
        _explicitVariantPairKeys.Clear(); foreach (var kv in pairMap) _explicitVariantPairKeys[kv.Key] = kv.Value;
        // Always enrich right after rebuild if collection loaded so UI reflects quantities immediately.
        _quantityCoordinator.EnrichAfterRebuildIfLoaded(_collection, _quantityService, _cards, BuildOrderedFaces, Refresh, Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug);
    }

    private readonly FaceOrderingService _faceOrdering = new();
    private readonly CachePathService _cachePaths = default!;
    private readonly StatusPanelService _statusPanel = default!;
    
    private void BuildOrderedFaces()
    {
        _orderedFaces.Clear();
        if (_cards.Count == 0) return;
        var ordered = _faceOrdering.BuildOrderedFaces(_cards, LayoutMode, SlotsPerPage, ColumnsPerPage, _explicitVariantPairKeys);
        _orderedFaces.AddRange(ordered);
    }
}

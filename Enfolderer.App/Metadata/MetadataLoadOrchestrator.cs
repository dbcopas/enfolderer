using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

using Enfolderer.App.Quantity;
using Enfolderer.App.Collection;
using Enfolderer.App.Binder;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
namespace Enfolderer.App.Metadata;

/// <summary>
/// Orchestrates binder metadata loading: applies overrides, kicks initial resolution, schedules remaining resolution, updates caches.
/// Pushes UI mutations back via provided delegates keeping VM slimmer.
/// </summary>
public class MetadataLoadOrchestrator
{
    private readonly SpecResolutionService _specResolution;
    private readonly CardQuantityService _quantityService;
    private readonly IMetadataProvider _metadataProvider; // higher-level abstraction (future cache ops)

    public MetadataLoadOrchestrator(SpecResolutionService specResolution, CardQuantityService quantityService, IMetadataProvider metadataProvider)
    { _specResolution = specResolution; _quantityService = quantityService; _metadataProvider = metadataProvider; }

    public async Task RunInitialAsync(
        BinderLoadResult load,
        string? collectionDir,
        IList<CardSpec> specs,
        ConcurrentDictionary<int, CardEntry> mfcBacks,
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        List<CardSpec> pendingSpecs,
        IList<(string set,string baseNum,string variantNum)> pendingVariantPairs,
        Action<string> setStatus,
        Func<bool> isCollectionLoaded,
        CardCollectionData collection,
        Action rebuildCardList,
        Action buildOrderedFaces,
        Action rebuildViews,
        Action refresh,
        Action persistCache,
        Action markCacheComplete)
    {
        foreach (var p in load.PendingVariantPairs) pendingVariantPairs.Add(p);
        foreach (var ps in load.Specs)
            pendingSpecs.Add(new CardSpec(ps.SetCode, ps.Number, ps.OverrideName, ps.ExplicitEntry, ps.NumberDisplayOverride){ Resolved = ps.Resolved });

    await _specResolution.ResolveAsync(load.InitialFetchList, load.InitialSpecIndexes, 5, specs as List<CardSpec> ?? new List<CardSpec>(specs), mfcBacks, setStatus);
        // Load collection BEFORE first card list build so automatic initial enrichment (custom/mainDb-only quantities) can occur immediately.
        if (!string.IsNullOrEmpty(collectionDir))
        {
            try { collection.Load(collectionDir); } catch (Exception ex) { Debug.WriteLine($"[Collection] Load failed (db): {ex.Message}"); }
        }
        rebuildCardList();
        if (collection.IsLoaded)
        {
            try { _quantityService.EnrichQuantities(collection, cards); _quantityService.AdjustMfcQuantities(cards); } catch (Exception ex) { Debug.WriteLine($"[Collection] Enrichment failed: {ex.Message}"); }
        }
        setStatus($"Initial load {cards.Count} faces (placeholders included).");
        buildOrderedFaces(); rebuildViews(); refresh();

        _ = Task.Run(async () =>
        {
            var remaining = new HashSet<int>();
            for (int i=0;i<specs.Count;i++) if (!load.InitialSpecIndexes.Contains(i)) remaining.Add(i);
            if (remaining.Count == 0) return;
            await _specResolution.ResolveAsync(load.InitialFetchList, remaining, 15, specs as List<CardSpec> ?? new List<CardSpec>(specs), mfcBacks, setStatus);
            Application.Current.Dispatcher.Invoke(() =>
            {
                rebuildCardList();
                if (collection.IsLoaded) _quantityService.EnrichQuantities(collection, cards);
                if (collection.IsLoaded) _quantityService.AdjustMfcQuantities(cards);
                buildOrderedFaces(); rebuildViews(); refresh();
                setStatus($"All metadata loaded ({cards.Count} faces).");
                persistCache();
                markCacheComplete();
            });
        });
    }
}

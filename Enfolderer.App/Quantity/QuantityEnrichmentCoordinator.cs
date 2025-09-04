using System;
using System.Collections.Generic;
using System.Diagnostics;
using Enfolderer.App.Core.Logging;
using System.Linq;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Quantity;

public class QuantityEnrichmentCoordinator
{
    public void EnrichAfterRebuildIfLoaded(CardCollectionData collection, IQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, Action refresh, bool debug)
    {
        if (!collection.IsLoaded) return;
    var sink = quantityService.LogSink;
    try
    {
            if (debug)
                sink?.Log($"Before enrichment: cards={cards.Count} qtyKeys={collection.Quantities.Count} anyPositive={cards.Any(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
            quantityService.ApplyAll(collection, cards);
            rebuildOrderedFaces();
            refresh();
            if (debug)
                sink?.Log($"After enrichment: positives={cards.Count(c=>c.Quantity>0)} updatedZeroes={cards.Count(c=>c.Quantity==0)} negatives={cards.Count(c=>c.Quantity<0)}", LogCategories.QtyCoordinator);
        }
        catch (Exception ex)
        { sink?.Log($"Enrichment failed: {ex.Message}", LogCategories.QtyCoordinator); }
    }

    public void EnrichFallbackIfNeeded(CardCollectionData collection, IQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, bool debug)
    {
        if (!collection.IsLoaded) return;
        bool needs = false; for (int i=0;i<cards.Count;i++) { if (cards[i].Quantity < 0) { needs = true; break; } }
        if (!needs) return;
    var sink = quantityService.LogSink;
    try
    {
            if (debug)
                sink?.Log($"Fallback trigger: cards={cards.Count} qtyKeys={collection.Quantities.Count} positivesPre={cards.Count(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
            quantityService.ApplyAll(collection, cards);
            rebuildOrderedFaces();
            if (debug)
                sink?.Log($"Fallback after: positives={cards.Count(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
        }
        catch (Exception ex)
        { sink?.Log($"Fallback failed: {ex.Message}", LogCategories.QtyCoordinator); }
    }

    public void LayoutChangeFallback(
        CardCollectionData collection,
        IQuantityService quantityService,
        List<CardEntry> cards,
        Action buildOrderedFaces,
        Action rebuildViews,
        Action refresh,
        bool debug)
    {
        if (!_AnyPositive(cards) && collection.IsLoaded && collection.Quantities.Count > 0)
        {
            var sink = quantityService.LogSink;
            try
            {
                quantityService.ApplyAll(collection, cards);
                buildOrderedFaces();
                rebuildViews();
                refresh();
                if (debug) sink?.Log("Layout fallback enrichment executed", LogCategories.QtyCoordinator);
            }
            catch (Exception ex)
            { sink?.Log($"Layout fallback failed: {ex.Message}", LogCategories.QtyCoordinator); }
        }
    }

    private static bool _AnyPositive(List<CardEntry> cards)
    {
        for (int i=0;i<cards.Count;i++) if (cards[i].Quantity>0) return true; return false;
    }
}

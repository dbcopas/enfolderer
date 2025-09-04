using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Quantity;

public class QuantityEnrichmentCoordinator
{
    public void EnrichAfterRebuildIfLoaded(CardCollectionData collection, CardQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, Action refresh, bool debug)
    {
        if (!collection.IsLoaded) return;
        try
        {
            if (debug)
                Debug.WriteLine($"[QtyCoordinator][Rebuild] Before enrichment: cards={cards.Count} qtyKeys={collection.Quantities.Count} anyPositive={cards.Any(c=>c.Quantity>0)}");
            quantityService.EnrichQuantities(collection, cards);
            quantityService.AdjustMfcQuantities(cards);
            rebuildOrderedFaces();
            refresh();
            if (debug)
                Debug.WriteLine($"[QtyCoordinator][Rebuild] After enrichment: positives={cards.Count(c=>c.Quantity>0)} updatedZeroes={cards.Count(c=>c.Quantity==0)} negatives={cards.Count(c=>c.Quantity<0)}");
        }
        catch (Exception ex)
        { Debug.WriteLine($"[QtyCoordinator][Rebuild] Enrichment failed: {ex.Message}"); }
    }

    public void EnrichFallbackIfNeeded(CardCollectionData collection, CardQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, bool debug)
    {
        if (!collection.IsLoaded) return;
        bool needs = false; for (int i=0;i<cards.Count;i++) { if (cards[i].Quantity < 0) { needs = true; break; } }
        if (!needs) return;
        try
        {
            if (debug)
                Debug.WriteLine($"[QtyCoordinator][Fallback] Trigger: cards={cards.Count} qtyKeys={collection.Quantities.Count} positivesPre={cards.Count(c=>c.Quantity>0)}");
            quantityService.EnrichQuantities(collection, cards);
            quantityService.AdjustMfcQuantities(cards);
            rebuildOrderedFaces();
            if (debug)
                Debug.WriteLine($"[QtyCoordinator][Fallback] After: positives={cards.Count(c=>c.Quantity>0)}");
        }
        catch (Exception ex)
        { Debug.WriteLine($"[QtyCoordinator][Fallback] Failed: {ex.Message}"); }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Enfolderer.App.Core.Logging;
using System.Linq;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Quantity;

public class QuantityEnrichmentCoordinator
{
    public void EnrichAfterRebuildIfLoaded(CardCollectionData collection, CardQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, Action refresh, bool debug)
    {
        if (!collection.IsLoaded) return;
    var logField = typeof(CardQuantityService).GetField("_log", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
    var sink = logField?.GetValue(quantityService) as Enfolderer.App.Core.Abstractions.ILogSink;
    try
    {
            if (debug)
                sink?.Log($"Before enrichment: cards={cards.Count} qtyKeys={collection.Quantities.Count} anyPositive={cards.Any(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
            quantityService.EnrichQuantities(collection, cards);
            quantityService.AdjustMfcQuantities(cards);
            rebuildOrderedFaces();
            refresh();
            if (debug)
                sink?.Log($"After enrichment: positives={cards.Count(c=>c.Quantity>0)} updatedZeroes={cards.Count(c=>c.Quantity==0)} negatives={cards.Count(c=>c.Quantity<0)}", LogCategories.QtyCoordinator);
        }
        catch (Exception ex)
        { sink?.Log($"Enrichment failed: {ex.Message}", LogCategories.QtyCoordinator); }
    }

    public void EnrichFallbackIfNeeded(CardCollectionData collection, CardQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, bool debug)
    {
        if (!collection.IsLoaded) return;
        bool needs = false; for (int i=0;i<cards.Count;i++) { if (cards[i].Quantity < 0) { needs = true; break; } }
        if (!needs) return;
    var logField = typeof(CardQuantityService).GetField("_log", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
    var sink = logField?.GetValue(quantityService) as Enfolderer.App.Core.Abstractions.ILogSink;
    try
    {
            if (debug)
                sink?.Log($"Fallback trigger: cards={cards.Count} qtyKeys={collection.Quantities.Count} positivesPre={cards.Count(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
            quantityService.EnrichQuantities(collection, cards);
            quantityService.AdjustMfcQuantities(cards);
            rebuildOrderedFaces();
            if (debug)
                sink?.Log($"Fallback after: positives={cards.Count(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
        }
        catch (Exception ex)
        { sink?.Log($"Fallback failed: {ex.Message}", LogCategories.QtyCoordinator); }
    }
}

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
    private bool _fallbackSuppressed;
    private string? _lastSignature;
    private int _lastQtyKeyCount;
    private int _lastCardCount;

    private string ComputeSignature(List<CardEntry> cards)
    {
        // Lightweight signature: take first 25 effective numbers and quantities
        int take = Math.Min(25, cards.Count);
        var parts = new System.Text.StringBuilder(take * 8);
        for (int i=0;i<take;i++)
        {
            var c = cards[i];
            parts.Append(c.EffectiveNumber);
            parts.Append(':');
            parts.Append(c.Quantity);
            if (c.PrimaryPairedQuantity.HasValue || c.SecondaryPairedQuantity.HasValue)
            {
                parts.Append('(');
                parts.Append(c.PrimaryPairedQuantity?.ToString() ?? "-");
                parts.Append(',');
                parts.Append(c.SecondaryPairedQuantity?.ToString() ?? "-");
                parts.Append(')');
            }
            parts.Append('|');
        }
        return parts.ToString();
    }
    public void EnrichAfterRebuildIfLoaded(CardCollectionData collection, IQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, Action refresh, bool debug)
    {
        if (!collection.IsLoaded) return;
    var sink = quantityService.LogSink;
    try
    {
            bool hasNegative = cards.Any(c => c.Quantity < 0);
            string sig = ComputeSignature(cards);
            bool skip = !hasNegative && _lastSignature == sig && _lastQtyKeyCount == collection.Quantities.Count && _lastCardCount == cards.Count;
            if (skip)
            {
                if (debug) sink?.Log("Skip enrichment (signature unchanged, no negatives)", LogCategories.QtyCoordinator);
                return;
            }
            if (debug)
                sink?.Log($"Before enrichment: cards={cards.Count} qtyKeys={collection.Quantities.Count} anyPositive={cards.Any(c=>c.Quantity>0)} negatives={hasNegative}", LogCategories.QtyCoordinator);
            quantityService.ApplyAll(collection, cards);
            rebuildOrderedFaces();
            refresh();
            if (debug)
                sink?.Log($"After enrichment: positives={cards.Count(c=>c.Quantity>0)} updatedZeroes={cards.Count(c=>c.Quantity==0)} negatives={cards.Count(c=>c.Quantity<0)}", LogCategories.QtyCoordinator);
            _lastSignature = ComputeSignature(cards);
            _lastQtyKeyCount = collection.Quantities.Count;
            _lastCardCount = cards.Count;
        }
        catch (Exception ex)
        { sink?.Log($"Enrichment failed: {ex.Message}", LogCategories.QtyCoordinator); }
    }

    public void EnrichFallbackIfNeeded(CardCollectionData collection, IQuantityService quantityService, List<CardEntry> cards, Action rebuildOrderedFaces, bool debug)
    {
        if (_fallbackSuppressed) return; // previously failed; avoid repeated exceptions
        if (!collection.IsLoaded) return;
        bool needs = false; for (int i=0;i<cards.Count;i++) { if (cards[i].Quantity < 0) { needs = true; break; } }
        if (!needs) return;
    var sink = quantityService.LogSink;
    try
    {
            if (debug)
                sink?.Log($"Fallback trigger: cards={cards.Count} qtyKeys={collection.Quantities.Count} positivesPre={cards.Count(c=>c.Quantity>0)}", LogCategories.QtyCoordinator);
            quantityService.ApplyAll(collection, cards);
            // Do NOT rebuild ordered faces here; it can race with UI page rendering enumerations and cause InvalidOperationException.
            // Quantities are updated in-place (record copies at indices) which preserves list structure and keeps existing ordering valid.
            if (debug)
                sink?.Log($"Fallback after (no structural rebuild): positives={cards.Count(c=>c.Quantity>0)} negatives={cards.Count(c=>c.Quantity<0)}", LogCategories.QtyCoordinator);
            _lastSignature = ComputeSignature(cards);
            _lastQtyKeyCount = collection.Quantities.Count;
            _lastCardCount = cards.Count;
        }
        catch (Exception ex)
        {
            // Lenient fallback: don't throw; attempt lightweight coercion so UI gains stable x(y) formatting even if full enrichment failed.
            sink?.Log($"Fallback failed: {ex.Message} (entering lenient mode & suppressing further attempts)", LogCategories.QtyCoordinator);
            try
            {
                int coerced = 0, annotated = 0;
                for (int i=0;i<cards.Count;i++)
                {
                    var c = cards[i];
                    if (c.Quantity < 0) { c = c with { Quantity = 0 }; coerced++; }
                    // If EffectiveNumber expresses a pair (e.g., 296(361)) and paired fields are null, annotate as 0(0) to force display formation.
                    if (c.PrimaryPairedQuantity == null && c.SecondaryPairedQuantity == null)
                    {
                        var eff = c.EffectiveNumber;
                        int open = eff.IndexOf('(');
                        if (open > 0 && eff.EndsWith(")") && open < eff.Length-2)
                        {
                            var prim = eff.Substring(0, open);
                            var sec = eff.Substring(open+1, eff.Length-open-2);
                            bool allDigitsPrim = prim.All(char.IsDigit);
                            bool allDigitsSec = sec.All(char.IsDigit);
                            if (allDigitsPrim && allDigitsSec)
                            {
                                c = c with { PrimaryPairedQuantity = 0, SecondaryPairedQuantity = 0 };
                                annotated++;
                            }
                        }
                    }
                    cards[i] = c;
                }
                sink?.Log($"Lenient fallback applied: coercedNegatives={coerced} annotatedPairs={annotated}", LogCategories.QtyCoordinator);
            }
            catch (Exception inner)
            {
                sink?.Log($"Lenient fallback secondary failure: {inner.Message}", LogCategories.QtyCoordinator);
            }
            _fallbackSuppressed = true;
        }
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

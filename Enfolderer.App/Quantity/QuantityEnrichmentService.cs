using System;
using System.Collections.Generic;
using System.Diagnostics;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Quantity;

/// <summary>
/// Thin wrapper around CardQuantityService to encapsulate common enrichment + MFC adjustment sequence with error handling.
/// </summary>
public class QuantityEnrichmentService
{
    private readonly CardQuantityService _quantityService;
    public QuantityEnrichmentService(CardQuantityService quantityService) { _quantityService = quantityService; }

    public void Enrich(CardCollectionData collection, List<CardEntry> cards)
    {
        try
        {
            if (_quantityService is CardQuantityService concrete)
                concrete.ApplyAll(collection, cards);
            else
            {
                _quantityService.EnrichQuantities(collection, cards);
                _quantityService.AdjustMfcQuantities(cards);
            }
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[Collection] Enrichment failed: {ex.Message}");
        }
    }
}

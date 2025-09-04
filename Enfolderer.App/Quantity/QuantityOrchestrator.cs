using System.Collections.Generic;
using Enfolderer.App.Collection;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Core;

namespace Enfolderer.App.Quantity;

public interface IQuantityOrchestrator
{
    void ApplyAll(CardCollectionData collection, List<CardEntry> cards);
}

public sealed class QuantityOrchestrator : IQuantityOrchestrator
{
    private readonly IQuantityService _svc;
    public QuantityOrchestrator(IQuantityService svc) { _svc = svc; }
    public void ApplyAll(CardCollectionData collection, List<CardEntry> cards)
    {
        _svc.EnrichQuantities(collection, cards);
        _svc.AdjustMfcQuantities(cards);
    }
}
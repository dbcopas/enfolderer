using System.Collections.Generic;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction over quantity enrichment / adjustment / toggle logic so higher layers can be decoupled
/// from the concrete implementation and future persistence strategies.
/// </summary>
public interface IQuantityService
{
    void EnrichQuantities(CardCollectionData collection, List<CardEntry> cards);
    void AdjustMfcQuantities(List<CardEntry> cards);
    // Unified convenience operation (enrich + MFC adjust)
    void ApplyAll(CardCollectionData collection, List<CardEntry> cards);
    Enfolderer.App.Core.Abstractions.ILogSink? LogSink { get; }
    int ToggleQuantity(
        CardSlot slot,
        string currentCollectionDir,
        CardCollectionData collection,
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        System.Func<string,string,string,int?> resolveCardIdFromDb,
        System.Action<string> setStatus);
}

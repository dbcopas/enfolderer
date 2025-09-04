using System.Collections.Generic;
using Enfolderer.App.Core;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Narrow abstraction for metadata cache persistence so UI/orchestrator do not depend on full resolver surface.
/// </summary>
public interface IMetadataCachePersistence
{
    bool TryLoad(string hash, List<CardEntry> intoCards);
    void Persist(string hash, List<CardEntry> cards);
    void MarkComplete(string hash);
}

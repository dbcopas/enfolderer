using System.Collections.Generic;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Adapter exposing only cache persistence members of the metadata resolver.
/// </summary>
public sealed class MetadataCachePersistenceAdapter : IMetadataCachePersistence
{
    private readonly ICardMetadataResolver _resolver;
    public MetadataCachePersistenceAdapter(ICardMetadataResolver resolver) { _resolver = resolver; }
    public bool TryLoad(string hash, List<CardEntry> intoCards) => _resolver.TryLoadMetadataCache(hash, intoCards);
    public void Persist(string hash, List<CardEntry> cards) => _resolver.PersistMetadataCache(hash, cards);
    public void MarkComplete(string hash) => _resolver.MarkCacheComplete(hash);
}

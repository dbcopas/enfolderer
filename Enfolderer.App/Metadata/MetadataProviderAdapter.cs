using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Adapter exposing CardMetadataResolver as IMetadataProvider.
/// </summary>
public sealed class MetadataProviderAdapter : IMetadataProvider
{
    private readonly ICardMetadataResolver _resolver;
    public MetadataProviderAdapter(ICardMetadataResolver resolver) { _resolver = resolver; }
    public bool TryLoadMetadata(string hash, List<CardEntry> intoCards) => _resolver.TryLoadMetadataCache(hash, intoCards);
    public void PersistMetadata(string? hash, List<CardEntry> cards) => _resolver.PersistMetadataCache(hash, cards);
    public void MarkComplete(string? hash) => _resolver.MarkCacheComplete(hash);
    public Task ResolveSpecsAsync(List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList, HashSet<int> targetIndexes, Func<int,int> updateCallback, Func<int,CardEntry?,(CardEntry? backFace,bool persist)> onCardResolved, Func<string,string,string?,Task<CardEntry?>> fetchCard)
        => _resolver.ResolveSpecsAsync(fetchList, targetIndexes, updateCallback, onCardResolved, fetchCard);
}

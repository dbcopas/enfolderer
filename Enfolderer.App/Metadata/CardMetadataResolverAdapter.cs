using System.Collections.Generic;
using System.Threading.Tasks;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Adapter for CardMetadataResolver -> ICardMetadataResolver.
/// </summary>
public sealed class CardMetadataResolverLowLevelAdapter : ICardMetadataResolver
{
    private readonly CardMetadataResolver _inner;
    public CardMetadataResolver Inner => _inner; // exposed for transitional consumers needing concrete features
    public CardMetadataResolverLowLevelAdapter(CardMetadataResolver inner) { _inner = inner; }
    public bool TryLoadMetadataCache(string hash, List<CardEntry> intoCards) => _inner.TryLoadMetadataCache(hash, intoCards);
    public void PersistMetadataCache(string? hash, List<CardEntry> cards) => _inner.PersistMetadataCache(hash, cards);
    public void MarkCacheComplete(string? hash) => _inner.MarkCacheComplete(hash);
    public bool TryLoadCardFromCache(string setCode, string number, out CardEntry? entry) => _inner.TryLoadCardFromCache(setCode, number, out entry);
    public void PersistCardToCache(CardEntry ce) => _inner.PersistCardToCache(ce);
    public Task ResolveSpecsAsync(List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList, HashSet<int> targetIndexes, System.Func<int,int> updateCallback, System.Func<int,CardEntry?,(CardEntry? backFace,bool persist)> onCardResolved, System.Func<string,string,string?,Task<CardEntry?>> fetchCard)
        => _inner.ResolveSpecsAsync(fetchList, targetIndexes, updateCallback, onCardResolved, fetchCard);
}

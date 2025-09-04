using System.Collections.Generic;
using System.Threading.Tasks;
using Enfolderer.App.Core;
using Enfolderer.App.Metadata;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction for resolving card metadata (fetching + caching).
/// </summary>
public interface ICardMetadataResolver
{
    bool TryLoadMetadataCache(string hash, List<CardEntry> intoCards);
    void PersistMetadataCache(string? hash, List<CardEntry> cards);
    void MarkCacheComplete(string? hash);
    bool TryLoadCardFromCache(string setCode, string number, out CardEntry? entry);
    void PersistCardToCache(CardEntry ce);
    Task ResolveSpecsAsync(
        List<Enfolderer.App.Metadata.FetchSpec> fetchList,
        System.Collections.Generic.HashSet<int> targetIndexes,
        System.Func<int,int> updateCallback,
        System.Func<int,CardEntry?,(CardEntry? backFace,bool persist)> onCardResolved,
        System.Func<string,string,string?,Task<CardEntry?>> fetchCard);
}

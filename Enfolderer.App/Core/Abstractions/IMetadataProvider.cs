using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enfolderer.App.Core;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Higher-level metadata provider abstraction (cache + multi-spec resolution) distinct from low-level resolver.
/// This narrows the surface BinderViewModel needs and allows swapping backing resolver later.
/// </summary>
public interface IMetadataProvider
{
    bool TryLoadMetadata(string hash, List<CardEntry> intoCards);
    void PersistMetadata(string? hash, List<CardEntry> cards);
    void MarkComplete(string? hash);
    Task ResolveSpecsAsync(List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList,
        HashSet<int> targetIndexes,
        Func<int,int> updateCallback,
        Func<int,CardEntry?,(CardEntry? backFace,bool persist)> onCardResolved,
        Func<string,string,string?,Task<CardEntry?>> fetchCard);
}

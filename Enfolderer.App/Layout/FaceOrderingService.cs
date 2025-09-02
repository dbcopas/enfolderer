using System.Collections.Generic;
using Enfolderer.App.Core;

namespace Enfolderer.App.Layout;

/// <summary>
/// Provides a reusable ordering of card faces (front/back + variant grouping) so the view model does not new services repeatedly.
/// </summary>
public class FaceOrderingService
{
    private readonly FaceLayoutService _layoutService;

    public FaceOrderingService() : this(new FaceLayoutService(new PairGroupingAnalyzer())) { }
    public FaceOrderingService(FaceLayoutService layoutService) { _layoutService = layoutService; }

    public IList<CardEntry> BuildOrderedFaces(IReadOnlyList<CardEntry> cards, string layoutMode, int slotsPerPage, int columnsPerPage, IReadOnlyDictionary<string,string> explicitVariantPairKeys)
    {
        var list = cards as List<CardEntry> ?? new List<CardEntry>(cards);
        var pairMap = new Dictionary<CardEntry,string>();
        if (explicitVariantPairKeys.Count>0)
        {
            foreach (var c in list)
            {
                var keyCandidate = (c.Set ?? "") + ":" + c.Number;
                if (explicitVariantPairKeys.TryGetValue(keyCandidate, out var key))
                    pairMap[c] = key;
            }
        }
        return _layoutService.BuildOrderedFaces(list, layoutMode, slotsPerPage, columnsPerPage, pairMap);
    }
}

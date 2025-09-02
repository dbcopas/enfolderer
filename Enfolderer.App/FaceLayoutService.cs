using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Enfolderer.App;

public class FaceLayoutService
{
    private readonly PairGroupingAnalyzer _pairAnalyzer;
    public FaceLayoutService(PairGroupingAnalyzer? pairAnalyzer = null)
    {
        _pairAnalyzer = pairAnalyzer ?? new PairGroupingAnalyzer();
    }

    public List<CardEntry> BuildOrderedFaces(
        List<CardEntry> cards,
        string layoutMode,
        int slotsPerPage,
        int columnsPerPage,
        Dictionary<CardEntry,string> explicitPairKeys)
    {
        var ordered = new List<CardEntry>();
        if (cards.Count == 0) return ordered;
        if (string.Equals(layoutMode, "3x3", StringComparison.OrdinalIgnoreCase))
        {
            ordered.AddRange(cards);
            return ordered;
        }
        var longRunCards = _pairAnalyzer.IdentifyLongRunCards(cards);
        var remaining = new List<CardEntry>(cards);
        int globalSlot = 0;
        while (remaining.Count > 0)
        {
            int col = (globalSlot % slotsPerPage) % columnsPerPage;
            int groupSize = _pairAnalyzer.IsPairStart(remaining, 0, longRunCards, explicitPairKeys) ? 2 : 1;
            if (groupSize == 2)
            {
                if (col % 2 == 1 || col == columnsPerPage -1)
                {
                    int singleIndex = -1;
                    for (int i = 1; i < remaining.Count; i++)
                    {
                        var cand = remaining[i];
                        bool isBackPlaceholder = string.Equals(cand.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                        if (isBackPlaceholder) continue;
                        if (!_pairAnalyzer.IsPairStart(remaining, i, longRunCards, explicitPairKeys) && !_pairAnalyzer.IsSecondOfPair(remaining, i, longRunCards, explicitPairKeys) && !cand.IsBackFace)
                        {
                            singleIndex = i;
                            break;
                        }
                    }
                    if (singleIndex != -1)
                    {
                        ordered.Add(remaining[singleIndex]);
                        remaining.RemoveAt(singleIndex);
                        globalSlot++;
                        continue;
                    }
                }
            }

            if (groupSize == 1)
            {
                if (remaining[0] == null)
                {
                    Debug.WriteLine("[BuildOrderedFaces] Encountered null single at head; skipping.");
                    remaining.RemoveAt(0);
                    continue;
                }
                ordered.Add(remaining[0]);
                remaining.RemoveAt(0);
                globalSlot++;
            }
            else
            {
                if (remaining[0] == null || remaining[1] == null)
                {
                    Debug.WriteLine("[BuildOrderedFaces] Encountered null within pair; downgrading to single placement.");
                    if (remaining[0] != null) { ordered.Add(remaining[0]); }
                    remaining.RemoveAt(0);
                    globalSlot++;
                    continue;
                }
                ordered.Add(remaining[0]);
                ordered.Add(remaining[1]);
                remaining.RemoveRange(0, 2);
                globalSlot += 2;
            }
        }
        return ordered;
    }
}

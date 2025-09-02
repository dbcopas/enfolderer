using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Enfolderer.App.Core;

/// <summary>
/// Encapsulates heuristics for determining when two consecutive CardEntry items
/// should be treated as an enforced side-by-side pair in a page layout.
/// </summary>
public class PairGroupingAnalyzer
{
    public HashSet<CardEntry> IdentifyLongRunCards(List<CardEntry> cards)
    {
        var longRunCards = new HashSet<CardEntry>();
        int i = 0;
        while (i < cards.Count)
        {
            var c = cards[i];
            if (c != null && !c.IsModalDoubleFaced && !c.IsBackFace)
            {
                string name = (c.Name ?? string.Empty).Trim();
                int j = i + 1;
                while (j < cards.Count)
                {
                    var n = cards[j];
                    if (n == null || n.IsModalDoubleFaced || n.IsBackFace) break;
                    if (!string.Equals(name, (n.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) break;
                    j++;
                }
                int runLen = j - i;
                if (runLen >= 3)
                {
                    for (int k = i; k < j; k++) longRunCards.Add(cards[k]);
                }
                i = j;
                continue;
            }
            i++;
        }
        return longRunCards;
    }

    private static bool IsNazgul(CardEntry ce) => string.Equals(ce.Name?.Trim(), "Nazgûl", StringComparison.OrdinalIgnoreCase);
    private static bool IsBackPlaceholder(CardEntry ce) => string.Equals(ce.Number, "BACK", StringComparison.OrdinalIgnoreCase);

    public bool IsPairStart(List<CardEntry> list, int idx, HashSet<CardEntry> longRunCards, Dictionary<CardEntry,string> explicitPairKeys)
    {
        if (idx < 0 || idx >= list.Count) return false;
        var c = list[idx];
        if (c == null)
        {
            Debug.WriteLine($"[PairGrouping] Null entry at index {idx}; treating as non-pair start.");
            return false;
        }
        if (c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
        {
            var next = list[idx + 1];
            if (next != null && next.IsBackFace) return true;
        }
        if (explicitPairKeys.Count > 0 && idx + 1 < list.Count)
        {
            if (explicitPairKeys.TryGetValue(c, out var key1))
            {
                var n2 = list[idx + 1];
                if (n2 != null && explicitPairKeys.TryGetValue(n2, out var key2) && key1 == key2) return true;
            }
        }
        if (!c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
        {
            var n = list[idx + 1];
            if (n != null && !n.IsModalDoubleFaced && !n.IsBackFace)
            {
                if (IsBackPlaceholder(c) && IsBackPlaceholder(n)) return false;
                if (IsNazgul(c) && IsNazgul(n)) return false;
                var cName = c.Name ?? string.Empty;
                var nName = n.Name ?? string.Empty;
                if (string.Equals(cName.Trim(), nName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    if (longRunCards.Contains(c) || longRunCards.Contains(n)) return false;
                    if (idx + 2 < list.Count)
                    {
                        var third = list[idx + 2];
                        if (third != null && !third.IsModalDoubleFaced && !third.IsBackFace)
                        {
                            var tName = third.Name ?? string.Empty;
                            if (string.Equals(cName.Trim(), tName.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                        }
                    }
                    return true;
                }
            }
        }
        return false;
    }

    public bool IsSecondOfPair(List<CardEntry> list, int idx, HashSet<CardEntry> longRunCards, Dictionary<CardEntry,string> explicitPairKeys)
    {
        if (idx <= 0 || idx >= list.Count) return false;
        var c = list[idx];
        if (c == null)
        {
            Debug.WriteLine($"[PairGrouping] Null entry at index {idx}; treating as non-second.");
            return false;
        }
        if (c.IsBackFace) return true;
        if (explicitPairKeys.Count > 0)
        {
            var prevExp = list[idx - 1];
            if (prevExp != null && explicitPairKeys.TryGetValue(prevExp, out var pk1) && explicitPairKeys.TryGetValue(c, out var pk2) && pk1 == pk2)
                return true;
        }
        var prev = list[idx - 1];
        if (prev != null && !prev.IsModalDoubleFaced && !prev.IsBackFace && !c.IsModalDoubleFaced && !c.IsBackFace)
        {
            if (IsBackPlaceholder(prev) && IsBackPlaceholder(c)) return false;
            if (IsNazgul(prev) && IsNazgul(c)) return false;
            var prevName = prev.Name ?? string.Empty;
            var cName = c.Name ?? string.Empty;
            if (string.Equals(prevName.Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (longRunCards.Contains(prev) || longRunCards.Contains(c)) return false;
                bool hasPrevPrev = idx - 2 >= 0 && list[idx - 2] != null && !list[idx - 2].IsModalDoubleFaced && !list[idx - 2].IsBackFace && string.Equals((list[idx-2].Name??string.Empty).Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase);
                bool hasNext = idx + 1 < list.Count && list[idx + 1] != null && !list[idx + 1].IsModalDoubleFaced && !list[idx + 1].IsBackFace && string.Equals((list[idx+1].Name??string.Empty).Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase);
                if (hasPrevPrev || hasNext) return false;
                return true;
            }
        }
        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Enfolderer.App;

public class FaceLayoutService
{
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
        var longRunCards = new HashSet<CardEntry>();
        int iRun = 0;
        while (iRun < cards.Count)
        {
            var c = cards[iRun];
            if (c != null && !c.IsModalDoubleFaced && !c.IsBackFace)
            {
                string name = (c.Name ?? string.Empty).Trim();
                int j = iRun + 1;
                while (j < cards.Count)
                {
                    var n = cards[j];
                    if (n == null || n.IsModalDoubleFaced || n.IsBackFace) break;
                    if (!string.Equals(name, (n.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) break;
                    j++;
                }
                int runLen = j - iRun;
                if (runLen >= 3)
                {
                    for (int k = iRun; k < j; k++) longRunCards.Add(cards[k]);
                }
                iRun = j;
                continue;
            }
            iRun++;
        }
        var remaining = new List<CardEntry>(cards);
        int globalSlot = 0;
        while (remaining.Count > 0)
        {
            int col = (globalSlot % slotsPerPage) % columnsPerPage;

            bool IsPairStart(List<CardEntry> list, int idx)
            {
                if (idx < 0 || idx >= list.Count) return false;
                var c = list[idx];
                if (c == null)
                {
                    Debug.WriteLine($"[BuildOrderedFaces] Null entry at index {idx} in remaining list (IsPairStart). Treating as single.");
                    return false;
                }
                bool IsNazgul(CardEntry ce) => string.Equals(ce.Name?.Trim(), "Nazgûl", StringComparison.OrdinalIgnoreCase);
                bool IsBackPlaceholder(CardEntry ce) => string.Equals(ce.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                if (c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
                {
                    var next = list[idx + 1];
                    if (next != null && next.IsBackFace) return true;
                }
                if (explicitPairKeys.Count > 0)
                {
                    if (idx + 1 < list.Count && explicitPairKeys.TryGetValue(c, out var key1))
                    {
                        var n2 = list[idx + 1];
                        if (n2 != null && explicitPairKeys.TryGetValue(n2, out var key2) && key1 == key2)
                        {
                            return true;
                        }
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
                                    if (string.Equals(cName.Trim(), tName.Trim(), StringComparison.OrdinalIgnoreCase))
                                        return false;
                                }
                            }
                            return true;
                        }
                    }
                }
                return false;
            }

            bool IsSecondOfPair(List<CardEntry> list, int idx)
            {
                if (idx <= 0 || idx >= list.Count) return false;
                var c = list[idx];
                if (c == null)
                {
                    Debug.WriteLine($"[BuildOrderedFaces] Null entry at index {idx} in remaining list (IsSecondOfPair). Treating as single.");
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
                    bool IsBackPlaceholder(CardEntry ce) => string.Equals(ce.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                    bool IsNazgul(CardEntry ce) => string.Equals(ce.Name?.Trim(), "Nazgûl", StringComparison.OrdinalIgnoreCase);
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

            int groupSize = IsPairStart(remaining, 0) ? 2 : 1;
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
                        if (!IsPairStart(remaining, i) && !IsSecondOfPair(remaining, i) && !cand.IsBackFace)
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

using System;
using System.Collections.Generic;
using System.Linq;

namespace Enfolderer.App;

/// <summary>
/// Builds explicit variant pairing keys so ordering can keep paired variants adjacent.
/// Responsibility extracted from MainWindow to reduce UI class complexity.
/// </summary>
public class VariantPairingService
{
    /// <summary>
    /// Produce a dictionary mapping each card that participates in an explicit variant pair
    /// (base + variant) to a stable key string. Both sides of a pair share the same key.
    /// </summary>
    public Dictionary<CardEntry,string> BuildExplicitPairKeyMap(
        IList<CardEntry> allCards,
        IEnumerable<(string set,string baseNum,string variantNum)> pendingPairs)
    {
        var map = new Dictionary<CardEntry,string>();
        if (allCards.Count == 0) return map;
        foreach (var tup in pendingPairs)
        {
            if (string.IsNullOrWhiteSpace(tup.set) || string.IsNullOrWhiteSpace(tup.baseNum) || string.IsNullOrWhiteSpace(tup.variantNum))
                continue;
            CardEntry? baseEntry = allCards.FirstOrDefault(c => string.Equals(c.Set, tup.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, tup.baseNum, StringComparison.OrdinalIgnoreCase));
            CardEntry? varEntry  = allCards.FirstOrDefault(c => string.Equals(c.Set, tup.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, tup.variantNum, StringComparison.OrdinalIgnoreCase));
            if (baseEntry != null && varEntry != null)
            {
                string key = $"{tup.set.ToLowerInvariant()}|{tup.baseNum.ToLowerInvariant()}|{tup.variantNum.ToLowerInvariant()}";
                map[baseEntry] = key;
                map[varEntry]  = key;
            }
        }
        return map;
    }
}

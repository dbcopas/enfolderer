using System;
using System.Collections.Generic;

namespace Enfolderer.App.Core;

/// <summary>
/// Builds the in-memory card face list from specs + synthesized backs + pending variant pairs.
/// </summary>
public class CardListBuilder
{
    private readonly VariantPairingService _variantPairing;
    public CardListBuilder(VariantPairingService variantPairing) => _variantPairing = variantPairing;

    public (List<CardEntry> cards, Dictionary<string,string> explicitVariantPairKeys) Build(
        IList<CardSpec> specs,
        IReadOnlyDictionary<int, CardEntry> mfcBacks,
        IList<(string set,string baseNum,string variantNum)> pendingExplicitVariantPairs)
    {
        var cards = new List<CardEntry>();
        var variantMap = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (int i=0;i<specs.Count;i++)
        {
            var s = specs[i];
            bool hasFaceOverride = !string.IsNullOrEmpty(s.faceOverride);
            bool showBackOnly = string.Equals(s.faceOverride, "SB", StringComparison.OrdinalIgnoreCase);
            bool showFrontOnly = string.Equals(s.faceOverride, "SF", StringComparison.OrdinalIgnoreCase);

            if (showBackOnly && mfcBacks.TryGetValue(i, out var backForSB))
            {
                // SB: use the back face as the sole entry; skip front entirely
                cards.Add(backForSB);
                continue; // do NOT also append the back below
            }

            if (s.Resolved != null)
            {
                var resolved = s.Resolved;
                if (s.numberDisplayOverride != null && resolved.DisplayNumber != s.numberDisplayOverride)
                    resolved = resolved with { DisplayNumber = s.numberDisplayOverride };
                cards.Add(resolved);
            }
            else
            {
                var placeholderName = s.overrideName ?? s.number;
                var displayNumber = s.numberDisplayOverride;
                cards.Add(new CardEntry(placeholderName, s.number, s.setCode, false, false, null, null, displayNumber));
            }

            // SF: show front only — skip the MFC back face slot
            if (showFrontOnly)
                continue;

            if (mfcBacks.TryGetValue(i, out var back)) cards.Add(back);
        }
        var built = _variantPairing.BuildExplicitPairKeyMap(cards, pendingExplicitVariantPairs);
        foreach (var kv in built)
        {
            var key = (kv.Key.Set ?? "") + ":" + kv.Key.Number;
            variantMap[key] = kv.Value;
        }
        return (cards, variantMap);
    }
}

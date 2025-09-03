using System.Collections.Generic;
using Enfolderer.App.Core;

namespace Enfolderer.App.Tests;

public static class VariantPairingServiceTests
{
    public static int RunAll()
    {
        int failures = 0;
    void Check(bool c) { if (!c) failures++; }

        var cards = new List<CardEntry>
        {
            new CardEntry("Alpha", "1", "SET", false),
            new CardEntry("Alpha Alt", "1a", "SET", false),
            new CardEntry("Beta", "2", "SET", false)
        };
        var pending = new List<(string set,string baseNum,string variantNum)>{ ("SET","1","1a") };
        var svc = new VariantPairingService();
        var map = svc.BuildExplicitPairKeyMap(cards, pending);
    Check(map.Count == 2);
    Check(map[cards[0]] == map[cards[1]]);

        // Negative: variant not found
        var pending2 = new List<(string,string,string)>{ ("SET","1","9z") };
        var map2 = svc.BuildExplicitPairKeyMap(cards, pending2);
    Check(map2.Count == 0);

        return failures;
    }
}

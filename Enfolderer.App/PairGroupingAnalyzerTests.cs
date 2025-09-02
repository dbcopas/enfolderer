using System.Collections.Generic;
using Enfolderer.App.Core;

namespace Enfolderer.App.Tests;

public static class PairGroupingAnalyzerTests
{
    public static int RunAll()
    {
        int failures = 0;
        void Check(bool c) { if (!c) failures++; }
        var analyzer = new PairGroupingAnalyzer();

        // Two-of-a-kind should pair
        var two = new List<CardEntry>
        {
            new CardEntry("Goblin", "1", "A", false),
            new CardEntry("Goblin", "2", "A", false),
            new CardEntry("Elf", "3", "A", false)
        };
        var longRun2 = analyzer.IdentifyLongRunCards(two);
        var expKeysEmpty = new Dictionary<CardEntry,string>();
    Check(analyzer.IsPairStart(two,0,longRun2,expKeysEmpty));
    Check(analyzer.IsSecondOfPair(two,1,longRun2,expKeysEmpty));

        // Three-of-a-kind should not pair (long run exclusion)
        var three = new List<CardEntry>
        {
            new CardEntry("Orc", "1", "A", false),
            new CardEntry("Orc", "2", "A", false),
            new CardEntry("Orc", "3", "A", false)
        };
        var longRun3 = analyzer.IdentifyLongRunCards(three);
    Check(longRun3.Count == 3);
    Check(!analyzer.IsPairStart(three,0,longRun3,expKeysEmpty));

        // Explicit pairing overrides name mismatch
        var explicitList = new List<CardEntry>
        {
            new CardEntry("Dragon A", "10", "S", false),
            new CardEntry("Dragon B", "11", "S", false)
        };
        var expKeys = new Dictionary<CardEntry,string>{{ explicitList[0], "k"},{ explicitList[1], "k"}};
        var longRunExp = analyzer.IdentifyLongRunCards(explicitList);
    Check(analyzer.IsPairStart(explicitList,0,longRunExp,expKeys));
    Check(analyzer.IsSecondOfPair(explicitList,1,longRunExp,expKeys));

        return failures;
    }
}

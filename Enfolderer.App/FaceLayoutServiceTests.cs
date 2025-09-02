using System.Collections.Generic;

namespace Enfolderer.App.Tests;

public static class FaceLayoutServiceTests
{
    public static int RunAll()
    {
        int failures = 0;
        void Check(bool c) { if (!c) failures++; }
        var analyzer = new PairGroupingAnalyzer();
        var svc = new FaceLayoutService(analyzer);

        var cards = new List<CardEntry>
        {
            new CardEntry("Goblin", "1", "S", false),
            new CardEntry("Goblin", "2", "S", false),
            new CardEntry("Angel", "3", "S", false),
        };
        var ordered = svc.BuildOrderedFaces(cards, "4x3", 12, 4, new System.Collections.Generic.Dictionary<CardEntry,string>());
        // Expect the first two goblins remain adjacent
        int idx1 = ordered.FindIndex(c => c.Number == "1");
        int idx2 = ordered.FindIndex(c => c.Number == "2");
    Check(idx1 != -1 && idx2 == idx1 + 1);

        // Explicit pair with different names
        var mixed = new List<CardEntry>
        {
            new CardEntry("Alpha", "10", "S", false),
            new CardEntry("Beta", "11", "S", false),
            new CardEntry("Gamma", "12", "S", false),
        };
        var exp = new Dictionary<CardEntry,string>{{ mixed[0],"p"},{mixed[1],"p"}};
        var ordered2 = svc.BuildOrderedFaces(mixed, "4x3", 12, 4, exp);
        int a = ordered2.FindIndex(c => c.Number == "10");
        int b = ordered2.FindIndex(c => c.Number == "11");
    Check(a != -1 && b == a + 1);

        // Layout 3x3 should be identity
        var identity = svc.BuildOrderedFaces(mixed, "3x3", 9, 3, exp);
    Check(identity.Count == mixed.Count && identity[0] == mixed[0] && identity[1] == mixed[1]);

        return failures;
    }
}

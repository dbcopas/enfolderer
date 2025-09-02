using System;
using Enfolderer.App.Core;

namespace Enfolderer.App.Tests;

public static class CardEntryTests
{
    public static int RunAll()
    {
        int failures = 0;
        void Check(bool condition, string name)
        { if (!condition) failures++; }

        // Basic parse
        var c1 = CardEntry.FromCsv("Lightning Bolt;123;SET");
        Check(c1.Name == "Lightning Bolt", "Name basic");
        Check(c1.Number == "123", "Number basic");
        Check(c1.Set == "SET", "Set basic");
        Check(!c1.IsModalDoubleFaced, "Basic not MDFC");

        // MDFC via marker suffix
        var c2 = CardEntry.FromCsv("Front Name/Back Name|MFC;007;XYZ");
        Check(c2.IsModalDoubleFaced, "MDFC marker");
        Check(c2.FrontRaw == "Front Name" && c2.BackRaw == "Back Name", "Front/back extracted");
        Check(c2.Name.Contains("("), "Display naming contains paren");

        // MDFC via trailing field
        var c3 = CardEntry.FromCsv("Some Card;045;ABC;MFC");
        Check(c3.IsModalDoubleFaced, "MDFC trailing field");

        // Invalid formats
        bool threw = false;
        try { CardEntry.FromCsv(""); } catch { threw = true; }
        Check(threw, "Empty line throws");

        threw = false;
        try { CardEntry.FromCsv("OnlyOneField"); } catch { threw = true; }
        Check(threw, "Insufficient fields throws");

        return failures;
    }
}

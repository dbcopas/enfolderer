namespace Enfolderer.App.Tests;

/// <summary>
/// Aggregates all lightweight self-test harnesses. Each individual test class returns the number of failed checks.
/// </summary>
public static class SelfTests
{
    public static int RunAll()
    {
        int failures = 0;
        failures += LayoutConfigServiceTests.RunAll();
        failures += CardEntryTests.RunAll();
        failures += VariantPairingServiceTests.RunAll();
        failures += PairGroupingAnalyzerTests.RunAll();
        failures += FaceLayoutServiceTests.RunAll();
        return failures;
    }
}

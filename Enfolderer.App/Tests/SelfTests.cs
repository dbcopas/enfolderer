using Enfolderer.App.Layout;
using System.IO;
namespace Enfolderer.App.Tests;

/// <summary>
/// Aggregates all lightweight self-test harnesses. Each individual test class returns the number of failed checks.
/// </summary>
public static class SelfTests
{
    public static int RunAll()
    {
        int failures = 0;
    string progressLog = Path.Combine(Path.GetTempPath(), "enfolderer_selftests_progress.txt");
    try { File.WriteAllText(progressLog, "START\n"); } catch {}
    try { Enfolderer.App.Infrastructure.AppRuntimeFlags.DisableImageFetching = true; } catch {}
    int suiteFails;
    suiteFails = LayoutConfigServiceTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After LayoutConfig fails={suiteFails}\n"); } catch {}
    suiteFails = CardEntryTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After CardEntry fails={suiteFails}\n"); } catch {}
    suiteFails = VariantPairingServiceTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After VariantPairing fails={suiteFails}\n"); } catch {}
    suiteFails = PairGroupingAnalyzerTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After PairGrouping fails={suiteFails}\n"); } catch {}
    suiteFails = FaceLayoutServiceTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After FaceLayout fails={suiteFails}\n"); } catch {}
    suiteFails = NavigationServiceTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After Navigation fails={suiteFails}\n"); } catch {}
    suiteFails = PageSlotBuilderTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After PageSlotBuilder fails={suiteFails}\n"); } catch {}
    suiteFails = PageViewPresenterTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After PageViewPresenter fails={suiteFails}\n"); } catch {}
    suiteFails = CommandFactoryTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After CommandFactory fails={suiteFails}\n"); } catch {}
    suiteFails = QuantityServicesTests.RunAll(); failures += suiteFails; try { File.AppendAllText(progressLog, $"After QuantityServices fails={suiteFails}\n"); } catch {}
    // Binder parsing characterization (now using Task.Run inside test to avoid dispatcher deadlock).
    int binderFails = 0;
    try { binderFails = BinderParsingCharTests.RunAll(); } catch(Exception ex) { binderFails = 1; try { File.AppendAllText(progressLog, "BinderParsing EX: "+ex.GetType().Name+" "+ex.Message+"\n"); } catch {} }
    failures += binderFails;
    try { File.AppendAllText(progressLog, $"After BinderParsing fails={binderFails}\n"); } catch {}
    try { File.AppendAllText(progressLog, $"DONE failures={failures}\n"); } catch {}
        return failures;
    }
}

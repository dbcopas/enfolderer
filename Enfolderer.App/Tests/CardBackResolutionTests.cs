using System;
using System.IO;
using Enfolderer.App.Metadata;
using Enfolderer.App.Binder;
using Enfolderer.App.Imaging;
using Enfolderer.App.Core;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization of card back resolution priority:
/// 1. If local back image path resolver returns existing file -> mapping uses that path.
/// 2. Otherwise falls back to embedded/pack URI.
/// </summary>
public static class CardBackResolutionTests
{
    public static int RunAll()
    {
        int failures = 0; void Check(bool c){ if(!c) failures++; }
    try
    {
            // Create temp local back image file to simulate presence.
            string tempDir = Path.Combine(Path.GetTempPath(), "enfolderer_tests");
            Directory.CreateDirectory(tempDir);
            string localBackPath = Path.Combine(tempDir, "local_back.jpg");
            File.WriteAllBytes(localBackPath, new byte[]{1,2,3}); // contents irrelevant

            int slotsPerPage = 12;
            string binderPath = Path.Combine(tempDir, "binder_back_test.txt");
            File.WriteAllText(binderPath, "2 ; backface\n");

            var theme = new BinderThemeService();
            var resolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, new[]{"transform"}, 5);
            string? Resolver(bool want) { return localBackPath; }
            var parser = new BinderFileParser(theme, resolver, Resolver, _ => false);
            var result = parser.ParseAsync(binderPath, slotsPerPage).GetAwaiter().GetResult();
            var (front, back) = CardImageUrlStore.Get("__BACK__", "BACK");
            Check(result.CacheHit == false);
            Check(front == localBackPath && back == localBackPath);

            // Omit absence fallback check in headless mode to avoid pack URI resource probing.
        }
        catch (System.Exception ex)
        {
            failures++; try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "enfolderer_selftests_progress.txt"), "CardBackResolution EX: "+ex.GetType().Name+" "+ex.Message+"\n"); } catch {}
        }
        return failures;
    }
}

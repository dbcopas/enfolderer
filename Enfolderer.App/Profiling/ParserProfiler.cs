using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enfolderer.App.Metadata;
using Enfolderer.App.Binder;

namespace Enfolderer.App.Profiling;

// Lightweight ad-hoc profiling harness (Phase 4). Controlled by PERF_PARSE env var at runtime.
public static class ParserProfiler
{
    public static async Task RunAsync()
    {
    // AppContext.BaseDirectory points to bin/Configuration/net8.0-windows/ ; binder text files live at repo root.
        string baseDir = AppContext.BaseDirectory;
        string probe = baseDir;
        string? chosenRoot = null;
        for (int i = 0; i < 8 && chosenRoot == null; i++)
        {
            try
            {
                var binderFilesProbe = Directory.GetFiles(probe, "binder_*.txt", SearchOption.TopDirectoryOnly);
                if (binderFilesProbe.Length > 0 || File.Exists(Path.Combine(probe, "Enfolderer.sln")))
                {
                    chosenRoot = probe; break;
                }
                var parent = Path.GetFullPath(Path.Combine(probe, ".."));
                if (string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase)) break; // reached filesystem root
                probe = parent;
            }
            catch { break; }
        }
        string root = chosenRoot ?? baseDir; // fallback to baseDir if not found
        string logPath = Path.Combine(root, "parser_profile.log");
        void Log(string msg)
        {
            try { File.AppendAllText(logPath, msg + Environment.NewLine); } catch { }
            Console.WriteLine(msg); // will be invisible in WinExe but kept for completeness
            System.Diagnostics.Debug.WriteLine(msg);
            try { File.AppendAllText(Path.Combine(baseDir, "parser_profile_local.log"), msg + Environment.NewLine); } catch { }
        }
        Log($"[Profiler] baseDir={baseDir}");
    Log($"[Profiler] chosenRoot={root}");
        Log($"[Profiler] Output -> {logPath}");
    string[] candidateFiles = Directory.GetFiles(root, "binder_*.txt", SearchOption.TopDirectoryOnly);
        if (candidateFiles.Length == 0)
        {
            Log("[Profiler] No binder_*.txt files found near: " + root);
            return;
        }
        Log("[Profiler] Found binder files: " + string.Join(", ", candidateFiles.Select(Path.GetFileName)));
        var theme = new BinderThemeService();
        var resolver = new CardMetadataResolver(Path.Combine(root, "cache_profiler"), new []{"modal_dfc","transform"}, 1, null);
        Directory.CreateDirectory(Path.Combine(root, "cache_profiler"));
        var parser = new BinderFileParser(theme, resolver, _ => null, _ => false);
        int slotsPerPage = 18; // default 3x3 * 2 pages preview
        foreach (var file in candidateFiles)
        {
            Log("[Profiler] Parsing " + Path.GetFileName(file));
            // Warm-up
            await parser.ParseAsync(file, slotsPerPage);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var sw = Stopwatch.StartNew();
            const int iterations = 5;
            // GC / allocation baseline
            long allocBaseline = GC.GetTotalAllocatedBytes(true);
            int g0b = GC.CollectionCount(0); int g1b = GC.CollectionCount(1); int g2b = GC.CollectionCount(2);
            for (int i = 0; i < iterations; i++)
            {
                await parser.ParseAsync(file, slotsPerPage);
            }
            sw.Stop();
            int g0a = GC.CollectionCount(0); int g1a = GC.CollectionCount(1); int g2a = GC.CollectionCount(2);
            long allocAfter = GC.GetTotalAllocatedBytes(true);
            long allocDelta = allocAfter - allocBaseline;
            double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
            double avgAllocPerIterKB = allocDelta > 0 ? (allocDelta / 1024.0) / iterations : 0;
            var memInfo = GC.GetGCMemoryInfo();
            Log($"[Profiler] {Path.GetFileName(file)} avg={avgMs:F2} ms iters={iterations} allocTotal={allocDelta/1024.0:F1}KB avgPerIter={avgAllocPerIterKB:F1}KB GCs Δ(Gen0={g0a-g0b},Gen1={g1a-g1b},Gen2={g2a-g2b}) heap={memInfo.HeapSizeBytes/1024/1024}MB committed={memInfo.TotalCommittedBytes/1024/1024}MB");
        }
    }
}
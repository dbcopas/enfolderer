using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enfolderer.App.Binder;
using Enfolderer.App.Core;
using Enfolderer.App.Metadata;
using Enfolderer.App.Quantity;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization tests for binder file parsing quirks to lock current behavior before refactor.
/// Each Check() failure increments counter; no framework dependency.
/// </summary>
public static class BinderParsingCharTests
{
    private static int _tmpIndex = 0;
    private static string WriteTempBinder(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"enfolderer_char_{Interlocked.Increment(ref _tmpIndex)}.binder.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static BinderFileParser CreateParser(Func<string,bool>? isCacheComplete = null)
    {
        var theme = new BinderThemeService();
    // Provide minimal valid constructor args: cacheRoot=temp dir, physicallyTwoSidedLayouts sample list, schemaVersion=1
    var metadata = new CardMetadataResolver(Path.GetTempPath(), new []{"modal_dfc","double_faced_token","double_faced_card"}, 1);
        // resolveLocalBackImagePath: return null to force embedded fallback mapping path
        Func<bool,string?> resolver = _ => null;
        return new BinderFileParser(theme, metadata, _ => resolver(true), isCacheComplete ?? (_ => false));
    }

    private static int _checkId = 0;
    private static void Check(ref int failures, bool condition, string message)
    {
        int id = Interlocked.Increment(ref _checkId);
        string logPath = Path.Combine(Path.GetTempPath(), "enfolderer_charlog.txt");
        if (!condition)
        {
            failures++; try { File.AppendAllText(logPath, $"FAIL {id} {message}\n"); } catch {}
        }
        else
        {
            try { File.AppendAllText(logPath, $"OK {id} {message}\n"); } catch {}
        }
    }

    public static int RunAll()
    {
        int failures = 0;
        void Scenario(Action a, string label){ try { a(); } catch(Exception ex){ failures++; try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_charlog.txt"), $"EX {label} {ex.GetType().Name}:{ex.Message}\n"); } catch {} } }

        Scenario(() => {
            // Real cache-hit characterization: first parse (no cache), then synthesize cache and reparse with isCacheComplete returning true.
            var path = WriteTempBinder("=SET", "1");
            var parser1 = CreateParser(_ => false);
            var first = Task.Run(() => parser1.ParseAsync(path, 18)).GetAwaiter().GetResult();
            Check(ref failures, first.CacheHit == false, "First parse unexpectedly cache hit");
            // Synthesize metadata cache file for hash produced by first parse.
            var hash = first.FileHash;
            var cacheRoot = System.IO.Path.GetTempPath();
            var metaDir = System.IO.Path.Combine(cacheRoot, "meta");
            System.IO.Directory.CreateDirectory(metaDir);
            var cachePath = System.IO.Path.Combine(metaDir, hash + ".json");
            var facesJson = "[" +
                "{\"Name\":\"Test\",\"Number\":\"1\",\"Set\":\"SET\",\"IsMfc\":false,\"IsBack\":false,\"FrontRaw\":null,\"BackRaw\":null,\"FrontImageUrl\":null,\"BackImageUrl\":null,\"Layout\":\"normal\",\"SchemaVersion\":1}" +
                "]";
            System.IO.File.WriteAllText(cachePath, facesJson);
            // Second parser with isCacheComplete acknowledging this hash.
            var parser2 = CreateParser(h => h == hash);
            var second = Task.Run(() => parser2.ParseAsync(path, 18)).GetAwaiter().GetResult();
            Check(ref failures, second.CacheHit == true && second.CachedCards.Count == 1, "Second parse did not produce expected cache hit");
        }, "CacheHitReal");

        Scenario(() => {
            var path = WriteTempBinder("=ZNR", "001-003");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, slotsPerPage: 18)).GetAwaiter().GetResult();
            var nums = result.Specs.Select(s => s.Number).ToList();
            Check(ref failures, nums.SequenceEqual(new[]{"001","002","003"}), "Zero padded numeric range expansion mismatch");
        }, "ZeroPadRange");

        Scenario(() => {
            var path = WriteTempBinder("=SET", "ABC001-003");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var nums = result.Specs.Select(s => s.Number).ToList();
            Check(ref failures, nums.SequenceEqual(new[]{"ABC001","ABC002","ABC003"}), "Attached prefix range expansion failed");
        }, "AttachedPrefixRange");

        Scenario(() => {
            var path = WriteTempBinder("=WAR", "12+JP");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var nums = result.Specs.Select(s => s.Number).ToList();
            Check(ref failures, nums.Count==2 && nums[0]=="12" && nums[1]=="12/jp", "+language variant pair not produced correctly");
        }, "PlusLanguageVariant");

        Scenario(() => {
            var path = WriteTempBinder("=WAR", "01-02+JP");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var pairs = result.Specs.Select(s => s.Number).ToList();
            Check(ref failures, pairs.SequenceEqual(new[]{"01","01/jp","02","02/jp"}), "Range+language variant expansion order changed");
        }, "RangeLanguageVariant");

        Scenario(() => {
            var path = WriteTempBinder("=ANY", "2;backface");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var backs = result.Specs.Where(s => s.SetCode=="__BACK__").ToList();
            Check(ref failures, backs.Count==2 && backs.All(b => b.Number=="BACK"), "Backface placeholder expansion incorrect");
        }, "BackfacePlaceholder");

        Scenario(() => {
            var path = WriteTempBinder("=WAR", "★7", "★8-9");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var stars = result.Specs.Select(s => s.Number).ToList();
            Check(ref failures, stars.SequenceEqual(new[]{"7★","8★","9★"}), "Star syntax expansion mismatch");
        }, "StarSyntax");

        Scenario(() => {
            var path = WriteTempBinder("=SET", "1-2||3-4");
            var parser = CreateParser();
            var result = Task.Run(() => parser.ParseAsync(path, 18)).GetAwaiter().GetResult();
            var seq = result.Specs.Select(s=>s.Number).ToList();
            Check(ref failures, seq.SequenceEqual(new[]{"1","3","2","4"}), "Parallel interleave order changed");
        }, "ParallelInterleave");

        Scenario(() => {
            var qtySvc = new CardQuantityService();
            var list = new List<CardEntry>{
                new CardEntry("Alpha/Beta|MFC","10","SET",true,false,"Alpha","Beta"),
                new CardEntry("Alpha/Beta|MFC","10","SET",true,false,"Alpha","Beta")
            };
            list[0] = list[0] with { Quantity = 1 };
            list[1] = list[1] with { Quantity = 1 };
            qtySvc.AdjustMfcQuantities(list);
            Check(ref failures, list[0].Quantity==1 && list[1].Quantity==0, "MFC fallback pairing (broad heuristic) changed");
        }, "MfcFallbackPairing");

    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_charlog.txt"), $"DONE failures={failures}\n"); } catch {}
    return failures;
    }
}

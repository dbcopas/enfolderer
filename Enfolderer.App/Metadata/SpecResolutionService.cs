using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using Enfolderer.App.Core;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Handles staged / incremental resolution of specs using CardMetadataResolver.
/// Encapsulates progress reporting and spec mutation callbacks.
/// </summary>
public class SpecResolutionService
{
    private readonly CardMetadataResolver _resolver;

    public SpecResolutionService(CardMetadataResolver resolver)
    { _resolver = resolver; }

    public async Task ResolveAsync(
        List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList,
        HashSet<int> targetIndexes,
        int updateInterval,
        List<CardSpec> specs,
        IDictionary<int, CardEntry> mfcBacks,
        Action<string>? statusSetter)
    {
        int total = targetIndexes.Count;
        await _resolver.ResolveSpecsAsync(
            fetchList,
            targetIndexes,
            done =>
            {
                if (done % updateInterval == 0 || done == total)
                {
                    statusSetter?.Invoke($"Resolving metadata {done}/{total} ({(int)(done*100.0/Math.Max(1,total))}%)");
                }
                return done;
            },
            (specIndex, resolved) =>
            {
                if (specIndex < 0 || specIndex >= specs.Count) return (null,false);
                if (resolved != null)
                {
                    if (resolved.IsBackFace) mfcBacks[specIndex] = resolved; else specs[specIndex] = specs[specIndex] with { Resolved = resolved };
                }
                return (null,false);
            },
            async (set, num, nameOverride) => await FetchViaHttpAsync(set, num, nameOverride)
        );
    }

    // Lightweight HTTP fetch replicating prior inline logic (kept here to isolate from VM).
    private static async Task<CardEntry?> FetchViaHttpAsync(string setCode, string number, string? overrideName)
    {
        try {
            await ApiRateLimiter.WaitAsync();
            var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
            var resp = await BinderViewModel.Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return CardJsonTranslator.Translate(doc.RootElement, setCode, number, overrideName);
        } catch { return null; }
    }
}

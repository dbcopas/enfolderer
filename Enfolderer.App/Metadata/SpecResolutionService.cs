using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using Enfolderer.App.Core;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Importing;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Handles staged / incremental resolution of specs using CardMetadataResolver.
/// Encapsulates progress reporting and spec mutation callbacks.
/// </summary>
public class SpecResolutionService
{
    private readonly ICardMetadataResolver _resolver;
    private readonly Func<System.Net.Http.HttpClient> _httpClientProvider;

    public SpecResolutionService(ICardMetadataResolver resolver, Func<System.Net.Http.HttpClient> httpClientProvider)
    { _resolver = resolver; _httpClientProvider = httpClientProvider; }

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
    private async Task<CardEntry?> FetchViaHttpAsync(string setCode, string number, string? overrideName)
    {
        try {
            await ApiRateLimiter.WaitAsync();
            var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
        var client = _httpClientProvider();
        var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return CardJsonTranslator.Translate(doc.RootElement, setCode, number, overrideName);
        } catch { return null; }
    }
}

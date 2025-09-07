using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using Enfolderer.App.Core;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Importing;
using Enfolderer.App.Imaging;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Handles staged / incremental resolution of specs using CardMetadataResolver.
/// Encapsulates progress reporting and spec mutation callbacks.
/// </summary>
public class SpecResolutionService
{
    private readonly ICardMetadataResolver _resolver;
    private readonly Func<System.Net.Http.HttpClient> _httpClientProvider;
    private readonly Enfolderer.App.Core.Abstractions.ILogSink? _log;

    public SpecResolutionService(ICardMetadataResolver resolver, Func<System.Net.Http.HttpClient> httpClientProvider, Enfolderer.App.Core.Abstractions.ILogSink? log = null)
    { _resolver = resolver; _httpClientProvider = httpClientProvider; _log = log; }

    public async Task ResolveAsync(
        List<FetchSpec> fetchList,
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
            if (NotFoundCardStore.IsNotFound(setCode, number)) { _log?.Log($"Skip fetch (cached 404) set={setCode} num={number}", "SpecFetch"); return null; }
            await ApiRateLimiter.WaitAsync();
            var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
        var client = _httpClientProvider();
        var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                try { _log?.Log($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} GET {url}", "SpecFetch"); } catch { }
                if ((int)resp.StatusCode == 404) { try { NotFoundCardStore.MarkNotFound(setCode, number); } catch { } }
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return CardJsonTranslator.Translate(doc.RootElement, setCode, number, overrideName);
    } catch (Exception ex) { _log?.Log($"HTTP fetch failed set={setCode} num={number}: {ex.Message}", "SpecFetch"); return null; }
    }
}

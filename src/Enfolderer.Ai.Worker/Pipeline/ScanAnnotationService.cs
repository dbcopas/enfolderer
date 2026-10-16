using System.Diagnostics;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure.Catalogue;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Worker.Pipeline;

public sealed class ScanAnnotationService(
    ScryfallPriceClient prices, IScanImageStore images, ILogger<ScanAnnotationService> log)
{
    public async Task<ScanResultDocument> CreateAsync(ScanResultDocument result, Stream source, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var enriched = new List<IdentifiedCard>();
        var cache = new Dictionary<(string Set, string Number, string Language), CardPrices>();
        foreach (var card in result.Cards)
        {
            var updated = card;
            if (card.Game == CardGames.Magic && card.IsIdentified && card.HasPrinting)
            {
                var key = (card.Set!, card.CollectorNumber!, card.Language ?? "en");
                if (!cache.TryGetValue(key, out var value))
                {
                    try
                    {
                        value = await prices.GetAsync(key.Item1, key.Item2, key.Item3, ct);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException
                        || ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        log.LogWarning(ex, "Card {Index} of job {JobId}: pricing failed for {Set} {Number}.",
                            card.Index, result.JobId, card.Set, card.CollectorNumber);
                        value = new CardPrices { RetrievedAt = DateTimeOffset.UtcNow, Error = "Price lookup failed; see scan logs" };
                    }
                    cache.Add(key, value);
                }
                updated = card with { Prices = value };
                log.LogInformation("Card {Index} of job {JobId}: prices EUR nonfoil {Nonfoil} ({NonfoilLanguage}), "
                    + "foil {Foil} ({FoilLanguage}); source {Source}; retrieved {RetrievedAt}; error {Error}.",
                    card.Index, result.JobId, value.Nonfoil, value.NonfoilLanguage, value.Foil,
                    value.FoilLanguage, value.Source, value.RetrievedAt, value.Error);
            }
            enriched.Add(updated);
        }
        result = result with { Cards = enriched };
        log.LogInformation("Job {JobId}: timing pricing, elapsed {ElapsedMs} ms.", result.JobId, timer.ElapsedMilliseconds);
        try
        {
            timer.Restart();
            source.Position = 0;
            using var png = new MemoryStream();
            PricedScanRenderer.Render(source, enriched, png);
            log.LogInformation("Job {JobId}: timing rendering, elapsed {ElapsedMs} ms.", result.JobId, timer.ElapsedMilliseconds);
            png.Position = 0;
            timer.Restart();
            await images.WriteAsync(ScanBlobPaths.BuildAnnotatedBlobPath(result.JobId), png, "image/png", ct);
            log.LogInformation("Job {JobId}: timing image_store, elapsed {ElapsedMs} ms.", result.JobId, timer.ElapsedMilliseconds);
            return result with { AnnotatedImagePath = $"jobs/{Uri.EscapeDataString(result.JobId)}/annotated-image" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Job {JobId}: annotated image unavailable; preserving scan and CSV results.", result.JobId);
            return result with { AnnotationError = "Annotated image could not be created or stored; see worker logs." };
        }
    }
}

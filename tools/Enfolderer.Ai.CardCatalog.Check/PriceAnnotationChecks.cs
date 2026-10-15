using System.Net;
using System.Text;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure.Catalogue;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class PriceAnnotationChecks
{
    public static async Task RunAsync(Action<bool, string> check, string? previewPath = null)
    {
        Console.WriteLine("\nExact-printing EUR prices and annotated scan output");
        foreach (var item in new[]
        {
            (LocalNf: "1.25", LocalFoil: "2.50", EnglishNf: "3.00", EnglishFoil: "4.00", Nf: (decimal?)1.25m, Foil: (decimal?)2.50m, NfLang: "ja", FoilLang: "ja", Requests: 1),
            ("1.25", null, "3.00", "4.00", (decimal?)1.25m, (decimal?)4m, "ja", "en", 2),
            (null, "2.50", "3.00", "4.00", (decimal?)3m, (decimal?)2.50m, "en", "ja", 2),
            (null, null, "3.00", null, (decimal?)3m, null, "en", null, 2),
            (null, null, null, "4.00", null, (decimal?)4m, null, "en", 2),
            (null, null, null, null, null, null, null, null, 2)
        })
        {
            using var handler = new PriceHandler(item.LocalNf, item.LocalFoil, item.EnglishNf, item.EnglishFoil);
            using var http = new HttpClient(handler);
            var result = await new ScryfallPriceClient(http).GetAsync("clb", "240", "JP", default);
            check(result.Nonfoil == item.Nf && result.Foil == item.Foil
                && result.NonfoilLanguage == item.NfLang && result.FoilLanguage == item.FoilLang
                && handler.Paths.Count == item.Requests && result.Error is null,
                $"independent finish fallback: local {item.LocalNf ?? "N/A"}/{item.LocalFoil ?? "N/A"}, "
                + $"English {item.EnglishNf ?? "N/A"}/{item.EnglishFoil ?? "N/A"}");
            check(handler.Paths[0].EndsWith("/clb/240/ja")
                && handler.Paths.All(p => p.Contains("/clb/240")), "pricing never substitutes another set or number");
        }
        using (var handler = new PriceHandler(null, null, "0", null, foreignMissing: true))
        using (var http = new HttpClient(handler))
        {
            var result = await new ScryfallPriceClient(http).GetAsync("clb", "240", "ja", default);
            check(result.Nonfoil == 0m && result.Foil is null && result.NonfoilLanguage == "en",
                "missing localized printing falls back to English; actual zero is distinct from missing");
        }
        using (var handler = new PriceHandler(null, null, "5", "6"))
        using (var http = new HttpClient(handler))
        {
            var result = await new ScryfallPriceClient(http).GetAsync("clb", "240", "en", default);
            check(handler.Paths.Count == 1 && result.Nonfoil == 5m && result.Foil == 6m,
                "English cards query once and do not trigger redundant fallback");
        }
        using var image = new Image<Rgba32>(800, 1100, Color.DarkGreen);
        using var source = new MemoryStream();
        image.SaveAsPng(source);
        var card = new IdentifiedCard
        {
            Index = 0, Game = CardGames.Magic, Set = "clb", CollectorNumber = "240",
            Name = "Majestic Genesis", Language = "ja", Resolution = "ambiguous",
            Quad = new CardQuad([new(40, 40), new(760, 40), new(760, 1060), new(40, 1060)])
        };
        var store = new OutputStore();
        var loneMatch = card with { ArtVerdict = "agrees", ArtMargin = 0 };
        check(!loneMatch.PrintingWasPlaced
            && PricedScanRenderer.DescribePrintingEvidence(loneMatch).Contains("Artwork matches; set/number not established"),
            "a lone artwork match explains the evidence without claiming printing verification");
        var decisiveMatch = loneMatch with { ArtMargin = 0.08 };
        check(decisiveMatch.PrintingWasPlaced
            && PricedScanRenderer.DescribePrintingEvidence(decisiveMatch).Contains("decisive artwork comparison"),
            "decisive artwork agreement places a default printing just like an art-based correction");
        check(PricedScanRenderer.DescribePriceTarget(card) == "Price for CLB 240"
            && PricedScanRenderer.DescribePrintingEvidence(card).Contains("Printing needs verification"),
            "the exact price target is separate from confidence in the photographed printing");
        check(PricedScanRenderer.DescribePrintingEvidence(card with { Resolution = "confirmed" })
            == "Printing supported by catalogue resolution",
            "confirmed text evidence does not require a decisive artwork match");
        using (var handler = new PriceHandler("1", null, "2", "3"))
        using (var http = new HttpClient(handler))
        {
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), store,
                NullLogger<ScanAnnotationService>.Instance);
            var result = await service.CreateAsync(new ScanResultDocument
            {
                JobId = "price-test", Cards = [card, card with { Index = 1, Quad = null }]
            }, source, default);
            check(result.AnnotatedImagePath == "jobs/price-test/annotated-image"
                && result.AnnotationError is null && result.Cards[0].Language == "ja"
                && result.Cards[0].Prices?.FoilLanguage == "en" && handler.Paths.Count == 2,
                "annotation enriches final printings, preserves photographed language, and caches duplicate lookups");
            using var png = Image.Load<Rgba32>(store.Content!);
            if (previewPath is not null) await File.WriteAllBytesAsync(previewPath, store.Content!);
            check(png.Width == 800 && png.Height > image.Height && png[0, 0] == image[0, 0]
                && png[60, 370] != image[60, 370],
                "output preserves original photo outside labels, draws a price overlay and appends a legend");
            check(store.Path == "outputs/price-test/priced.png" && store.ContentType == "image/png",
                "output uses a job-scoped private container path and PNG content type");
            var roundTrip = JsonSerializer.Deserialize<ScanResultDocument>(JsonSerializer.Serialize(result, ScanJson.Options), ScanJson.Options);
            check(roundTrip?.Cards[0].Prices?.Nonfoil == 1m && roundTrip.AnnotatedImagePath == result.AnnotatedImagePath,
                "image route and nullable prices/provenance survive API JSON");
            check(PricedScanRenderer.DescribePrices(new CardPrices { Nonfoil = 1.23m, NonfoilLanguage = "en" })
                .Contains("Foil: N/A"), "one-finish price labels retain N/A instead of borrowing the other finish");
            check(PricedScanRenderer.DescribePrices(result.Cards[0].Prices, "ja").Contains("en fallback"),
                "the image labels an English fallback without changing photographed language");
        }
        using (var handler = new PriceHandler("1", null, null, null, failEnglish: true))
        using (var http = new HttpClient(handler))
        {
            var result = await new ScryfallPriceClient(http).GetAsync("clb", "240", "ja", default);
            check(result.Nonfoil == 1m && result.NonfoilLanguage == "ja" && result.Foil is null && result.Error is not null,
                "failed English fallback preserves an available foreign-language finish and reports the failure");
        }
        using (var handler = new PriceHandler(null, null, null, null, wrongPrinting: true))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await new ScryfallPriceClient(http).GetAsync("clb", "240", "en", default);
                check(false, "a mismatched pricing record must be rejected");
            }
            catch (InvalidDataException) { check(true, "a price for another collector number is never accepted"); }
        }
        foreach (var malformed in new[] { "[]", "{\"set\":123}", "{\"set\":\"clb\",\"collector_number\":\"240\",\"lang\":\"ja\",\"prices\":[]}" })
        {
            using var handler = new PriceHandler(null, null, null, null, rawJson: malformed);
            using var http = new HttpClient(handler);
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), new OutputStore(),
                NullLogger<ScanAnnotationService>.Instance);
            var result = await service.CreateAsync(new ScanResultDocument { JobId = "malformed", Cards = [card] }, source, default);
            check(result.Cards[0].Prices?.Error is not null && result.AnnotatedImagePath is not null,
                "malformed price-response shapes report optional errors without failing the scan");
        }
        foreach (var failWrite in new[] { false, true })
        {
            using var handler = new PriceHandler(null, null, null, null, fail: true);
            using var http = new HttpClient(handler);
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), new OutputStore { FailWrite = failWrite },
                NullLogger<ScanAnnotationService>.Instance);
            var result = await service.CreateAsync(new ScanResultDocument { JobId = "errors", Cards = [card] }, source, default);
            check(result.Cards.Count == 1 && result.Cards[0].IsIdentified && result.Cards[0].Prices?.Error is not null
                && (failWrite ? result.AnnotationError is not null : result.AnnotatedImagePath is not null),
                "pricing/storage failure is explicit and never discards successfully identified cards");
        }
        using (var handler = new PriceHandler(null, null, null, null))
        using (var http = new HttpClient(handler))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await new ScryfallPriceClient(http).GetAsync("clb", "240", "ja", cancellation.Token);
                check(false, "pricing cancellation must propagate");
            }
            catch (OperationCanceledException) { check(true, "pricing cancellation propagates"); }
        }
    }

    private sealed class PriceHandler(string? localNf, string? localFoil, string? englishNf, string? englishFoil,
        bool foreignMissing = false, bool fail = false, bool failEnglish = false, bool wrongPrinting = false,
        string? rawJson = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.AbsolutePath);
            var foreign = Paths[^1].EndsWith("/ja");
            if (fail || failEnglish && !foreign || foreign && foreignMissing)
                return Task.FromResult(new HttpResponseMessage(fail || failEnglish ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound));
            var json = rawJson ?? JsonSerializer.Serialize(new
            {
                set = "clb", collector_number = wrongPrinting ? "999" : "240", lang = foreign ? "ja" : "en",
                prices = new { eur = foreign ? localNf : englishNf, eur_foil = foreign ? localFoil : englishFoil, usd = "99.00" }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class OutputStore : IScanImageStore
    {
        public bool FailWrite { get; init; }
        public byte[]? Content { get; private set; }
        public string? Path { get; private set; }
        public string? ContentType { get; private set; }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(Content is not null);
        public async Task WriteAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        {
            if (FailWrite) throw new IOException("Test storage failure");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Content = buffer.ToArray();
            Path = path;
            ContentType = contentType;
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure.Catalogue;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.Fonts;
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
            Name = "Majestic Genesis", Language = "ja", Resolution = "ambiguous", Confidence = 0.6,
            Quad = new CardQuad([new(40, 40), new(760, 40), new(760, 1060), new(40, 1060)])
        };
        var store = new OutputStore();
        var family = SystemFonts.TryGet("DejaVu Sans", out var dejavu) ? dejavu : SystemFonts.Get("Segoe UI Symbol");
        foreach (var prices in new[]
        {
            new CardPrices { Nonfoil = 1, Foil = 3 },
            new CardPrices { Nonfoil = 123456.78m, Foil = 999999.99m },
            new CardPrices()
        })
        {
            var text = PricedScanRenderer.DescribeOverlayPrices(prices);
            if (text.Length == 0)
            {
                check(true, "missing prices leave the overlay text empty");
                continue;
            }
            var fitted = PricedScanRenderer.FitOverlayFont(family, text, 291.6f, 57.6f);
            var bounds = TextMeasurer.MeasureBounds(text, new TextOptions(fitted));
            var larger = TextMeasurer.MeasureBounds(text, new TextOptions(family.CreateFont(fitted.Size * 1.01f)));
            check(bounds.Width <= 292 && bounds.Height <= 58
                && (larger.Width > 291.6f || larger.Height > 57.6f),
                "measured font fits both dimensions and uses the maximum available space for short, long and missing prices");
        }
        check(PricedScanRenderer.DescribeOverlayPrices(new CardPrices { Nonfoil = 1.23m, Foil = 4.56m })
            == "\u20ac1.23    \u20ac4.56\u2605"
            && PricedScanRenderer.DescribeOverlayPrices(new CardPrices { Foil = 4.56m }) == "\u20ac4.56\u2605"
            && PricedScanRenderer.DescribeOverlayPrices(new CardPrices { Nonfoil = 1.23m }) == "\u20ac1.23"
            && PricedScanRenderer.DescribeOverlayPrices(null) == "",
            "overlay uses euro symbols and a foil star, preserving missing finishes");
        IdentifiedCard[] unsorted =
        [
            card with { Index = 7 },
            card with { Index = 6, Prices = new CardPrices() },
            card with { Index = 5, Prices = new CardPrices { Nonfoil = 1, Foil = 200 } },
            card with { Index = 4, Prices = new CardPrices { Nonfoil = 0 } },
            card with { Index = 3, Prices = new CardPrices { Foil = 100 } },
            card with { Index = 2, Prices = new CardPrices { Nonfoil = 5 } },
            card with { Index = 1, Prices = new CardPrices { Nonfoil = 5 } },
            card with { Index = 0, Prices = new CardPrices { Nonfoil = 10 } }
        ];
        check(PricedScanRenderer.OrderLegendCards(unsorted).Select(c => c.Index).SequenceEqual([3, 0, 1, 2, 5, 4, 6, 7])
            && unsorted.Select(c => c.Index).SequenceEqual([7, 6, 5, 4, 3, 2, 1, 0]),
            "legend uses foil only when nonfoil is missing, preserves zero, stable ties and CSV order");
        check(PricedScanRenderer.DescribeOverlayPrinting(card) == "CLB 240"
            && PricedScanRenderer.DescribeOverlayPrinting(card with { Set = null, CollectorNumber = null }) == ""
            && PricedScanRenderer.DescribeOverlayPrinting(card with { CollectorNumber = "240a" }) == "CLB 240a",
            "overlay includes uppercase set and exact collector number without inventing missing identifiers");
        check(PricedScanRenderer.OverlayConfidenceColor(card with { Resolution = "confirmed" }) == Color.LimeGreen
            && PricedScanRenderer.OverlayConfidenceColor(card) == Color.Gold
            && PricedScanRenderer.OverlayConfidenceColor(card with { Confidence = 0.2 }) == Color.Red
            && PricedScanRenderer.OverlayConfidenceColor(card with { Name = null }) == Color.Red,
            "confidence circle is green for placed printings, yellow for review, red for low confidence/unidentified");
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
            check(png[373, 429] == Color.Gold.ToPixel<Rgba32>() && png[45, 45] == image[45, 45]
                && png[399, 340] != image[399, 340] && png[401, 340] == image[401, 340],
                "price box occupies exactly half the card width, with lower-right circle and no number badge");
            check(Enumerable.Range(413, 20).Any(y => Enumerable.Range(55, 150)
                .Any(x => png[x, y].R > 180 && png[x, y].G > 180 && png[x, y].B > 180)),
                "rendered overlay includes the smaller printing line below the price");
            foreach (var candidate in new[]
            {
                card with { Resolution = "confirmed" },
                card with { Confidence = 0.2 }
            })
            {
                source.Position = 0;
                using var rendered = new MemoryStream();
                PricedScanRenderer.Render(source, [candidate with { Prices = result.Cards[0].Prices }], rendered);
                rendered.Position = 0;
                using var confidenceImage = Image.Load<Rgba32>(rendered);
                check(confidenceImage[373, 429] == PricedScanRenderer.OverlayConfidenceColor(candidate).ToPixel<Rgba32>(),
                    "rendered circle uses the actual green/red match confidence color");
            }
            check(store.Path == "outputs/price-test/priced.png" && store.ContentType == "image/png",
                "output uses a job-scoped private container path and PNG content type");
            var roundTrip = JsonSerializer.Deserialize<ScanResultDocument>(JsonSerializer.Serialize(result, ScanJson.Options), ScanJson.Options);
            check(roundTrip?.Cards[0].Prices?.Nonfoil == 1m && roundTrip.AnnotatedImagePath == result.AnnotatedImagePath,
                "image route and nullable prices/provenance survive API JSON");
            check(PricedScanRenderer.DescribePrices(new CardPrices { Nonfoil = 1.23m, NonfoilLanguage = "en" })
                .EndsWith("Foil: "), "missing finish labels stay blank without borrowing another finish or showing a star");
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

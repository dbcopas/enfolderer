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
                && handler.Paths.Count == item.Requests && result.Error is null
                && result.Layout == "normal" && result.CardName == "Majestic Genesis",
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
        check(CardValueRanking.Order(unsorted).Select(c => c.Index).SequenceEqual([3, 0, 1, 2, 5, 4, 6, 7])
            && unsorted.Select(c => c.Index).SequenceEqual([7, 6, 5, 4, 3, 2, 1, 0]),
            "value ranking uses foil only when nonfoil is missing, preserves zero and stable ties without changing source order");
        var trophyCards = new[]
        {
            card with { Index = 0, Prices = new CardPrices { Nonfoil = 100 }, Ownership = new(4, 4, 1) },
            card with { Index = 1, Prices = new CardPrices { Nonfoil = 50 }, Ownership = new(0, 0, 1, "unknown") },
            card with { Index = 2, Prices = new CardPrices { Nonfoil = 10 }, Ownership = new(0, 4, 1) },
            card with { Index = 3, Prices = new CardPrices { Foil = 20 }, Ownership = new(1, 6, 2) },
            card with { Index = 4, Prices = new CardPrices { Nonfoil = 30 }, Ownership = new(2, 6, 2) },
            card with { Index = 5, Prices = new CardPrices(), Ownership = new(0, 0, 1) }
        };
        check(CardValueRanking.HighestPriced(trophyCards)?.Index == 0
            && CardValueRanking.HighestPriced(trophyCards.Skip(1))?.Index == 1
            && CardValueRanking.HighestPriced([trophyCards[5]]) is null
            && CardValueRanking.HighestPriced([card]) is null
            && CardValueRanking.HighestPriced([card with { Index = 1, Prices = new CardPrices { Nonfoil = 0 } },
                card with { Index = 0, Prices = new CardPrices { Nonfoil = 0 } }])?.Index == 0,
            "one trophy selects highest-priced identified card irrespective of ownership, with scan-order ties and real zero prices");
        var ranks = CardValueRanking.Ranks(trophyCards);
        check(ranks.Count == 6 && ranks[0] == 1 && ranks[1] == 2 && ranks[2] == 5 && ranks[3] == 4 && ranks[5] == 6
            && CardValueRanking.Ranks(unsorted).Count == 8,
            "every identified card has its one-based full CSV price rank, regardless of ownership or missing price");
        check(CardValueRanking.Ranks(trophyCards.Append(card with { Index = 9, Name = null,
                Prices = new CardPrices { Nonfoil = 999 } }))[2] == 5,
            "unidentified cards excluded from CSV cannot shift overlay price ranks");
        check(PricedScanRenderer.OverlayBackgroundColor(trophyCards[2]) == Color.FromRgb(160, 25, 25)
            && PricedScanRenderer.OverlayBackgroundColor(trophyCards[3]) == Color.FromRgb(160, 25, 25)
            && PricedScanRenderer.OverlayBackgroundColor(trophyCards[0]) == Color.Black
            && PricedScanRenderer.OverlayBackgroundColor(trophyCards[1]) == Color.Black,
            "only exact-printing shortages turn panels red; unknown ownership is never amber or treated as a known shortage");
        check(PricedScanRenderer.DescribeOverlayPrinting(card) == "CLB 240"
            && PricedScanRenderer.DescribeOverlayPrinting(card with { Set = null, CollectorNumber = null }) == ""
            && PricedScanRenderer.DescribeOverlayPrinting(card with { CollectorNumber = "240a" }) == "CLB 240a",
            "overlay includes uppercase set and exact collector number without inventing missing identifiers");
        check(PricedScanRenderer.DescribeOverlayName(card with { Language = "en" }) == ""
            && PricedScanRenderer.DescribeOverlayName(card with { Language = " EN " }) == ""
            && PricedScanRenderer.DescribeOverlayName(card with { Language = "de" }) == card.Name
            && PricedScanRenderer.DescribeOverlayName(card with { Language = null }) == card.Name,
            "only confirmed English cards omit the name; foreign and unknown-language cards retain it");
        var lettering = new List<byte[]>();
        foreach (var sample in new[]
        {
            card with { Language = "en", Prices = new CardPrices { Nonfoil = 10 } },
            card with { Language = "de", Prices = new CardPrices { Nonfoil = 10 },
                Name = string.Join(" ", Enumerable.Repeat("Very long translated name", 8)) },
            card with { Language = "de", Name = "Short", Prices = new CardPrices { Nonfoil = 123456789.99m, Foil = 999999999.99m } }
        })
        {
            source.Position = 0;
            using var rendered = new MemoryStream();
            PricedScanRenderer.Render(source, [sample], rendered);
            using var renderedImage = Image.Load<Rgba32>(rendered.ToArray());
            var pixels = new List<byte>();
            var printingTop = sample.Language == "en" ? 412 : 466;
            for (var y = printingTop; y < printingTop + 39; y++)
                for (var x = 55; x < 340; x++)
                {
                    var pixel = renderedImage[x, y];
                    pixels.AddRange([pixel.R, pixel.G, pixel.B]);
                }
            lettering.Add(pixels.ToArray());
            if (sample.Language == "en")
                check(renderedImage[60, 510] == image[60, 510],
                    "English overlays remove the name band and expose the photo below the shorter panel");
        }
        check(lettering.Skip(1).All(p => p.SequenceEqual(lettering[0])),
            "actual set/number pixels are independent of English name omission, long foreign names and long price text");
        foreach (var language in new[] { "en", "de" })
        foreach (var compare in new[] { false, true })
        {
            source.Position = 0;
            using var output = new MemoryStream();
            PricedScanRenderer.Render(source, [card with { Language = language, Prices = new CardPrices { Nonfoil = 10 },
                Ownership = compare ? new(1, 4, 1) : null }], output);
            using var rendered = Image.Load<Rgba32>(output.ToArray());
            var bottom = (int)Math.Ceiling(325.6 + 360 * (0.24 + (language == "en" ? 0 : 0.15)
                + 0.19 + (compare ? 0.32 : 0)));
            check(rendered[60, bottom - 2] != image[60, bottom - 2]
                && rendered[60, bottom + 2] == image[60, bottom + 2],
                $"{language}, comparison {compare}: rendered panel height includes only the active text bands");
        }
        await CheckRelatedOwnershipAsync(check, card, source);
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
            check(png.Width == 800 && png.Height == image.Height && png[0, 0] == image[0, 0]
                && png[60, 370] != image[60, 370],
                "output preserves original photo dimensions and pixels outside labels, with no footer or appended legend");
            check(png[373, 514] == Color.Gold.ToPixel<Rgba32>() && png[45, 45] == image[45, 45]
                && png[399, 340] != image[399, 340] && png[401, 340] == image[401, 340],
                "price box occupies exactly half the card width, with lower-right circle");
            check(new[] { 413, 467 }.All(top => Enumerable.Range(top, 20).Any(y => Enumerable.Range(55, 150)
                .Any(x => png[x, y].R > 180 && png[x, y].G > 180 && png[x, y].B > 180))),
                "taller overlay includes card name and exact set/number on separate lines under the prices");
            check(result.Cards.All(c => c.Ownership is null)
                && Enumerable.Range(540, 106).All(y => Enumerable.Range(40, 360)
                    .All(x => png[x, y] == image[x, y])),
                "without collection comparison, the entire former ownership band preserves the original photo");
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
                check(confidenceImage[373, 514] == PricedScanRenderer.OverlayConfidenceColor(candidate).ToPixel<Rgba32>(),
                    "rendered circle uses the actual green/red match confidence color");
            }
            foreach (var needed in new[] { true, false })
            {
                source.Position = 0;
                using var rendered = new MemoryStream();
                var candidate = card with { Prices = new CardPrices { Nonfoil = 10 },
                    Ownership = new(needed ? 0 : 1, 4, 1) };
                PricedScanRenderer.Render(source, [candidate], rendered);
                rendered.Position = 0;
                using var trophyImage = Image.Load<Rgba32>(rendered);
                check(trophyImage[375, 345] == Color.Gold.ToPixel<Rgba32>(),
                    "rendered gold trophy marks the highest-value card even when already owned");
                var rankVisible = Enumerable.Range(340, 27).Any(y => Enumerable.Range(360, 26)
                    .Any(x => trophyImage[x, y].R > 220 && trophyImage[x, y].G > 220 && trophyImage[x, y].B > 220));
                check(!rankVisible, "the trophy winner does not also receive a numeric rank badge");
            }
            var positions = new[] { 40d, 300d, 560d };
            var badgeCards = positions.Select((x, i) => card with { Index = i,
                Quad = new CardQuad([new(x, 40), new(x + 220, 40), new(x + 220, 348), new(x, 348)]),
                Prices = new CardPrices { Nonfoil = 30 - i * 10 },
                Ownership = i == 0 ? new(4, 4, 1) : i == 1 ? new(0, 0, 1) : null }).ToArray();
            source.Position = 0;
            using (var rendered = new MemoryStream())
            {
                PricedScanRenderer.Render(source, badgeCards, rendered);
                using var badges = Image.Load<Rgba32>(rendered.ToArray());
                for (var i = 0; i < positions.Length; i++)
                {
                    var left = (int)positions[i] + 97;
                    var gold = Enumerable.Range(130, 11).Any(y => Enumerable.Range(left, 10)
                        .Any(x => badges[x, y] == Color.Gold.ToPixel<Rgba32>()));
                    var white = Enumerable.Range(130, 8).Any(y => Enumerable.Range(left, 10)
                        .Any(x => badges[x, y].R > 220 && badges[x, y].G > 220 && badges[x, y].B > 220));
                    check(gold == (i == 0) && white == (i != 0),
                        $"rendered price rank {i + 1}: only the winner has a trophy; every other card has a number");
                }
            }
            using (var legacyHandler = new PriceHandler(null, null, null, null, rawJson:
                """{"set":"vis","collector_number":"11","lang":"en","layout":"normal","name":"Knight of Valor","multiverse_ids":[3718],"prices":{"eur":"0.11","eur_foil":null}}"""))
            using (var legacyHttp = new HttpClient(legacyHandler))
            {
                var legacyService = new ScanAnnotationService(new ScryfallPriceClient(legacyHttp), new OutputStore(),
                    NullLogger<ScanAnnotationService>.Instance);
                var snapshot = new CollectionSnapshot { GathererPrintings = [new(3718, 4, 4)] };
                snapshot.Validate();
                var legacyResult = await legacyService.CreateAsync(new ScanResultDocument { JobId = "legacy",
                    Cards = [card with { Set = "vis", CollectorNumber = "11", Name = "Knight of Valor", Language = "en" }] },
                    source, default, snapshot);
                check(legacyResult.Cards[0].Prices?.GathererIds.SequenceEqual([3718]) == true
                    && legacyResult.Cards[0].Ownership is { Quantity: 4, Error: null, IsMissing: false },
                    "pricing metadata resolves legacy unnumbered Knight of Valor to exactly four owned, without scanned-name matching");
                var wrongPrinting = card with { Set = "vis", CollectorNumber = "12",
                    Prices = new CardPrices { Layout = "normal", GathererIds = [3719] } };
                check(CollectionSnapshot.Resolve(wrongPrinting, snapshot.BuildIndex())
                    is { Quantity: 0, AllQuantity: 0, Error: null, IsMissing: true },
                    "an absent printing assumes zero without inheriting another printing's legacy ownership");
                var numbered = snapshot with { Printings = [new("vis", "11", 0, 4)] };
                check(CollectionSnapshot.Resolve(legacyResult.Cards[0], numbered.BuildIndex()).Quantity == 0,
                    "exact set/number quantity, including zero, takes priority over the legacy ID fallback");
            }
            check(store.Path == "outputs/price-test/priced.png" && store.ContentType == "image/png",
                "output uses a job-scoped private container path and PNG content type");
            source.Position = 0;
            var datedPrefix = ScanBlobPaths.BuildJobPrefix("dated", new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var datedResult = await service.CreateAsync(new ScanResultDocument { JobId = "dated", Cards = [] },
                source, default, blobPrefix: datedPrefix);
            check(store.Path == $"outputs/{datedPrefix}/priced.png"
                && datedResult.AnnotatedImagePath == "jobs/dated/annotated-image",
                "annotation stores dated blobs without changing public result download URLs");
            var roundTrip = JsonSerializer.Deserialize<ScanResultDocument>(JsonSerializer.Serialize(result, ScanJson.Options), ScanJson.Options);
            check(roundTrip?.Cards[0].Prices?.Nonfoil == 1m && roundTrip.AnnotatedImagePath == result.AnnotatedImagePath
                && roundTrip.Cards[0].Prices?.Layout == "normal" && roundTrip.Cards[0].Prices?.CardName == "Majestic Genesis",
                "image route, nullable prices, layout and catalogue name survive API JSON");
            check(PricedScanRenderer.DescribePrices(new CardPrices { Nonfoil = 1.23m, NonfoilLanguage = "en" })
                .EndsWith("Foil: "), "missing finish labels stay blank without borrowing another finish or showing a star");
            check(PricedScanRenderer.DescribePrices(result.Cards[0].Prices, "ja").Contains("en fallback"),
                "the image labels an English fallback without changing photographed language");
        }
        foreach (var quantity in new[] { 0, 2 })
        {
            using var handler = new PriceHandler("1", "2", null, null);
            using var http = new HttpClient(handler);
            var output = new OutputStore();
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), output,
                NullLogger<ScanAnnotationService>.Instance);
            var snapshot = new CollectionSnapshot { Printings = [new("clb", "240", quantity, 15)] };
            var result = await service.CreateAsync(new ScanResultDocument { JobId = "owned", Cards = [card] },
                source, default, snapshot);
            var owned = result.Cards[0].Ownership;
            check(owned is not null && owned.Quantity == quantity && owned.AllQuantity == 15
                && owned.IsMissing == (quantity == 0)
                && owned.OverlayText == $"Owned: {quantity}\nAll sets: 15",
                "worker always displays exact-printing quantities, including zero, and the all-sets total");
            using var rendered = Image.Load<Rgba32>(output.Content!);
            check(rendered[41, 100] == image[41, 100]
                && rendered[373, 623] == Color.Gold.ToPixel<Rgba32>()
                && (quantity == 0 ? rendered[60, 340].R > rendered[60, 340].G
                    : rendered[60, 340].R == 0)
                && Enumerable.Range(540, 90).Any(y => Enumerable.Range(55, 240)
                    .Any(x => rendered[x, y].R > 220 && rendered[x, y].G > 220 && rendered[x, y].B > 220)),
                "server collection overlays use a missing-card panel tint or normal owned panel, never a card frame");
        }
        using (var handler = new PriceHandler("1", "2", null, null))
        using (var http = new HttpClient(handler))
        {
            var output = new OutputStore();
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), output,
                NullLogger<ScanAnnotationService>.Instance);
            var result = await service.CreateAsync(new ScanResultDocument { JobId = "unknown", Cards = [card] },
                source, default, new CollectionSnapshot { Printings = [new("aaa", "1", 0, 0)] });
            check(result.Cards[0].Ownership is { Quantity: 0, AllQuantity: 0, RequiredQuantity: 1,
                    Error: null, IsMissing: true, OverlayText: "Owned: 0\nAll sets: 0" }
                && result.AnnotatedImagePath is not null
                && CardValueRanking.HighestPriced(result.Cards)?.Index == card.Index,
                "absent snapshot printings assume zero, suppress question marks and remain eligible for the overall price trophy");
            using var rendered = Image.Load<Rgba32>(output.Content!);
            check(rendered[60, 340].R > rendered[60, 340].G,
                "absent snapshot printings render with the missing-card red panel instead of amber");
            var doubleFaced = card with { Prices = new CardPrices { Layout = "modal_dfc" } };
            check(CollectionSnapshot.Resolve(doubleFaced,
                    new CollectionSnapshot { Printings = [new("aaa", "1", 0, 0)] }.BuildIndex())
                is { Quantity: 0, AllQuantity: 0, RequiredQuantity: 2, Error: null, IsMissing: true },
                "absent double-faced printings assume zero while retaining the two-copy requirement");
            var unavailable = await service.CreateAsync(new ScanResultDocument { JobId = "unavailable", Cards = [card] },
                source, default, ownershipError: "Collection snapshot unavailable.");
            check(unavailable.Cards[0].Ownership is { Error: not null, OverlayText: "Owned: unavailable\nAll sets: unavailable" }
                && CardValueRanking.Ranks(unavailable.Cards).Count == 1,
                "a genuine collection failure remains explicit without a question mark or amber panel and does not suppress price rank");
            using var unavailableImage = Image.Load<Rgba32>(output.Content!);
            check(Enumerable.Range(540, 90).Any(y => Enumerable.Range(55, 240)
                .Any(x => unavailableImage[x, y].R > 220 && unavailableImage[x, y].G > 220
                    && unavailableImage[x, y].B > 220)),
                "an explicitly requested comparison that fails still renders unavailable ownership quantities");
            var conflicting = new CollectionSnapshot { GathererPrintings = [new(1, 1, 1), new(2, 2, 2)] };
            try
            {
                CollectionSnapshot.Resolve(card with { Prices = new CardPrices { Layout = "normal", GathererIds = [1, 2] } },
                    conflicting.BuildIndex());
                check(false, "conflicting legacy ownership must remain an error");
            }
            catch (InvalidOperationException) { check(true, "conflicting legacy ownership remains an error"); }
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

    private static async Task CheckRelatedOwnershipAsync(Action<bool, string> check, IdentifiedCard card, Stream source)
    {
        var oracle = Guid.Parse("9f8f29ba-7e64-455d-be05-ec7973f2bd0a");
        var old = new CollectionSnapshot { Printings = [new("ltr", "224", 4, 6)],
            GathererPrintings = [new(197881, 2, 2)] };
        var lookup = old.BuildIndex();
        var related = new[] { new CardPrintingReference("ltr", "224", []),
            new CardPrintingReference("ltr", "329", []), new CardPrintingReference("ltr", "224", []) };
        var modern = card with { Set = "hoc", CollectorNumber = "36", Prices = new CardPrices {
            Layout = "normal", OracleId = oracle, RelatedPrintings = related } };
        ScannedCollectionPrinting[] scanned =
        [
            new("hoc", "036", "en", "Sauron, the Dark Lord", 2),
            new("HOC", "36", "ja", "Sauron, the Dark Lord", 1),
            new("ltr", "224", "en", "Sauron, the Dark Lord", 5),
            new("ltr", "329", "en", "Sauron, the Dark Lord", 3),
            new("zzz", "1", "en", "Sauron, the Dark Lord", 100)
        ];
        check(new CombinedCollectionOwnership(old, scanned).Resolve(modern) is { Quantity: 3, AllQuantity: 17 },
            "combined ownership adds scanned copies across languages and canonical printings without summing imported all-set totals twice");
        check(new CombinedCollectionOwnership(null, scanned).Resolve(modern) is { Quantity: 3, AllQuantity: 11 },
            "scanned-only ownership counts related printing keys, not matching names from unrelated printings");
        check(new CombinedCollectionOwnership(old, scanned).Resolve(modern with
            { Set = "wwk", CollectorNumber = "136", Prices = modern.Prices with { GathererIds = [197881], RelatedPrintings = [] } })
            is { Quantity: 2, AllQuantity: 2 },
            "combined ownership preserves uploaded legacy Gatherer quantities without borrowing scanned quantities from other printings");
        check(CollectionSnapshot.Resolve(modern, lookup) is { Quantity: 0, AllQuantity: 6, IsMissing: true },
            "new printing uses canonical siblings for the all-sets total without borrowing their exact quantity or double-counting totals");
        check(CollectionSnapshot.Resolve(modern with { Prices = modern.Prices with {
                RelatedPrintings = [new("wwk", "136", [197881])] } }, lookup)
            is { Quantity: 0, AllQuantity: 2, IsMissing: true },
            "canonical sibling Gatherer IDs recover all-sets totals from legacy-only snapshots");
        var exact = old with { Printings = [new("ltr", "224", 4, 6), new("hoc", "36", 0, 0)] };
        check(CollectionSnapshot.Resolve(modern, exact.BuildIndex()) is { Quantity: 0, AllQuantity: 6 },
            "an explicit zero printing still recovers its all-sets total without changing exact ownership");

        string Printing(string set, string number, Guid id) => JsonSerializer.Serialize(new {
            set, collector_number = number, oracle_id = id, digital = false, multiverse_ids = Array.Empty<int>() });
        string Page(string data, bool more = false, string? next = null) =>
            $"{{\"data\":[{data}],\"has_more\":{more.ToString().ToLowerInvariant()},\"next_page\":{JsonSerializer.Serialize(next)}}}";
        var nextPage = $"https://api.scryfall.com/cards/search?unique=prints&q=oracleid%3A{oracle:D}&page=2";
        foreach (var failure in new[] { "", "http", "wrong-card", "unsafe-page", "repeated-page", "malformed" })
        {
            var searches = 0;
            var multilingual = false;
            using var handler = new RelatedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath != "/cards/search")
                    return JsonSerializer.Serialize(new { set = "hoc", collector_number = "36", lang = "en",
                        oracle_id = oracle, layout = "normal", name = "Sauron, the Dark Lord",
                        prices = new { eur = "3.00", eur_foil = "4.00" } });
                searches++;
                multilingual |= request.RequestUri.Query.Contains("include_multilingual=true");
                if (failure == "http") throw new HttpRequestException("Test catalogue unavailable");
                if (failure == "malformed") return "{\"data\":{},\"has_more\":false}";
                if (failure == "wrong-card") return Page(Printing("ltr", "224", Guid.NewGuid()));
                if (failure == "unsafe-page") return Page("", true, "https://example.invalid/cards/search");
                if (failure == "repeated-page") return Page("", true, request.RequestUri.AbsoluteUri);
                return searches == 1 ? Page(Printing("hoc", "36", oracle), true, nextPage)
                    : Page(Printing("ltr", "224", oracle));
            });
            using var http = new HttpClient(handler);
            var service = new ScanAnnotationService(new ScryfallPriceClient(http), new OutputStore(),
                NullLogger<ScanAnnotationService>.Instance);
            var result = await service.CreateAsync(new ScanResultDocument { JobId = "canonical-ownership",
                Cards = [modern with { Language = "en" }, modern with { Index = 1, Language = "en" }] },
                source, default, old);
            if (failure.Length == 0)
                check(searches == 2 && result.Cards.All(c => c.Ownership is { Quantity: 0, AllQuantity: 6, Error: null }
                    && c.Prices is { Nonfoil: 3m, Foil: 4m, RelatedPrintings.Count: 2 }),
                    "worker follows canonical pagination once per card identity, preserves exact prices and recovers totals without re-uploading the snapshot");
            else
                check(result.Cards.All(c => c.Ownership?.Error is not null) && result.AnnotatedImagePath is not null,
                    $"canonical ownership {failure} failure remains explicit and cannot endorse an invented zero total");
            searches = 0;
            var combined = await service.CreateAsync(new ScanResultDocument
            {
                JobId = "combined-ownership", Cards = [modern with { Language = "en" }, modern with { Index = 1, Language = "en" }]
            }, source, default, old with { Printings = [new("hoc", "36", 2, 6)] }, scanned: scanned);
            if (failure.Length == 0)
                check(multilingual && searches == 2 && combined.Cards.All(c => c.Ownership is { Quantity: 5, AllQuantity: 14, Error: null }),
                    "annotation resolves combined all-sets ownership even with a nonzero imported total, caching multilingual catalogue pagination per identity");
            else
                check(combined.Cards.All(c => c.Ownership?.Error is not null) && combined.AnnotatedImagePath is not null,
                    $"combined ownership {failure} failure stays unknown rather than showing a partial uploaded-only total");
        }
    }

    private sealed class RelatedHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
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
                layout = "normal", name = "Majestic Genesis",
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

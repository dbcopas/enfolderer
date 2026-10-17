using System.Globalization;
using Enfolderer.Ai.Contracts;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

public static class PricedScanRenderer
{
    public static void Render(Stream source, IReadOnlyList<IdentifiedCard> cards, Stream output)
    {
        using var photo = Image.Load<Rgba32>(source);
        var originalWidth = photo.Width;
        var originalHeight = photo.Height;
        if (photo.Width > 2400 || photo.Height > 2400)
            photo.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(2400, 2400), Mode = ResizeMode.Max }));
        var sx = (float)photo.Width / originalWidth;
        var sy = (float)photo.Height / originalHeight;
        var family = SystemFonts.TryGet("DejaVu Sans", out var dejavu) ? dejavu
            : SystemFonts.TryGet("Segoe UI Symbol", out var symbols) ? symbols
            : SystemFonts.Get("Arial");
        var size = Math.Max(12, photo.Width / 100f);
        var font = family.CreateFont(size);
        var rowHeight = (int)Math.Ceiling(size * 8);
        var footerHeight = (int)Math.Ceiling(size * 7);
        // A numbered legend preserves titles, collector lines and artwork even on tightly packed pages.
        using var canvas = new Image<Rgba32>(photo.Width, photo.Height + footerHeight + rowHeight * cards.Count, Color.White);
        canvas.Mutate(c => c.DrawImage(photo, new Point(0, 0), 1));
        var legendCards = OrderLegendCards(cards);
        for (var i = 0; i < legendCards.Count; i++)
        {
            var card = legendCards[i];
            var label = (card.Index + 1).ToString(CultureInfo.InvariantCulture);
            if (card.Quad is { IsValid: true } quad)
            {
                var box = quad.BoundingBox();
                var x = Math.Clamp((float)box.X * sx, 0, Math.Max(0, photo.Width - size * 4));
                var y = Math.Clamp((float)box.Y * sy, 0, Math.Max(0, photo.Height - size * 2));
                var panelWidth = Math.Min((float)box.Width * sx / 2, photo.Width - x);
                var panel = DescribeOverlayPrices(card.Prices);
                var padding = panelWidth * 0.04f;
                var radius = panelWidth * 0.035f;
                var priceHeight = panelWidth * 0.24f;
                var panelHeight = panelWidth * 0.36f;
                var textWidth = panelWidth - padding * 3 - radius * 2;
                var textHeight = priceHeight - padding * 2;
                var priceFont = FitOverlayFont(family, panel, textWidth, textHeight);
                var textBounds = panel.Length == 0 ? default : TextMeasurer.MeasureBounds(panel, new TextOptions(priceFont));
                var panelY = Math.Clamp(y + (float)box.Height * sy * 0.28f, 0, Math.Max(0, photo.Height - panelHeight));
                canvas.Mutate(c => c.Fill(Color.Black.WithAlpha(0.8f),
                        new RectangleF(x, panelY, panelWidth, panelHeight)));
                if (panel.Length > 0)
                    canvas.Mutate(c => c.DrawText(panel, priceFont, Color.White, new PointF(
                        x + padding - textBounds.X, panelY + (priceHeight - textBounds.Height) / 2 - textBounds.Y)));
                var printing = DescribeOverlayPrinting(card);
                if (printing.Length > 0)
                {
                    var printingFont = FitOverlayFont(family, printing, textWidth, panelHeight - priceHeight - padding);
                    printingFont = family.CreateFont(Math.Min(printingFont.Size, priceFont.Size * 0.55f));
                    var printingBounds = TextMeasurer.MeasureBounds(printing, new TextOptions(printingFont));
                    canvas.Mutate(c => c.DrawText(printing, printingFont, Color.White,
                        new PointF(x + padding - printingBounds.X, panelY + priceHeight - printingBounds.Y)));
                }
                canvas.Mutate(c => c.Fill(OverlayConfidenceColor(card), new EllipsePolygon(
                    x + panelWidth - padding - radius, panelY + panelHeight - padding - radius, radius)));
            }
            var top = photo.Height + footerHeight + rowHeight * i;
            var title = $"{label}. {card.Name ?? "Unidentified"} | {card.Set} {card.CollectorNumber} | {card.Language}";
            var text = $"{Fit(title, photo.Width, size)}\n{DescribePriceTarget(card)}\n"
                + $"{DescribePrices(card.Prices, card.Language)}\n{DescribePrintingEvidence(card)}";
            canvas.Mutate(c => c.DrawText(text, font, Color.Black, new PointF(size, top)));
        }
        var retrieved = cards.Select(c => c.Prices?.RetrievedAt).FirstOrDefault(d => d.HasValue);
        var footer = $"Cardmarket via Scryfall | \u20ac daily reference prices, not live offers\n"
            + $"Retrieved: {(retrieved.HasValue ? retrieved.Value.ToString("u") : "unavailable")}. Blank overlay price = unavailable.\n"
            + "Overlay: nonfoil then foil (\u2605), set and number. Legend: nonfoil price or foil fallback, descending.\n"
            + "Circle: green = placed; yellow = needs verification; red = unidentified/low confidence.";
        canvas.Mutate(c => c.DrawText(footer, font, Color.Black, new PointF(size, photo.Height + size / 2)));
        canvas.SaveAsPng(output);
    }

    public static string DescribeOverlayPrices(CardPrices? prices) =>
        string.Join("    ", new[]
        {
            prices?.Nonfoil is { } nonfoil ? OverlayAmount(nonfoil) : null,
            prices?.Foil is { } foil ? $"{OverlayAmount(foil)}\u2605" : null
        }.Where(value => value is not null));

    public static Font FitOverlayFont(FontFamily family, string text, float width, float height)
    {
        if (string.IsNullOrEmpty(text)) return family.CreateFont(height);
        var measured = TextMeasurer.MeasureBounds(text, new TextOptions(family.CreateFont(100)));
        var fontSize = 100 * Math.Min(width / measured.Width, height / measured.Height);
        return family.CreateFont(fontSize);
    }

    public static IReadOnlyList<IdentifiedCard> OrderLegendCards(IReadOnlyList<IdentifiedCard> cards) =>
        cards.OrderByDescending(card => card.Prices?.Nonfoil ?? card.Prices?.Foil).ThenBy(card => card.Index).ToArray();

    public static string DescribeOverlayPrinting(IdentifiedCard card) =>
        string.Join(" ", new[] { card.Set?.ToUpperInvariant(), card.CollectorNumber }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static Color OverlayConfidenceColor(IdentifiedCard card) =>
        !card.IsIdentified ? Color.Red
        : card.PrintingWasPlaced ? Color.LimeGreen
        : card.Confidence < 0.5 ? Color.Red : Color.Gold;

    private static string OverlayAmount(decimal? amount) => amount.HasValue
        ? $"\u20ac{amount.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : "";

    public static string DescribePriceTarget(IdentifiedCard card) => card.HasPrinting
        ? $"Price for {card.Set!.ToUpperInvariant()} {card.CollectorNumber}"
        : "No printing price available";

    public static string DescribePrintingEvidence(IdentifiedCard card)
    {
        if (!card.IsIdentified) return "Unidentified - no valuation";
        if (card.PrintingWasPlaced)
            return card.ArtVerdict?.ToLowerInvariant() switch
            {
                "moved" => "Printing verified by art-based correction",
                "agrees" when card.ArtMargin > 0 => "Printing verified by decisive artwork comparison",
                _ => "Printing supported by catalogue resolution"
            };
        var evidence = card.ArtVerdict?.ToLowerInvariant() switch
        {
            "agrees" => "Artwork matches; set/number not established",
            "inconclusive" when card.ArtReason == "separation_too_small" => "Artwork cannot distinguish printings",
            "inconclusive" => "Artwork comparison inconclusive",
            _ => "No conclusive artwork comparison"
        };
        return $"Printing needs verification\n{evidence}";
    }

    public static string DescribePrices(CardPrices? prices, string? photographedLanguage = null) =>
        $"Nonfoil: {Amount(prices?.Nonfoil, prices?.NonfoilLanguage, photographedLanguage)} | "
        + $"Foil: {Amount(prices?.Foil, prices?.FoilLanguage, photographedLanguage)}"
        + (prices?.Foil.HasValue == true ? " \u2605" : "")
        + (prices?.Error is null ? "" : " | Lookup incomplete");

    private static string Amount(decimal? amount, string? language, string? photographedLanguage = null) => amount.HasValue
        ? $"\u20ac{amount.Value.ToString("0.00", CultureInfo.InvariantCulture)} [{language}"
            + (language == "en" && photographedLanguage is not null and not "en" ? " fallback]" : "]")
        : "";

    private static string Fit(string value, int width, float size)
    {
        var max = Math.Max(12, (int)(width / size * 1.5f));
        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }
}

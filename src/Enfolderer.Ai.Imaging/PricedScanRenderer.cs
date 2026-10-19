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
        using var canvas = new Image<Rgba32>(photo.Width, photo.Height);
        canvas.Mutate(c => c.DrawImage(photo, new Point(0, 0), 1));
        var highest = CardValueRanking.HighestPriced(cards);
        var ranks = CardValueRanking.Ranks(cards);
        foreach (var card in cards)
        {
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
                var panelHeight = panelWidth * 0.90f;
                var textWidth = panelWidth - padding * 3 - radius * 2;
                var textHeight = priceHeight - padding * 2;
                var priceFont = FitOverlayFont(family, panel, textWidth, textHeight);
                var textBounds = panel.Length == 0 ? default : TextMeasurer.MeasureBounds(panel, new TextOptions(priceFont));
                var panelY = Math.Clamp(y + (float)box.Height * sy * 0.28f, 0, Math.Max(0, photo.Height - panelHeight));
                var panelColor = OverlayBackgroundColor(card);
                canvas.Mutate(c => c.Fill(panelColor.WithAlpha(0.8f),
                        new RectangleF(x, panelY, panelWidth, panelHeight)));
                if (panel.Length > 0)
                    canvas.Mutate(c => c.DrawText(panel, priceFont, Color.White, new PointF(
                        x + padding - textBounds.X, panelY + (priceHeight - textBounds.Height) / 2 - textBounds.Y)));
                var title = DescribeOverlayName(card);
                if (title.Length > 0)
                {
                    var titleFont = FitOverlayFont(family, title, textWidth, panelWidth * 0.14f - padding);
                    var titleBounds = TextMeasurer.MeasureBounds(title, new TextOptions(titleFont));
                    canvas.Mutate(c => c.DrawText(title, titleFont, Color.White,
                        new PointF(x + padding - titleBounds.X, panelY + priceHeight - titleBounds.Y)));
                }
                var printing = DescribeOverlayPrinting(card);
                if (printing.Length > 0)
                {
                    var printingFont = FitOverlayFont(family, printing, textWidth, panelWidth * 0.12f - padding);
                    var printingBounds = TextMeasurer.MeasureBounds(printing, new TextOptions(printingFont));
                    canvas.Mutate(c => c.DrawText(printing, printingFont, Color.White,
                        new PointF(x + padding - printingBounds.X, panelY + panelWidth * 0.39f - printingBounds.Y)));
                }
                if (card.Ownership is { } ownership)
                {
                    var ownershipText = ownership.OverlayText;
                    var ownershipFont = FitOverlayFont(family, ownershipText, textWidth, panelWidth * 0.24f);
                    var ownershipBounds = TextMeasurer.MeasureBounds(ownershipText, new TextOptions(ownershipFont));
                    canvas.Mutate(c => c.DrawText(ownershipText, ownershipFont, Color.White,
                        new PointF(x + padding - ownershipBounds.X, panelY + panelWidth * 0.58f - ownershipBounds.Y)));
                }
                if (card != highest && ranks.TryGetValue(card.Index, out var rank))
                {
                    var rankText = rank.ToString(CultureInfo.InvariantCulture);
                    var rankFont = FitOverlayFont(family, rankText, radius * 2, radius * 2);
                    var rankBounds = TextMeasurer.MeasureBounds(rankText, new TextOptions(rankFont));
                    canvas.Mutate(c => c.DrawText(rankText, rankFont, Color.White,
                        new PointF(x + panelWidth - padding - radius * 2 - rankBounds.X,
                            panelY + padding - rankBounds.Y)));
                }
                if (card == highest)
                {
                    var trophyX = x + panelWidth - padding - radius * 2;
                    var trophyY = panelY + padding;
                    var unit = radius * 2;
                    canvas.Mutate(c =>
                    {
                        c.Fill(Color.Gold, new Polygon(new LinearLineSegment(
                            new PointF(trophyX, trophyY), new PointF(trophyX + unit, trophyY),
                            new PointF(trophyX + unit * 0.75f, trophyY + unit * 0.7f),
                            new PointF(trophyX + unit * 0.25f, trophyY + unit * 0.7f))));
                        c.Fill(Color.Gold, new RectangleF(trophyX + unit * 0.4f, trophyY + unit * 0.6f, unit * 0.2f, unit * 0.5f));
                        c.Fill(Color.Gold, new RectangleF(trophyX + unit * 0.15f, trophyY + unit, unit * 0.7f, unit * 0.2f));
                    });
                }
                canvas.Mutate(c => c.Fill(OverlayConfidenceColor(card), new EllipsePolygon(
                    x + panelWidth - padding - radius, panelY + panelHeight - padding - radius, radius)));
            }
        }
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

    public static string DescribeOverlayPrinting(IdentifiedCard card) =>
        string.Join(" ", new[] { card.Set?.ToUpperInvariant(), card.CollectorNumber }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static string DescribeOverlayName(IdentifiedCard card) =>
        string.Equals(card.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase)
            ? "" : card.Name ?? "Unidentified";

    public static Color OverlayBackgroundColor(IdentifiedCard card) =>
        card.Ownership?.IsMissing == true ? Color.FromRgb(160, 25, 25) : Color.Black;

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

}

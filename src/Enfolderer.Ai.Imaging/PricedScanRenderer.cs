using System.Globalization;
using Enfolderer.Ai.Contracts;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
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
            : SystemFonts.Get("Arial");
        var size = Math.Max(12, photo.Width / 100f);
        var font = family.CreateFont(size);
        var rowHeight = (int)Math.Ceiling(size * 8);
        var footerHeight = (int)Math.Ceiling(size * 4);
        // A numbered legend preserves titles, collector lines and artwork even on tightly packed pages.
        using var canvas = new Image<Rgba32>(photo.Width, photo.Height + footerHeight + rowHeight * cards.Count, Color.White);
        canvas.Mutate(c => c.DrawImage(photo, new Point(0, 0), 1));
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var label = (card.Index + 1).ToString(CultureInfo.InvariantCulture);
            if (card.Quad is { IsValid: true } quad)
            {
                var box = quad.BoundingBox();
                var x = Math.Clamp((float)box.X * sx, 0, Math.Max(0, photo.Width - size * 4));
                var y = Math.Clamp((float)box.Y * sy, 0, Math.Max(0, photo.Height - size * 2));
                canvas.Mutate(c => c.Fill(Color.Black, new RectangleF(x, y, size * 4, size * 2))
                    .DrawText(label, font, Color.White, new PointF(x + size / 2, y + size / 4)));
                var panelWidth = Math.Min((float)box.Width * sx, photo.Width - x);
                var priceFont = family.CreateFont(Math.Max(8, panelWidth / 36));
                var panelY = Math.Clamp(y + (float)box.Height * sy * 0.28f, 0, Math.Max(0, photo.Height - priceFont.Size * 6));
                var panel = $"#{label} {DescribePriceTarget(card)}"
                    + $"\nNF {Amount(card.Prices?.Nonfoil, card.Prices?.NonfoilLanguage, card.Language)}"
                    + $"\nFoil {Amount(card.Prices?.Foil, card.Prices?.FoilLanguage, card.Language)}"
                    + (card.IsIdentified && !card.PrintingWasPlaced ? "\nPrinting needs verification" : "");
                canvas.Mutate(c => c.Fill(Color.Black.WithAlpha(0.8f),
                        new RectangleF(x, panelY, panelWidth, priceFont.Size * 5.5f))
                    .DrawText(panel, priceFont, Color.White, new PointF(x + 2, panelY + 2)));
            }
            var top = photo.Height + footerHeight + rowHeight * i;
            var title = $"{label}. {card.Name ?? "Unidentified"} | {card.Set} {card.CollectorNumber} | {card.Language}";
            var text = $"{Fit(title, photo.Width, size)}\n{DescribePriceTarget(card)}\n"
                + $"{DescribePrices(card.Prices, card.Language)}\n{DescribePrintingEvidence(card)}\n"
                + $"Finish: {card.Finish ?? "unknown"}";
            canvas.Mutate(c => c.DrawText(text, font, Color.Black, new PointF(size, top)));
        }
        var retrieved = cards.Select(c => c.Prices?.RetrievedAt).FirstOrDefault(d => d.HasValue);
        var footer = $"Cardmarket via Scryfall | EUR daily reference prices, not live offers\n"
            + $"Retrieved: {(retrieved.HasValue ? retrieved.Value.ToString("u") : "unavailable")}. N/A = no price.";
        canvas.Mutate(c => c.DrawText(footer, font, Color.Black, new PointF(size, photo.Height + size / 2)));
        canvas.SaveAsPng(output);
    }

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
        + (prices?.Error is null ? "" : " | Lookup incomplete");

    private static string Amount(decimal? amount, string? language, string? photographedLanguage = null) => amount.HasValue
        ? $"EUR {amount.Value.ToString("0.00", CultureInfo.InvariantCulture)} [{language}"
            + (language == "en" && photographedLanguage is not null and not "en" ? " fallback]" : "]")
        : "N/A";

    private static string Fit(string value, int width, float size)
    {
        var max = Math.Max(12, (int)(width / size * 1.5f));
        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }
}

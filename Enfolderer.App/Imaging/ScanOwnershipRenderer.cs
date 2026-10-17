using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Enfolderer.Ai.Contracts;
using Enfolderer.App.Collection;

namespace Enfolderer.App.Imaging;

public static class ScanOwnershipRenderer
{
    public static IReadOnlyList<string> Render(string imagePath, ScanResultDocument result, ScanOwnershipIndex? inventory)
    {
        if (result.ImageWidth <= 0 || result.ImageHeight <= 0)
            throw new InvalidDataException("Scan image dimensions are missing; ownership geometry cannot be aligned.");
        BitmapFrame image;
        using (var input = File.OpenRead(imagePath))
            image = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var scale = (double)image.PixelWidth / result.ImageWidth;
        var photoHeight = result.ImageHeight * scale;
        var size = Math.Max(12, image.PixelWidth / 100d);
        var warnings = new List<string>();
        var ownedCards = new List<IdentifiedCard>();
        foreach (var card in result.Cards.Where(c => c.Game == CardGames.Magic))
        {
            CardOwnership ownership;
            try
            {
                if (inventory is null) throw new InvalidOperationException("Local collection is unavailable.");
                var local = inventory.Resolve(card);
                ownership = new(local.PrintingQuantity, local.NameQuantity, local.RequiredQuantity);
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"Card {card.Index} ({card.Set} {card.CollectorNumber}): {ex.Message}");
                ownership = new(0, 0, 1, ex.Message);
            }
            ownedCards.Add(card with { Ownership = ownership });
        }
        var highest = CardValueRanking.HighestNeeded(ownedCards);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            foreach (var card in ownedCards)
            {
                var ownership = card.Ownership!;
                if (card.Quad is not { IsValid: true } quad)
                {
                    warnings.Add($"{card.Set} {card.CollectorNumber}: ownership geometry is unavailable.");
                    continue;
                }
                var box = quad.BoundingBox();
                var x = Math.Clamp(box.X * scale, 0, Math.Max(0, image.PixelWidth - size * 4));
                var y = Math.Clamp(box.Y * scale, 0, Math.Max(0, photoHeight - size * 2));
                var width = Math.Min(box.Width * scale / 2, image.PixelWidth - x);
                var top = Math.Clamp(y + box.Height * scale * 0.28, 0, Math.Max(0, photoHeight - width * 0.58));
                if (ownership.Error is not null || ownership.IsMissing)
                {
                    var tint = ownership.Error is not null ? Color.FromArgb(96, 255, 165, 0) : Color.FromArgb(96, 255, 20, 147);
                    drawing.DrawRectangle(new SolidColorBrush(tint), null, new Rect(x, top, width, width * 0.58));
                }
                if (ownership.Error is not null || !ownership.IsMissing)
                {
                    var padding = width * 0.04;
                    var height = width * 0.14;
                    var text = new FormattedText(ownership.OverlayText, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 100, Brushes.White, 1);
                    text.SetFontSize(100 * Math.Min((width - padding * 2) / text.Width,
                        (height - padding) / text.Height));
                    var panelY = Math.Min(top + width * 0.58, photoHeight - height);
                    var background = ownership.Error is not null ? Color.FromArgb(230, 120, 70, 0) : Color.FromArgb(204, 0, 0, 0);
                    drawing.DrawRectangle(new SolidColorBrush(background), null,
                        new Rect(x, panelY, width, height));
                    drawing.DrawText(text, new Point(x + padding, panelY + (height - text.Height) / 2));
                }
                if (card == highest)
                {
                    var unit = width * 0.07;
                    var left = x + width - width * 0.04 - unit;
                    var trophyTop = top + width * 0.04;
                    var cup = new StreamGeometry();
                    using (var geometry = cup.Open())
                    {
                        geometry.BeginFigure(new Point(left, trophyTop), true, true);
                        geometry.LineTo(new Point(left + unit, trophyTop), true, false);
                        geometry.LineTo(new Point(left + unit * 0.75, trophyTop + unit * 0.7), true, false);
                        geometry.LineTo(new Point(left + unit * 0.25, trophyTop + unit * 0.7), true, false);
                    }
                    drawing.DrawGeometry(Brushes.Gold, null, cup);
                    drawing.DrawRectangle(Brushes.Gold, null,
                        new Rect(left + unit * 0.4, trophyTop + unit * 0.6, unit * 0.2, unit * 0.5));
                    drawing.DrawRectangle(Brushes.Gold, null,
                        new Rect(left + unit * 0.15, trophyTop + unit, unit * 0.7, unit * 0.2));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        File.WriteAllBytes(imagePath, encoded.ToArray());
        return warnings;
    }
}

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
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            foreach (var card in result.Cards.Where(c => c.Game == CardGames.Magic))
            {
                ScanOwnership? ownership = null;
                if (inventory is not null)
                {
                    try { ownership = inventory.Resolve(card); }
                    catch (InvalidOperationException ex)
                    {
                        warnings.Add($"Card {card.Index} ({card.Set} {card.CollectorNumber}): {ex.Message}");
                    }
                }
                if (card.Quad is not { IsValid: true } quad)
                {
                    warnings.Add($"{card.Set} {card.CollectorNumber}: ownership geometry is unavailable.");
                    continue;
                }
                var box = quad.BoundingBox();
                var x = Math.Clamp(box.X * scale, 0, Math.Max(0, image.PixelWidth - size * 4));
                var y = Math.Clamp(box.Y * scale, 0, Math.Max(0, photoHeight - size * 2));
                var width = Math.Min(box.Width * scale / 2, image.PixelWidth - x);
                var top = Math.Clamp(y + box.Height * scale * 0.28, 0, Math.Max(0, photoHeight - width * 0.36));
                if (ownership is null || ownership.IsMissing)
                {
                    var tint = ownership is null ? Color.FromArgb(96, 255, 165, 0) : Color.FromArgb(96, 255, 20, 147);
                    drawing.DrawRectangle(new SolidColorBrush(tint), null, new Rect(x, top, width, width * 0.36));
                }
                if (ownership is null || !ownership.IsMissing)
                {
                    var padding = width * 0.04;
                    var height = width * 0.14;
                    var text = new FormattedText(ownership?.OverlayText ?? "Owned: ?", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 100, Brushes.White, 1);
                    text.SetFontSize(100 * Math.Min((width - padding * 2) / text.Width,
                        (height - padding) / text.Height));
                    var panelY = Math.Min(top + width * 0.36, photoHeight - height);
                    var background = ownership is null ? Color.FromArgb(230, 120, 70, 0) : Color.FromArgb(204, 0, 0, 0);
                    drawing.DrawRectangle(new SolidColorBrush(background), null,
                        new Rect(x, panelY, width, height));
                    drawing.DrawText(text, new Point(x + padding, panelY + (height - text.Height) / 2));
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

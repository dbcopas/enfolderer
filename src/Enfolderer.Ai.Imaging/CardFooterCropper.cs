using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

public static class CardFooterCropper
{
    /// <summary>
    /// Magnifies the full-width footer, including retro-frame copyright/collector lines.
    /// For a sideways crop, both quarter-turn footers are supplied rather than guessing its top.
    /// </summary>
    public static byte[] CropToPng(ReadOnlyMemory<byte> cardImage)
    {
        using var image = Image.Load<Rgba32>(cardImage.Span);
        if (image.Width > image.Height)
        {
            using var first = image.Clone(c => c.Rotate(RotateMode.Rotate90));
            using var second = image.Clone(c => c.Rotate(RotateMode.Rotate270));
            using var firstFooter = Enlarge(first);
            using var secondFooter = Enlarge(second);
            using var stacked = new Image<Rgba32>(1200, firstFooter.Height + secondFooter.Height);
            stacked.Mutate(c => c.DrawImage(firstFooter, new Point(0, 0), 1f)
                .DrawImage(secondFooter, new Point(0, firstFooter.Height), 1f));
            return Encode(stacked);
        }
        using var footer = Enlarge(image);
        return Encode(footer);
    }

    private static Image<Rgba32> Enlarge(Image<Rgba32> image)
    {
        var top = (int)Math.Floor(image.Height * 0.75);
        var height = image.Height - top;
        return image.Clone(c => c.Crop(new Rectangle(0, top, image.Width, height))
            .Resize(1200, Math.Max(1, (int)Math.Round(1200d * height / image.Width)), KnownResamplers.NearestNeighbor));
    }

    private static byte[] Encode(Image<Rgba32> image)
    {
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }
}

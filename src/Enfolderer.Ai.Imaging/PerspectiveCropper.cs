using Enfolderer.Ai.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Enfolderer.Ai.Imaging;

/// <summary>Pixel size of a decoded image.</summary>
public sealed record ImageDimensions(int Width, int Height);

/// <summary>
/// Perspective-correct extraction of a card from a photo. The boundary agent only reports geometry;
/// this code performs the actual warp, which keeps Team A out of the crop-writing path.
/// </summary>
public static class PerspectiveCropper
{
    /// <summary>Aspect ratio (width / height) of a standard trading card (2.5" x 3.5").</summary>
    public const double StandardCardAspect = 2.5 / 3.5;

    /// <summary>Longest edge of a produced crop, in pixels.</summary>
    public const int DefaultOutputHeight = 1024;

    public static ImageDimensions ReadDimensions(Stream image)
    {
        var info = Image.Identify(image);
        if (image.CanSeek) image.Seek(0, SeekOrigin.Begin);
        return new ImageDimensions(info.Width, info.Height);
    }

    /// <summary>
    /// Warps the quadrilateral <paramref name="quad"/> out of <paramref name="source"/> into an
    /// upright rectangle and writes it to <paramref name="destination"/> as PNG.
    /// </summary>
    public static ImageDimensions CropToPng(Stream source, CardQuad quad, Stream destination, int outputHeight = DefaultOutputHeight)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (!quad.IsValid)
            throw new ArgumentException($"A quad must have exactly {CardQuad.RequiredPointCount} points.", nameof(quad));
        if (outputHeight < 16)
            throw new ArgumentOutOfRangeException(nameof(outputHeight), "Output height must be at least 16 pixels.");

        using var image = Image.Load<Rgba32>(source);

        var (width, height) = ChooseOutputSize(quad, outputHeight);
        using var output = new Image<Rgba32>(width, height);

        var homography = Homography.FromUnitSquare(quad);

        for (var y = 0; y < height; y++)
        {
            // v runs 0..1 down the card; u runs 0..1 across it.
            var v = height == 1 ? 0d : (double)y / (height - 1);
            for (var x = 0; x < width; x++)
            {
                var u = width == 1 ? 0d : (double)x / (width - 1);
                var (sx, sy) = homography.Project(u, v);
                output[x, y] = SampleBilinear(image, sx, sy);
            }
        }

        output.SaveAsPng(destination);
        if (destination.CanSeek) destination.Seek(0, SeekOrigin.Begin);
        return new ImageDimensions(width, height);
    }

    /// <summary>
    /// Picks an output size from the quad's own edge lengths so a landscape-oriented card is not
    /// squeezed into portrait, falling back to the standard card aspect for degenerate quads.
    /// </summary>
    internal static (int Width, int Height) ChooseOutputSize(CardQuad quad, int outputHeight)
    {
        var topEdge = Distance(quad.Points[0], quad.Points[1]);
        var bottomEdge = Distance(quad.Points[3], quad.Points[2]);
        var leftEdge = Distance(quad.Points[0], quad.Points[3]);
        var rightEdge = Distance(quad.Points[1], quad.Points[2]);

        var avgWidth = (topEdge + bottomEdge) / 2d;
        var avgHeight = (leftEdge + rightEdge) / 2d;

        var aspect = avgHeight > 0.0001 ? avgWidth / avgHeight : StandardCardAspect;
        if (double.IsNaN(aspect) || aspect <= 0.05 || aspect >= 20) aspect = StandardCardAspect;

        var width = (int)Math.Round(outputHeight * aspect);
        return (Math.Max(16, width), outputHeight);
    }

    private static double Distance(ImagePoint a, ImagePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Rgba32 SampleBilinear(Image<Rgba32> image, double x, double y)
    {
        var maxX = image.Width - 1;
        var maxY = image.Height - 1;

        var cx = Math.Clamp(x, 0, maxX);
        var cy = Math.Clamp(y, 0, maxY);

        var x0 = (int)Math.Floor(cx);
        var y0 = (int)Math.Floor(cy);
        var x1 = Math.Min(x0 + 1, maxX);
        var y1 = Math.Min(y0 + 1, maxY);

        var fx = cx - x0;
        var fy = cy - y0;

        var p00 = image[x0, y0];
        var p10 = image[x1, y0];
        var p01 = image[x0, y1];
        var p11 = image[x1, y1];

        static byte Mix(byte a, byte b, byte c, byte d, double fx, double fy)
        {
            var top = a + (b - a) * fx;
            var bottom = c + (d - c) * fx;
            return (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
        }

        return new Rgba32(
            Mix(p00.R, p10.R, p01.R, p11.R, fx, fy),
            Mix(p00.G, p10.G, p01.G, p11.G, fx, fy),
            Mix(p00.B, p10.B, p01.B, p11.B, fx, fy),
            Mix(p00.A, p10.A, p01.A, p11.A, fx, fy));
    }
}

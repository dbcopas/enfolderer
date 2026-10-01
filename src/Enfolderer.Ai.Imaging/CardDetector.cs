using Enfolderer.Ai.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

/// <summary>Tuning for <see cref="CardDetector"/>. The defaults suit a phone photo of a binder page.</summary>
public sealed record CardDetectorOptions
{
    /// <summary>Longest edge, in pixels, the detector works at. Larger is slower, not better.</summary>
    public int WorkingSize { get; init; } = 1000;

    /// <summary>Fraction of pixels kept as edge. Raise it for low-contrast surfaces.</summary>
    public double EdgeQuantile { get; init; } = 0.90;

    /// <summary>Smallest card, as a fraction of the image area.</summary>
    public double MinAreaFraction { get; init; } = 0.004;

    /// <summary>Largest card, as a fraction of the image area.</summary>
    public double MaxAreaFraction { get; init; } = 0.60;

    /// <summary>
    /// How much of its own bounding rectangle a blob must fill to count as a card. A card is
    /// convex, so a low ratio means the blob is a shadow, a hand, or two cards merged by a gap the
    /// edge detector missed.
    /// </summary>
    public double MinRectangularity { get; init; } = 0.72;

    /// <summary>
    /// Allowed deviation from the 2.5 x 3.5 card ratio, as a multiplier either way. Perspective
    /// foreshortens a tilted card, so this is generous — but not so generous that it admits a
    /// square or a circle, whose 1.0 ratio sits just outside. A card tilted far enough to look
    /// square is too distorted to identify anyway.
    /// </summary>
    public double AspectTolerance { get; init; } = 1.35;
}

/// <summary>One card located by <see cref="CardDetector"/>.</summary>
/// <param name="Quad">Corners in source-image pixels, ordered top-left, top-right, bottom-right, bottom-left.</param>
/// <param name="Confidence">How card-like the blob was: rectangularity blended with aspect agreement.</param>
public sealed record DetectedCard(CardQuad Quad, double Confidence);

/// <summary>
/// Finds trading cards in a photograph geometrically, with no model involved.
/// <para>
/// This is Team A's actual skill. A chat model has no detection head, so asking one for corner
/// coordinates makes it guess; edge detection measures them. The model's job is the part this
/// cannot do — deciding which blobs are really cards, which way up they are, and which game they
/// belong to.
/// </para>
/// <para>
/// The pipeline is the classic one: gradient magnitude, threshold, flood the background in from the
/// border, and treat everything the background cannot reach as a card. Flooding from the border is
/// what makes busy card art harmless: edges inside a card are holes in a region the background
/// never enters, so they are filled for free rather than fragmenting it.
/// </para>
/// </summary>
public static class CardDetector
{
    /// <summary>Aspect ratio (short side / long side) of a trading card.</summary>
    private const double CardAspect = PerspectiveCropper.StandardCardAspect;

    public static IReadOnlyList<DetectedCard> Detect(Stream image, CardDetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var opts = options ?? new CardDetectorOptions();

        using var source = Image.Load<Rgba32>(image);
        var fullWidth = source.Width;
        var fullHeight = source.Height;
        if (fullWidth < 8 || fullHeight < 8) return [];

        // Everything below runs on the downscaled copy; the quads are scaled back at the end. The
        // detail that matters here is a card's outline, which survives downscaling, and working
        // small keeps a 12 megapixel phone photo to well under a second.
        var scale = Math.Min(1d, (double)opts.WorkingSize / Math.Max(fullWidth, fullHeight));
        var width = Math.Max(8, (int)Math.Round(fullWidth * scale));
        var height = Math.Max(8, (int)Math.Round(fullHeight * scale));

        using var working = source.Clone(c => c.Resize(width, height));

        var luminance = ToLuminance(working, width, height);
        var edges = EdgeMask(luminance, width, height, opts.EdgeQuantile);
        Dilate(edges, width, height);

        var cardMask = FloodBackground(edges, width, height);
        var blobs = LabelBlobs(cardMask, width, height);

        var results = new List<DetectedCard>();
        double imageArea = (double)width * height;

        foreach (var blob in blobs)
        {
            if (blob.Count < imageArea * opts.MinAreaFraction) continue;
            if (blob.Count > imageArea * opts.MaxAreaFraction) continue;

            var rect = MinimumAreaRectangle(ConvexHull(blob.Points));
            if (rect is null) continue;

            var (corners, shortSide, longSide) = rect.Value;
            if (shortSide < 4 || longSide < 4) continue;

            var rectArea = shortSide * longSide;
            var rectangularity = blob.Count / rectArea;
            if (rectangularity < opts.MinRectangularity) continue;

            var aspect = shortSide / longSide;
            var aspectError = Math.Max(aspect / CardAspect, CardAspect / aspect);
            if (aspectError > opts.AspectTolerance) continue;

            // Scaled back to the original image, because the caller's quad is in source pixels and
            // the crop is taken from the full-resolution photograph, not from this working copy.
            var points = corners
                .Select(p => new ImagePoint(p.X / scale, p.Y / scale))
                .ToList();

            var confidence = Math.Clamp(
                Math.Min(1d, rectangularity) * (1d / aspectError),
                0d, 1d);

            results.Add(new DetectedCard(new CardQuad(points), Math.Round(confidence, 3)));
        }

        // Largest first, so a caller that trusts only the first few gets the clearest cards.
        return results
            .OrderByDescending(r => Area(r.Quad))
            .ToList();
    }

    private static double Area(CardQuad quad)
    {
        var (_, _, w, h) = quad.BoundingBox();
        return w * h;
    }

    private static byte[] ToLuminance(Image<Rgba32> image, int width, int height)
    {
        var luminance = new byte[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    luminance[y * width + x] = (byte)((p.R * 299 + p.G * 587 + p.B * 114) / 1000);
                }
            }
        });
        return luminance;
    }

    /// <summary>
    /// Sobel gradient magnitude, thresholded at a quantile rather than a fixed value so that a dim
    /// photo and a bright one yield a comparable amount of edge.
    /// </summary>
    private static bool[] EdgeMask(byte[] luminance, int width, int height, double quantile)
    {
        var magnitude = new int[width * height];
        var histogram = new int[1024];

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;

                var tl = luminance[i - width - 1];
                var t = luminance[i - width];
                var tr = luminance[i - width + 1];
                var l = luminance[i - 1];
                var r = luminance[i + 1];
                var bl = luminance[i + width - 1];
                var b = luminance[i + width];
                var br = luminance[i + width + 1];

                var gx = (tr + 2 * r + br) - (tl + 2 * l + bl);
                var gy = (bl + 2 * b + br) - (tl + 2 * t + tr);

                var m = Math.Min(1023, (Math.Abs(gx) + Math.Abs(gy)) / 4);
                magnitude[i] = m;
                histogram[m]++;
            }
        }

        var interior = (width - 2) * (height - 2);
        var target = (int)(interior * quantile);
        var threshold = 0;
        var running = 0;
        for (var v = 0; v < histogram.Length; v++)
        {
            running += histogram[v];
            if (running >= target) { threshold = v; break; }
        }

        // A perfectly flat image yields threshold 0, which would mark everything as edge.
        threshold = Math.Max(threshold, 8);

        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++) mask[i] = magnitude[i] >= threshold;
        return mask;
    }

    /// <summary>
    /// Grows the edge mask by one pixel so that a card outline broken by glare or a soft shadow
    /// still encloses its card. Without this the background leaks in through the gap and the card
    /// is never found.
    /// </summary>
    private static void Dilate(bool[] mask, int width, int height)
    {
        var copy = (bool[])mask.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                if (copy[i]) continue;
                if (copy[i - 1] || copy[i + 1] || copy[i - width] || copy[i + width])
                    mask[i] = true;
            }
        }
    }

    /// <summary>
    /// Floods the non-edge space inward from the image border and returns everything it could not
    /// reach. Holes inside a card are unreachable, so they are filled without a separate pass.
    /// </summary>
    private static bool[] FloodBackground(bool[] edges, int width, int height)
    {
        var background = new bool[width * height];
        var queue = new Queue<int>();

        void Seed(int x, int y)
        {
            var i = y * width + x;
            if (edges[i] || background[i]) return;
            background[i] = true;
            queue.Enqueue(i);
        }

        for (var x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
        for (var y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % width;
            var y = i / width;

            if (x > 0) Seed(x - 1, y);
            if (x < width - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < height - 1) Seed(x, y + 1);
        }

        var card = new bool[width * height];
        for (var i = 0; i < card.Length; i++) card[i] = !background[i];
        return card;
    }

    private sealed record Blob(int Count, List<(int X, int Y)> Points);

    /// <summary>Four-connected labelling of the card mask.</summary>
    private static List<Blob> LabelBlobs(bool[] mask, int width, int height)
    {
        var seen = new bool[mask.Length];
        var blobs = new List<Blob>();
        var queue = new Queue<int>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;

            var points = new List<(int X, int Y)>();
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                var x = i % width;
                var y = i / width;
                points.Add((x, y));

                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                    var n = ny * width + nx;
                    if (!mask[n] || seen[n]) return;
                    seen[n] = true;
                    queue.Enqueue(n);
                }

                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            blobs.Add(new Blob(points.Count, points));
        }

        return blobs;
    }

    /// <summary>Andrew's monotone chain, counter-clockwise, excluding collinear points.</summary>
    private static List<(double X, double Y)> ConvexHull(List<(int X, int Y)> points)
    {
        var sorted = points
            .Select(p => ((double)p.X, (double)p.Y))
            .Distinct()
            .OrderBy(p => p.Item1).ThenBy(p => p.Item2)
            .ToList();

        if (sorted.Count < 3) return sorted;

        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<(double X, double Y)>();

        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        var lowerCount = hull.Count + 1;
        for (var i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>
    /// Smallest-area enclosing rectangle by rotating calipers. The minimum always shares an edge
    /// with the hull, so trying every hull edge as the rectangle's axis finds it exactly.
    /// </summary>
    private static ((double X, double Y)[] Corners, double ShortSide, double LongSide)?
        MinimumAreaRectangle(List<(double X, double Y)> hull)
    {
        if (hull.Count < 3) return null;

        var bestArea = double.MaxValue;
        (double X, double Y)[]? bestCorners = null;
        double bestShort = 0, bestLong = 0;

        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];

            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var length = Math.Sqrt(ex * ex + ey * ey);
            if (length < 1e-9) continue;

            // Unit vectors along the candidate edge and perpendicular to it.
            var ux = ex / length;
            var uy = ey / length;
            var vx = -uy;
            var vy = ux;

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            foreach (var p in hull)
            {
                var u = p.X * ux + p.Y * uy;
                var v = p.X * vx + p.Y * vy;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            var w = maxU - minU;
            var h = maxV - minV;
            var area = w * h;
            if (area >= bestArea) continue;

            bestArea = area;
            bestShort = Math.Min(w, h);
            bestLong = Math.Max(w, h);

            (double X, double Y) At(double u, double v) => (u * ux + v * vx, u * uy + v * vy);

            bestCorners =
            [
                At(minU, minV),
                At(maxU, minV),
                At(maxU, maxV),
                At(minU, maxV)
            ];
        }

        return bestCorners is null ? null : (OrderAsCard(bestCorners), bestShort, bestLong);
    }

    /// <summary>
    /// Puts the four corners in the order the pipeline expects: top-left, top-right, bottom-right,
    /// bottom-left, with the card's short side as the top so the crop comes out portrait.
    /// <para>
    /// Geometry cannot tell which end of a card is its top — that is printed on the face, not in
    /// its shape — so this picks the upright of the two that sits closest to the top of the photo.
    /// Being wrong means a crop rotated by 180 degrees, which is exactly the kind of judgement the
    /// agent reviewing these quads is for.
    /// </para>
    /// </summary>
    private static (double X, double Y)[] OrderAsCard((double X, double Y)[] corners)
    {
        static double Distance((double X, double Y) a, (double X, double Y) b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Rotate the cycle so that corners[0]->corners[1] is a short side, making it the top edge.
        var side01 = Distance(corners[0], corners[1]);
        var side12 = Distance(corners[1], corners[2]);
        var ordered = side01 <= side12
            ? corners
            : [corners[1], corners[2], corners[3], corners[0]];

        // Of the two ways round, prefer the one whose top edge is higher up the photo.
        var topMidpoint = (ordered[0].Y + ordered[1].Y) / 2d;
        var bottomMidpoint = (ordered[2].Y + ordered[3].Y) / 2d;
        if (topMidpoint > bottomMidpoint)
            ordered = [ordered[2], ordered[3], ordered[0], ordered[1]];

        return ordered;
    }
}

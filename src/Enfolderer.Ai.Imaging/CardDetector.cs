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

    /// <summary>Largest single card, as a fraction of the image area.</summary>
    /// <remarks>
    /// Applied per card, after a merged block has been split. A tidy page of nine cards is one
    /// blob covering most of the photo, and rejecting it wholesale is what loses all nine.
    /// </remarks>
    public double MaxAreaFraction { get; init; } = 0.60;

    /// <summary>Most cards a merged block may be split into along one axis.</summary>
    public int MaxGridSpan { get; init; } = 6;

    /// <summary>
    /// Fraction of a candidate seam that must actually be edge for the seam to count as real.
    /// <para>
    /// This is the whole basis for telling one card from a block of them: nine cards in a 3 x 3
    /// arrangement have exactly the proportions of one card, so shape alone cannot distinguish
    /// them. The seams between them can.
    /// </para>
    /// </summary>
    public double MinSeamCoverage { get; init; } = 0.65;

    /// <summary>
    /// Least coverage any candidate seam may show before the arrangement is rejected outright.
    /// <para>
    /// Two touching cards of the same colour leave no visible seam, so a real arrangement can have
    /// a seam that is largely blank and still be correct. A division running through the middle of
    /// a card, however, crosses unbroken artwork and shows almost nothing anywhere along its
    /// length; this is the bar that separates the two.
    /// </para>
    /// </summary>
    public double MinSeamEvidence { get; init; } = 0.30;

    /// <summary>
    /// Shortest side, in working pixels, that a cell of a split block may have.
    /// <para>
    /// This is not really a geometric limit but a legibility one. Splitting only pays off if the
    /// pieces can still be identified as cards, and below roughly this size no title or set symbol
    /// survives. It also stops ruled artwork from being read as a dense grid of tiny cards, which
    /// shape alone cannot rule out.
    /// </para>
    /// </summary>
    public int MinCellShortSide { get; init; } = 60;

    /// <summary>
    /// How far a card may differ in size from the other cards in the same photo before it is not
    /// believed, as a ratio either way.
    /// <para>
    /// Cards in one photograph are all the same size, so this is tight by nature. The slack is
    /// there for perspective: a card at the edge of a wide-angle shot, or lying at an angle, is
    /// genuinely a little smaller on the sensor than one in the middle.
    /// </para>
    /// </summary>
    public double SizeTolerance { get; init; } = 1.35;

    /// <summary>
    /// Half the width, in working pixels, of the widest line that may be erased as a bridge rather
    /// than kept as part of a card.
    /// <para>
    /// Wide enough to cover table grain, mat seams and shadow edges; far narrower than any card, so
    /// a card is never at risk. Set to zero to keep every thin object.
    /// </para>
    /// </summary>
    public int BridgeWidth { get; init; } = 4;

    /// <summary>
    /// How close to a card's proportions a division has to be before it may propose a card size.
    /// <para>
    /// Tighter than <see cref="AspectTolerance"/>, and deliberately so. That tolerance decides
    /// whether to keep a crop, where being generous costs little, because the agent looking at the
    /// crop can reject it. This one decides what a card measures, and a wrong answer here is
    /// applied to every other object in the photo. Three touching cards cut down the middle give
    /// two nearly square pieces, which the looser tolerance admits; at this one they are not cards
    /// and cannot vote.
    /// </para>
    /// </summary>
    public double ProposalAspect { get; init; } = 1.15;

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

    /// <summary>
    /// How to look for cards by their own borders, or null to go straight to separating them from
    /// the background.
    /// <para>
    /// This runs first because it asks less of the photograph. Flooding the background inward needs
    /// the cards to be surrounded by something; looking for a card-shaped border needs only the
    /// card. Cards in a binder, cards overlapping each other and cards on a cluttered desk all
    /// defeat the first and not the second. When it finds nothing at all, the pipeline below runs
    /// as it always did.
    /// </para>
    /// </summary>
    public CardArrangementOptions? Arrangement { get; init; } = new();
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
/// <para>
/// Two things have to be added to that, and both are here because a photograph of cards is not one
/// picture of one card. The first is that an edge threshold is a budget for the whole frame, so a
/// photo full of detailed artwork spends it on the artwork and leaves the cards' own borders below
/// the line; <see cref="BackgroundLike"/> adds a second barrier, the colour of the table, which no
/// amount of detail elsewhere can exhaust. The second is that cards touch, and touching cards are
/// one shape to a flood — and worse, a tidy block of them is a card's shape exactly, because a
/// card's proportions are near enough 1/sqrt(2) that halving or tiling one reproduces them.
/// </para>
/// <para>
/// That last point is why this class looks at the whole photo before deciding anything. Shape
/// cannot tell one card from four, nor from half of one. Size can, because every card in a
/// photograph is the same size, so <see cref="ReferenceShortSide"/> has the objects vote on how
/// wide a card is here and <see cref="GridFromCardSize"/> applies the answer to all of them.
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

        // Cards found by their own borders, which is the only thing that works when they are not
        // surrounded by background: in a binder page, or touching, or overlapping. See
        // CardArrangementDetector for why none of what follows can solve those.
        if (opts.Arrangement is not null)
        {
            var arrangement = CardArrangementDetector.Detect(source, opts.Arrangement);
            if (arrangement.Count > 0) return arrangement;
        }

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

        var cardMask = FloodBackground(edges, BackgroundLike(working, width, height), width, height);
        Open(cardMask, width, height, opts.BridgeWidth);
        var blobs = LabelBlobs(cardMask, width, height);

        double imageArea = (double)width * height;

        // Pass one: the outline of every object in the photo. No decision about how many cards an
        // object holds is taken yet, because that decision needs the whole photo (see below).
        var blocks = new List<Block>();
        foreach (var blob in blobs)
        {
            if (blob.Count < imageArea * opts.MinAreaFraction) continue;

            var rect = MinimumAreaRectangle(ConvexHull(blob.Points));
            if (rect is null) continue;

            var (corners, shortSide, longSide) = rect.Value;
            if (shortSide < 4 || longSide < 4) continue;

            var rectangularity = blob.Count / (shortSide * longSide);
            if (rectangularity < opts.MinRectangularity) continue;

            blocks.Add(new Block(corners, shortSide, longSide, rectangularity));
        }

        // How big is a card in this photo? Every card in one photograph is the same size, because
        // they are the same object seen from one camera position, and that single number is what
        // makes the rest of this tractable.
        var reference = ReferenceShortSide(blocks, opts);

        var results = new List<DetectedCard>();
        foreach (var block in blocks)
        {
            // One object may be several cards that are touching. Deciding how many comes before any
            // judgement about shape, because shape cannot answer it.
            var (cols, rows) = reference is null
                ? FindGrid(edges, width, height, block.Corners, imageArea, opts)
                : GridFromCardSize(edges, width, height, block.Corners, reference.Value, opts);

            foreach (var cell in Subdivide(block.Corners, cols, rows))
            {
                var (cellShort, cellLong) = SideLengths(cell);
                if (cellShort < 4 || cellLong < 4) continue;

                var cellArea = cellShort * cellLong;
                if (cellArea > imageArea * opts.MaxAreaFraction) continue;
                if (cellArea < imageArea * opts.MinAreaFraction) continue;

                var aspect = cellShort / cellLong;
                var aspectError = Math.Max(aspect / CardAspect, CardAspect / aspect);
                if (aspectError > opts.AspectTolerance) continue;

                // Scaled back to the original image, because the caller's quad is in source pixels
                // and the crop is taken from the full-resolution photograph, not this working copy.
                var points = OrderAsCard(cell)
                    .Select(p => new ImagePoint(p.X / scale, p.Y / scale))
                    .ToList();

                var confidence = Math.Clamp(
                    Math.Min(1d, block.Rectangularity) * (1d / aspectError),
                    0d, 1d);

                results.Add(new DetectedCard(new CardQuad(points), Math.Round(confidence, 3)));
            }
        }

        results = DropOddSizes(results, opts);

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
    /// <summary>
    /// Erases anything narrower than a card from the object mask, then restores what is left to its
    /// original size.
    /// <para>
    /// Not every edge in a photo belongs to a card. The grain of a wooden table, the seam between
    /// two mats, the edge of a shadow: each is a line the flood cannot cross, so each survives as a
    /// thin object. On its own that is harmless, since nothing that thin is ever mistaken for a
    /// card. The damage is done when such a line runs past several cards and joins them, because
    /// the result is one object that is neither card-shaped nor solid, and it is discarded whole —
    /// taking every card it touched with it. A single grain line can cost the entire photo.
    /// </para>
    /// <para>
    /// Shrinking the mask and growing it back is the standard remedy: a bridge a few pixels wide
    /// disappears when the mask shrinks and never comes back, while a card, hundreds of pixels
    /// across, loses only its corners and regains them. Cards that are genuinely touching are
    /// joined along a whole edge rather than by a thin bridge, so they stay joined, which is right —
    /// separating those is a question about seams and sizes, not about width.
    /// </para>
    /// </summary>
    private static void Open(bool[] mask, int width, int height, int radius)
    {
        if (radius <= 0) return;
        Erode(mask, width, height, radius);
        Grow(mask, width, height, radius);
    }

    /// <summary>Shrinks the object mask by <paramref name="radius"/> in every direction.</summary>
    private static void Erode(bool[] mask, int width, int height, int radius) =>
        Sweep(mask, width, height, radius, all: true);

    /// <summary>Grows the object mask by <paramref name="radius"/> in every direction.</summary>
    private static void Grow(bool[] mask, int width, int height, int radius) =>
        Sweep(mask, width, height, radius, all: false);

    /// <summary>
    /// Runs a square-window minimum (<paramref name="all"/>) or maximum over the mask. A square
    /// window is separable, so one horizontal pass followed by one vertical pass gives the same
    /// answer as the square itself at a fraction of the cost.
    /// </summary>
    private static void Sweep(bool[] mask, int width, int height, int radius, bool all)
    {
        var scratch = new bool[mask.Length];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                scratch[y * width + x] = Window(d =>
                {
                    var nx = x + d;
                    // Outside the photo counts as background, so an object running off the edge of
                    // the frame is eroded there rather than held up by pixels that do not exist.
                    return nx >= 0 && nx < width && mask[y * width + nx];
                });

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                mask[y * width + x] = Window(d =>
                {
                    var ny = y + d;
                    return ny >= 0 && ny < height && scratch[ny * width + x];
                });

        bool Window(Func<int, bool> at)
        {
            for (var d = -radius; d <= radius; d++)
            {
                var value = at(d);
                if (all && !value) return false;
                if (!all && value) return true;
            }

            return all;
        }
    }

    /// <summary>
    /// Marks the pixels that look like the surface the cards are lying on.
    /// <para>
    /// This is the second of the two barriers that stop the background flood, and it exists because
    /// the first one competes with the artwork. <see cref="EdgeMask"/> keeps the strongest tenth of
    /// gradients in the photo, which is a fixed budget for the whole frame; fill that frame with
    /// nine cards of detailed art and the art spends the budget, leaving a card's own border below
    /// the threshold and the flood free to pour in and erase the card. Nothing about the border
    /// changed — the competition for the budget did.
    /// </para>
    /// <para>
    /// Colour is not a budget, so it does not have that failure. The table is sampled where it is
    /// certain to be visible, at the frame of the photo, and kept as a set of coarse colour bins
    /// rather than one average, so that a two-tone surface or an uneven light still reads as one
    /// background. The flood may then only pass through pixels that look like the table, which
    /// means that whether a card is detected no longer depends on how busy the card next to it is.
    /// </para>
    /// </summary>
    private static bool[] BackgroundLike(Image<Rgba32> image, int width, int height)
    {
        const double BackgroundCoverage = 0.80;
        const int Shift = 5;              // 8 levels per channel
        const int Levels = 256 >> Shift;
        var frame = Math.Max(2, Math.Min(width, height) / 50);

        var bins = new int[Levels * Levels * Levels];
        var pixels = new byte[width * height * 3];
        var sampled = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var onFrame = y < frame || y >= height - frame;
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    var o = (y * width + x) * 3;
                    pixels[o] = p.R;
                    pixels[o + 1] = p.G;
                    pixels[o + 2] = p.B;

                    if (!onFrame && x >= frame && x < width - frame) continue;
                    bins[Bin(p.R, p.G, p.B)]++;
                    sampled++;
                }
            }
        });

        // The commonest colours of the frame are taken until they account for most of it. A share
        // threshold would not do: a grained wooden table is dozens of browns, none of them common
        // enough on its own to clear any fixed bar, and the flood would have nowhere to start. What
        // is true of every background, flat or grained, is that between them its colours cover the
        // frame — so that, rather than any one colour's share, is what is asked for. Stopping short
        // of the whole frame is what keeps a card lying across the edge of the photo from
        // nominating its own colours as table.
        var order = Enumerable.Range(0, bins.Length)
            .Where(i => bins[i] > 0)
            .OrderByDescending(i => bins[i]);

        var isTable = new bool[bins.Length];
        var covered = 0;
        foreach (var i in order)
        {
            if (covered >= sampled * BackgroundCoverage) break;
            isTable[i] = true;
            covered += bins[i];
        }

        // Accept neighbouring bins too: a surface shading gradually from one side of the photo to
        // the other crosses bin boundaries without ever stopping being the table.
        var accepted = new bool[bins.Length];
        for (var r = 0; r < Levels; r++)
            for (var g = 0; g < Levels; g++)
                for (var b = 0; b < Levels; b++)
                {
                    if (!isTable[(r * Levels + g) * Levels + b]) continue;
                    for (var dr = -1; dr <= 1; dr++)
                        for (var dg = -1; dg <= 1; dg++)
                            for (var db = -1; db <= 1; db++)
                            {
                                int nr = r + dr, ng = g + dg, nb = b + db;
                                if (nr < 0 || ng < 0 || nb < 0) continue;
                                if (nr >= Levels || ng >= Levels || nb >= Levels) continue;
                                accepted[(nr * Levels + ng) * Levels + nb] = true;
                            }
                }

        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++)
        {
            var o = i * 3;
            mask[i] = accepted[Bin(pixels[o], pixels[o + 1], pixels[o + 2])];
        }

        return mask;

        static int Bin(byte r, byte g, byte b) =>
            (((r >> Shift) * Levels) + (g >> Shift)) * Levels + (b >> Shift);
    }

    private static bool[] FloodBackground(bool[] edges, bool[] backgroundLike, int width, int height)
    {
        var background = new bool[width * height];
        var queue = new Queue<int>();

        void Seed(int x, int y)
        {
            var i = y * width + x;
            if (background[i] || !backgroundLike[i]) return;
            // An edge only blocks the flood if it separates the background from something else.
            // The grain of a table is an edge with background on both sides; a card's border is
            // not, and that difference is what keeps grain from sealing the gaps between cards.
            if (edges[i] && Borders(x, y)) return;
            background[i] = true;
            queue.Enqueue(i);
        }

        bool Borders(int x, int y)
        {
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    if (!backgroundLike[ny * width + nx]) return true;
                }

            return false;
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

    /// <summary>An object found in the photo, before any decision about how many cards it holds.</summary>
    private readonly record struct Block(
        (double X, double Y)[] Corners, double Short, double Long, double Rectangularity);

    /// <summary>
    /// The width of a card in this photo, in working pixels, or null when the photo offers no
    /// opinion.
    /// <para>
    /// Every card in one photograph is the same size: they are the same object, photographed
    /// together from one camera position. That single number is the strongest piece of evidence
    /// available here, and it is the only one that no individual object can supply, because an
    /// object on its own cannot tell whether it is one card or four.
    /// </para>
    /// <para>
    /// So the whole photo votes. Each object proposes the card sizes that would divide it into a
    /// whole number of card-shaped pieces, and the size that accounts for the most of the photo
    /// wins. A page of nine touching cards and a tenth card lying apart then agree on one answer,
    /// which is what lets the page be divided correctly.
    /// </para>
    /// </summary>
    private static double? ReferenceShortSide(IReadOnlyList<Block> blocks, CardDetectorOptions options)
    {
        var span = Math.Max(1, options.MaxGridSpan);

        // Every way each object could be read as a whole number of card-shaped pieces is a proposal
        // about how wide a card is in this photo. An object is weighted by its area, so a page of
        // nine cards has more say than a stray button.
        var proposals = new List<(double Size, double Weight)>();
        var divisions = new List<(double Size, double Weight)>[blocks.Count];

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            divisions[i] = [];

            for (var cols = 1; cols <= span; cols++)
                for (var rows = 1; rows <= span; rows++)
                {
                    var (cellShort, cellLong) = SideLengths(Subdivide(block.Corners, cols, rows).First());
                    if (cellShort < 4 || cellLong < 4) continue;

                    var aspect = cellShort / cellLong;
                    if (Math.Max(aspect / CardAspect, CardAspect / aspect) > options.ProposalAspect) continue;

                    var weight = block.Short * block.Long;
                    divisions[i].Add((cellShort, weight));
                    proposals.Add((cellShort, weight));
                }
        }

        if (proposals.Count == 0) return null;

        // The winning size is the one that explains the most of the photo: the size at which the
        // largest share of what was found can be read as a whole number of cards. Where two sizes
        // explain the same amount the larger wins, because of the proportion trap described on
        // GridFromCardSize - half a card is card-shaped, so any size that fits also fits halved,
        // and the smaller reading is the one that yields crops of text boxes.
        double? best = null;
        var bestSupport = double.MinValue;

        foreach (var (size, _) in proposals)
        {
            var support = 0d;
            for (var i = 0; i < blocks.Count; i++)
                if (divisions[i].Any(d => Math.Max(d.Size / size, size / d.Size) <= options.SizeTolerance))
                    support += blocks[i].Short * blocks[i].Long;

            if (support > bestSupport || (Math.Abs(support - bestSupport) < 1e-9 && size > best))
            {
                best = size;
                bestSupport = support;
            }
        }

        return best;
    }

    /// <summary>
    /// Divides a block into cards of a known size.
    /// <para>
    /// This exists because shape alone is not merely weak evidence here, it is actively misleading.
    /// A trading card is 63 x 88 mm, and 63/88 is 0.716 — near enough 1/sqrt(2) that a card cut in
    /// half across its long side has, to within two per cent, a card's proportions all over again.
    /// So a card's art box and its text box both look exactly like cards, a 3 x 6 division of nine
    /// cards scores as well as the correct 3 x 3, and no amount of tightening the aspect tolerance
    /// can separate them: the two shapes really are the same shape.
    /// </para>
    /// <para>
    /// Size is what breaks the tie, and only the whole photo knows it. Each arrangement is measured
    /// against the card size the photo agreed on, and the closest fit wins; a division into halves
    /// is then wrong for the obvious reason, that the halves are half the size of the cards lying
    /// next to them. Ties go to the coarser division, because a crop of a whole card that turned
    /// out to be two cards can still be identified as one of them, whereas a crop of a text box
    /// cannot be identified as anything.
    /// </para>
    /// </summary>
    private static (int Cols, int Rows) GridFromCardSize(
        bool[] edges, int width, int height,
        (double X, double Y)[] corners, double referenceShort, CardDetectorOptions options)
    {
        var referenceLong = referenceShort / CardAspect;
        var span = Math.Max(1, options.MaxGridSpan);

        var best = (Cols: 1, Rows: 1);
        var bestError = double.MaxValue;
        var bestCells = int.MaxValue;

        for (var cols = 1; cols <= span; cols++)
        {
            for (var rows = 1; rows <= span; rows++)
            {
                var cells = cols * rows;
                var cell = Subdivide(corners, cols, rows).First();
                var (cellShort, cellLong) = SideLengths(cell);
                if (cellShort < 4 || cellLong < 4) continue;

                var error = Math.Max(
                    Math.Max(cellShort / referenceShort, referenceShort / cellShort),
                    Math.Max(cellLong / referenceLong, referenceLong / cellLong));
                if (error > options.SizeTolerance) continue;

                // Size has already chosen the arrangement; the seams only have to corroborate it,
                // so the bar here is evidence of a seam rather than a complete one. Two touching
                // cards with the same border colour genuinely have no edge between them.
                if (cells > 1)
                {
                    var seams = SeamCoverage(edges, width, height, corners, cols, rows);
                    if (seams is null) continue;
                    if (seams.Value.AcrossCols.Concat(seams.Value.AcrossRows)
                        .Any(c => c < options.MinSeamEvidence)) continue;
                }

                if (error < bestError || (Math.Abs(error - bestError) < 1e-9 && cells < bestCells))
                {
                    best = (cols, rows);
                    bestError = error;
                    bestCells = cells;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Removes crops that disagree with the rest of the photo about how big a card is.
    /// <para>
    /// A last line of defence rather than the main one: whatever route a fragment took to get here,
    /// a crop half the size of every other crop in the same photo is not a card, because cards in
    /// one photograph are all the same size. It only runs once there are enough crops for "the rest
    /// of the photo" to mean something.
    /// </para>
    /// </summary>
    private static List<DetectedCard> DropOddSizes(List<DetectedCard> cards, CardDetectorOptions options)
    {
        if (cards.Count < 3) return cards;

        var sizes = cards.Select(c => SideLengths(Corners(c.Quad)).Short).OrderBy(s => s).ToList();
        var median = sizes.Count % 2 == 1
            ? sizes[sizes.Count / 2]
            : (sizes[sizes.Count / 2 - 1] + sizes[sizes.Count / 2]) / 2d;
        if (median <= 0) return cards;

        var kept = cards
            .Where(c =>
            {
                var s = SideLengths(Corners(c.Quad)).Short;
                return Math.Max(s / median, median / s) <= options.SizeTolerance;
            })
            .ToList();

        return kept.Count == 0 ? cards : kept;
    }

    private static (double X, double Y)[] Corners(CardQuad quad) =>
        quad.Points.Select(p => (p.X, p.Y)).ToArray();

    /// <summary>
    /// Decides how many cards a rectangular block contains, by looking for the seams that would
    /// separate them.
    /// <para>
    /// Shape cannot answer this. Cards tile without changing proportion — three columns of three
    /// are exactly as wide-to-tall as one card — so a block of nine passes every aspect test as a
    /// single card and is cropped as the whole page. What distinguishes them is that a block has
    /// continuous edges running across its interior where the cards meet, and one card does not.
    /// </para>
    /// <para>
    /// Only arrangements whose cells are card-shaped are considered, and an arrangement has to be
    /// anchored: at least one axis must be divided by seams that are present along their whole
    /// length, so art that happens to contain a straight line cannot on its own split a card.
    /// </para>
    /// <para>
    /// The other axis is allowed to be fainter. Two touching cards whose borders happen to be the
    /// same colour have no visible seam between them at all, so insisting every seam is complete
    /// collapses a tidy page back into one card. Once one axis is anchored the cell width is known,
    /// and card proportions then imply how many cells the other axis must hold; the faint seams
    /// only have to corroborate it. Candidates are scored by their mean seam coverage so that the
    /// arrangement whose seams are really there wins over one that merely fits.
    /// </para>
    /// <para>
    /// This is the fallback, used only when the photo contains no object that is card-shaped on its
    /// own and so has no opinion about how big a card is. <see cref="GridFromCardSize"/> is both
    /// stronger and simpler and is preferred whenever it can be used, because seams cannot settle
    /// every case on their own: a card's proportions are near enough 1/sqrt(2) that halving it
    /// reproduces them, so shape ranks a division into halves exactly as highly as the right one.
    /// </para>
    /// </summary>
    private static (int Cols, int Rows) FindGrid(
        bool[] edges, int width, int height,
        (double X, double Y)[] corners, double imageArea, CardDetectorOptions options)
    {
        var span = Math.Max(1, options.MaxGridSpan);
        var best = (Cols: 1, Rows: 1);
        var bestScore = double.MinValue;
        var bestCells = int.MaxValue;

        for (var cols = 1; cols <= span; cols++)
        {
            for (var rows = 1; rows <= span; rows++)
            {
                var cells = cols * rows;
                if (cells == 1) continue;

                var cell = Subdivide(corners, cols, rows).First();
                var (cellShort, cellLong) = SideLengths(cell);
                if (cellShort < options.MinCellShortSide) continue;

                var cellArea = cellShort * cellLong;
                if (cellArea < imageArea * options.MinAreaFraction) continue;
                if (cellArea > imageArea * options.MaxAreaFraction) continue;

                var aspect = cellShort / cellLong;
                var error = Math.Max(aspect / CardAspect, CardAspect / aspect);
                if (error > options.AspectTolerance) continue;

                var seams = SeamCoverage(edges, width, height, corners, cols, rows);
                if (seams is null) continue;

                var (acrossCols, acrossRows) = seams.Value;

                // Every seam has to show something. A division placed through the middle of a card
                // crosses unbroken artwork, and that is what rules out splitting three rows into four.
                if (acrossCols.Concat(acrossRows).Any(c => c < options.MinSeamEvidence)) continue;

                var anchored =
                    (acrossCols.Length > 0 && acrossCols.All(c => c >= options.MinSeamCoverage)) ||
                    (acrossRows.Length > 0 && acrossRows.All(c => c >= options.MinSeamCoverage));
                if (!anchored) continue;

                // Ties go to the coarser division. A crop of two cards can still be identified as
                // one of them; a crop of half a card cannot be identified as anything.
                var score = acrossCols.Concat(acrossRows).Average();
                if (score > bestScore || (Math.Abs(score - bestScore) < 1e-9 && cells < bestCells))
                {
                    best = (cols, rows);
                    bestScore = score;
                    bestCells = cells;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Measures how much of each line where two cards in this arrangement would meet is really
    /// drawn in the edge mask. Returns the coverage of the seams between columns and between rows
    /// separately, because the two axes are independent evidence, or null when the block is too
    /// small for the measurement to mean anything.
    /// </summary>
    private static (double[] AcrossCols, double[] AcrossRows)? SeamCoverage(
        bool[] edges, int width, int height,
        (double X, double Y)[] corners, int cols, int rows)
    {
        // The rectangle's own axes: u runs along corners[0]->corners[1], v down corners[0]->[3].
        var ux = (corners[1].X - corners[0].X) / cols;
        var uy = (corners[1].Y - corners[0].Y) / cols;
        var vx = (corners[3].X - corners[0].X) / rows;
        var vy = (corners[3].Y - corners[0].Y) / rows;

        (double X, double Y) At(double c, double r) =>
            (corners[0].X + ux * c + vx * r, corners[0].Y + uy * c + vy * r);

        var acrossCols = new List<double>();
        var acrossRows = new List<double>();

        for (var c = 1; c < cols; c++)
        {
            var coverage = SeamCoverage(edges, width, height, At(c, 0), At(c, rows));
            if (coverage is null) return null;
            acrossCols.Add(coverage.Value);
        }

        for (var r = 1; r < rows; r++)
        {
            var coverage = SeamCoverage(edges, width, height, At(0, r), At(cols, r));
            if (coverage is null) return null;
            acrossRows.Add(coverage.Value);
        }

        return (acrossCols.ToArray(), acrossRows.ToArray());
    }

    /// <summary>
    /// Walks a line and reports how much of it lies on an edge. A small search radius absorbs the
    /// wobble from a slightly skewed photograph and from rounding the rectangle's corners to whole
    /// pixels. Null means the line was too short to measure.
    /// </summary>
    private static double? SeamCoverage(
        bool[] edges, int width, int height,
        (double X, double Y) from, (double X, double Y) to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 8) return null;

        var samples = (int)Math.Ceiling(length);
        var counted = 0;
        var hits = 0;

        for (var i = 0; i <= samples; i++)
        {
            var t = (double)i / samples;
            var x = (int)Math.Round(from.X + dx * t);
            var y = (int)Math.Round(from.Y + dy * t);
            if (x < 0 || y < 0 || x >= width || y >= height) continue;

            counted++;
            if (HasEdgeNear(edges, width, height, x, y, 2)) hits++;
        }

        return counted < 8 ? null : (double)hits / counted;
    }

    private static bool HasEdgeNear(bool[] edges, int width, int height, int x, int y, int radius)
    {
        for (var dy = -radius; dy <= radius; dy++)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= height) continue;
            for (var dx = -radius; dx <= radius; dx++)
            {
                var nx = x + dx;
                if (nx < 0 || nx >= width) continue;
                if (edges[ny * width + nx]) return true;
            }
        }
        return false;
    }

    /// <summary>Cuts a rectangle into a <paramref name="cols"/> x <paramref name="rows"/> tiling.</summary>
    private static IEnumerable<(double X, double Y)[]> Subdivide(
        (double X, double Y)[] corners, int cols, int rows)
    {
        var ux = (corners[1].X - corners[0].X) / cols;
        var uy = (corners[1].Y - corners[0].Y) / cols;
        var vx = (corners[3].X - corners[0].X) / rows;
        var vy = (corners[3].Y - corners[0].Y) / rows;

        (double X, double Y) At(double c, double r) =>
            (corners[0].X + ux * c + vx * r, corners[0].Y + uy * c + vy * r);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                yield return [At(c, r), At(c + 1, r), At(c + 1, r + 1), At(c, r + 1)];
            }
        }
    }

    private static (double Short, double Long) SideLengths((double X, double Y)[] quad)
    {
        static double Distance((double X, double Y) a, (double X, double Y) b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        var a = Distance(quad[0], quad[1]);
        var b = Distance(quad[1], quad[2]);
        return (Math.Min(a, b), Math.Max(a, b));
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

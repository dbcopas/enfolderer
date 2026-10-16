using Enfolderer.Ai.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

/// <summary>Tuning for <see cref="CardArrangementDetector"/>.</summary>
public sealed record CardArrangementOptions
{
    /// <summary>Longest edge, in pixels, the search runs at. Zero disables the detector.</summary>
    public int WorkingSize { get; init; } = 420;

    /// <summary>
    /// How much edge a box's weakest side must carry, as a multiple of the photo's own mean
    /// gradient, before it is proposed as a card.
    /// <para>
    /// The weakest side decides it, not the average of the four: half a card has three real sides
    /// and one imaginary one, and averaging lets the three carry the fourth.
    /// </para>
    /// </summary>
    public double MinEdge { get; init; } = 1.0;

    /// <summary>
    /// How much detail a box must hold inside its own borders, as a multiple of the photo's mean
    /// gradient. A card is busy; an empty binder pocket is flat.
    /// </summary>
    public double MinDetail { get; init; } = 0.5;

    /// <summary>
    /// How far a card's width may stray from the one width the photo voted for, as a fraction.
    /// <para>
    /// Perspective is why this is not tight. A page photographed from one side has its near column
    /// a quarter wider than its far one, and both are cards; measured on the training photographs,
    /// anything under a fifth loses a whole column.
    /// </para>
    /// </summary>
    public double SizeSlack { get; init; } = 0.22;

    /// <summary>How much two boxes may overlap, as a fraction of the smaller, and both be kept.</summary>
    public double MaxOverlap { get; init; } = 0.35;

    /// <summary>
    /// How much of <see cref="MinEdge"/> a box must carry when it sits where the arrangement says
    /// a card should be. A card whose border is lost to glare is still a card when its neighbours
    /// place it.
    /// </summary>
    public double CompletionRelief { get; init; } = 0.5;

    /// <summary>
    /// How alike the photo must be to itself, a card's spacing apart, before that spacing is
    /// believed to be the spacing of whole cards rather than of halves.
    /// </summary>
    public double MinSelfSimilarity { get; init; } = 0.35;

    /// <summary>
    /// How much better the double spacing must look than the single one before the cards found are
    /// taken to be halves of wider cards.
    /// </summary>
    public double HalvesMargin { get; init; } = 1.4;

    /// <summary>
    /// How much detail a box must hold relative to the typical box in this photo. This is what
    /// separates an empty pocket from a dim card: both are flat against the photo's mean, but only
    /// the empty one is flat against the cards beside it.
    /// </summary>
    public double MinRelativeDetail { get; init; } = 0.5;
}

/// <summary>
/// Finds the cards in a photograph by looking for card-shaped boxes that all agree on one size.
/// <para>
/// This replaces an earlier attempt that assumed the photo was a nine-pocket binder page and fitted
/// that lattice to it. A page is only one of the things people photograph: a few cards laid on a
/// table, a half-filled twelve-pocket sheet and a page with one column out of frame are all
/// ordinary, and all of them defeat a fixed grid. Nothing here counts rows or columns. The
/// arrangement is whatever the cards turn out to be in.
/// </para>
/// <para>
/// A card is found by its border rather than by its surroundings, which is what makes a binder page
/// tractable at all: there is no background between pockets for <see cref="CardDetector"/>'s flood
/// to enter, but every card still has four sides. A box is proposed where all four of its sides
/// carry edge and its inside carries detail, and crucially it is judged on its <em>weakest</em>
/// side, because a box covering half a card, or two cards, has some real sides and some imaginary
/// ones.
/// </para>
/// <para>
/// Four sides and a card's proportions are not enough on their own, because a card's art box has
/// both — 63/88 is within two per cent of 1/sqrt(2), so a card's parts are card-shaped too. What
/// settles it is that every card in one photograph is the same size, having been photographed
/// together from one position. So the proposals vote on a width, weighted by how good they are,
/// and only the proposals at the winning width survive. An art box loses that vote to the nine
/// cards around it.
/// </para>
/// <para>
/// Finally the arrangement completes itself. The cards that were found imply a spacing, and a card
/// whose own border was lost to glare or shadow is looked for again where that spacing says it
/// should be, with a lower bar but a real one. The search is local rather than rigid, so a page
/// photographed at an angle, where the far column sits higher than the near one, still completes.
/// </para>
/// </summary>
public static class CardArrangementDetector
{
    /// <summary>A card standing upright: taller than it is wide.</summary>
    private const double Upright = PerspectiveCropper.StandardCardAspect;

    /// <summary>The same card on its side, which is how a sideways page photographs.</summary>
    private const double OnItsSide = 1d / PerspectiveCropper.StandardCardAspect;

    private sealed record Box(int X, int Y, int Width, int Height, double Edge, double Detail);

    /// <summary>
    /// Finds every card in an already-decoded photo. Returns an empty list when the photo holds
    /// nothing card-shaped, which is the caller's cue to try something else.
    /// </summary>
    public static IReadOnlyList<DetectedCard> Detect(Image<Rgba32> source, CardArrangementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var opts = options ?? new CardArrangementOptions();
        if (opts.WorkingSize <= 0) return [];

        var scale = Math.Min(1d, (double)opts.WorkingSize / Math.Max(source.Width, source.Height));
        var width = Math.Max(32, (int)Math.Round(source.Width * scale));
        var height = Math.Max(32, (int)Math.Round(source.Height * scale));

        using var working = source.Clone(c => c.Resize(width, height));
        var frame = new Frame(working, width, height);
        if (frame.MeanGradient < 1e-6) return [];

        var proposals = Propose(frame, opts, Upright);
        if (proposals.Count == 0) return [];

        var cards = KeepOneSize(proposals, opts, (long)width * height, Upright);

        var aspect = Upright;

        var sideways = WholeCardsFromHalves(frame, cards, opts);
        if (sideways is not null) (cards, aspect) = (sideways, OnItsSide);

        cards = Complete(frame, cards, opts, aspect);
        cards = Complete(frame, cards, opts, aspect);
        cards = DropEmptyPockets(cards, opts);

        var isSideways = aspect == OnItsSide;
        var topOnLeft = isSideways && DetermineSidewaysOrientation(frame, cards);
        var (_, medianTilt) = MeasureTilts(source, cards, scale, isSideways);

        var orderedCards = AlignAndSortCards(cards, isSideways, topOnLeft, medianTilt);

        return orderedCards
            .Select(b => new DetectedCard(
                QuadOf(b, scale, isSideways, topOnLeft, medianTilt, source.Width, source.Height),
                Math.Round(Math.Clamp(b.Edge / 2d, 0d, 1d), 3)))
            .ToList();
    }

    internal static IReadOnlyList<DetectedCard> CompleteMeasured(
        Image<Rgba32> source, IReadOnlyList<DetectedCard> anchors, CardArrangementOptions opts)
    {
        var scale = Math.Min(1d, opts.WorkingSize / (double)Math.Max(source.Width, source.Height));
        using var working = source.Clone(c => c.Resize(
            Math.Max(32, (int)Math.Round(source.Width * scale)), Math.Max(32, (int)Math.Round(source.Height * scale))));
        var frame = new Frame(working, working.Width, working.Height);
        var originals = new Dictionary<Box, DetectedCard>();
        foreach (var anchor in anchors)
        {
            var b = anchor.Quad.BoundingBox();
            var box = Measure(frame, (int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale),
                (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale));
            originals[box] = anchor;
        }
        var boxes = originals.Keys.ToList();
        boxes = Complete(frame, boxes, opts, Upright, 0.1);
        boxes = Complete(frame, boxes, opts, Upright, 0.1);
        var (_, tilt) = MeasureTilts(source, boxes, scale, false);
        var results = new List<DetectedCard>();
        foreach (var box in boxes)
        {
            if (originals.TryGetValue(box, out var original))
            {
                results.Add(original);
                continue;
            }
            var nearest = originals.Keys.OrderBy(b => Math.Abs(b.Y + b.Height / 2d - box.Y - box.Height / 2d)).First();
            var row = originals.Keys.Where(b => Math.Abs(b.Y - nearest.Y) < nearest.Height * 0.35).ToList();
            var column = originals.Keys.Where(b =>
                Math.Abs(b.X + b.Width / 2d - box.X - box.Width / 2d) < box.Width * 0.35).ToList();
            var columnWidth = column.Count > 0 ? Median(column.Select(b => (double)b.Width).ToArray()) : box.Width;
            var columnCenter = column.Count > 0 ? Median(column.Select(b => b.X + b.Width / 2d).ToArray()) : box.X + box.Width / 2d;
            var top = row.Min(b => b.Y) - box.Height * 0.03;
            var bottom = top + Median(row.Select(b => (double)b.Height).ToArray()) * 1.06;
            var nextRow = originals.Keys.Where(b => b.Y > top + box.Height * 0.5).ToList();
            if (nextRow.Count > 0)
                bottom = Math.Min(bottom, Median(nextRow.Select(b => (double)b.Y).ToArray()) - box.Height * 0.02);
            var located = box with
            {
                X = (int)Math.Round(columnCenter - columnWidth / 2),
                Y = (int)Math.Round(top),
                Width = (int)Math.Round(columnWidth),
                Height = (int)Math.Round(bottom - top)
            };
            results.Add(new DetectedCard(
                QuadOf(located, scale, false, false, tilt, source.Width, source.Height, 0),
                Math.Round(Math.Clamp(box.Edge / 2, 0, 1), 3)));
        }
        var typicalHeight = Median(results.Select(r => r.Quad.BoundingBox().Height).ToArray());
        var ordered = results.OrderBy(r => r.Quad.BoundingBox().Y).ToList();
        var rows = new List<List<DetectedCard>>();
        foreach (var card in ordered)
        {
            if (rows.Count == 0 || card.Quad.BoundingBox().Y - rows[^1][0].Quad.BoundingBox().Y > typicalHeight * 0.5)
                rows.Add([]);
            rows[^1].Add(card);
        }
        return rows.SelectMany(row => row.OrderBy(r => r.Quad.BoundingBox().X)).ToList();
    }

    private static List<Box> AlignAndSortCards(
        List<Box> cards, bool isSideways, bool topOnLeft, double tilt)
    {
        if (cards.Count <= 1) return cards;

        var norm = Math.Sqrt(1.0 + tilt * tilt);

        if (!isSideways)
        {
            var cos = 1.0 / norm;
            var sin = tilt / norm;
            var hAvg = cards.Average(b => b.Height);

            var withCoord = cards.Select(b =>
            {
                var cx = b.X + b.Width / 2.0;
                var cy = b.Y + b.Height / 2.0;
                var yRot = cy * cos - cx * sin;
                var xRot = cx * cos + cy * sin;
                return (Box: b, YRot: yRot, XRot: xRot);
            }).OrderBy(c => c.YRot).ToList();

            var rows = new List<List<(Box Box, double YRot, double XRot)>>();
            var currentRow = new List<(Box Box, double YRot, double XRot)> { withCoord[0] };
            for (var i = 1; i < withCoord.Count; i++)
            {
                if (withCoord[i].YRot - withCoord[i - 1].YRot > hAvg * 0.5)
                {
                    rows.Add(currentRow);
                    currentRow = new List<(Box Box, double YRot, double XRot)>();
                }
                currentRow.Add(withCoord[i]);
            }
            rows.Add(currentRow);

            var sorted = new List<Box>();
            foreach (var row in rows)
            {
                var medY = Median(row.Select(r => (double)r.Box.Y).ToArray());
                foreach (var item in row.OrderBy(r => r.XRot))
                {
                    var b = item.Box;
                    if (Math.Abs(b.Y - medY) > hAvg * 0.15)
                    {
                        b = b with { Y = (int)Math.Round(medY) };
                    }
                    sorted.Add(b);
                }
            }
            return sorted;
        }
        else
        {
            var cos = 1.0 / norm;
            var sin = tilt / norm;
            var wAvg = cards.Average(b => b.Width);

            var withCoord = cards.Select(b =>
            {
                var cx = b.X + b.Width / 2.0;
                var cy = b.Y + b.Height / 2.0;
                var xRot = cx * cos + cy * sin;
                var yRot = cy * cos - cx * sin;
                return (Box: b, XRot: xRot, YRot: yRot);
            }).OrderBy(c => c.XRot).ToList();

            var binderRows = new List<List<(Box Box, double XRot, double YRot)>>();
            var currentRow = new List<(Box Box, double XRot, double YRot)> { withCoord[0] };
            for (var i = 1; i < withCoord.Count; i++)
            {
                if (withCoord[i].XRot - withCoord[i - 1].XRot > wAvg * 0.5)
                {
                    binderRows.Add(currentRow);
                    currentRow = new List<(Box Box, double XRot, double YRot)>();
                }
                currentRow.Add(withCoord[i]);
            }
            binderRows.Add(currentRow);

            if (!topOnLeft) binderRows.Reverse();

            var sorted = new List<Box>();
            foreach (var row in binderRows)
            {
                var medX = Median(row.Select(r => (double)r.Box.X).ToArray());
                var orderedInRow = topOnLeft
                    ? row.OrderBy(r => r.YRot)
                    : row.OrderByDescending(r => r.YRot);

                foreach (var item in orderedInRow)
                {
                    var b = item.Box;
                    if (Math.Abs(b.X - medX) > wAvg * 0.15)
                    {
                        b = b with { X = (int)Math.Round(medX) };
                    }
                    sorted.Add(b);
                }
            }
            return sorted;
        }
    }

    private static bool DetermineSidewaysOrientation(Frame frame, List<Box> cards)
    {
        double leftSum = 0, rightSum = 0;
        foreach (var b in cards)
        {
            var midX = b.X + b.Width / 2;
            leftSum += frame.MeanLuminance(b.X, b.Y, midX, b.Y + b.Height);
            rightSum += frame.MeanLuminance(midX, b.Y, b.X + b.Width, b.Y + b.Height);
        }
        return leftSum <= rightSum;
    }

    private static (Dictionary<Box, double> Tilts, double MedianTilt) MeasureTilts(
        Image<Rgba32> source, List<Box> cards, double scale, bool isSideways)
    {
        var tilts = new Dictionary<Box, double>();
        var tiltList = new List<double>();

        foreach (var b in cards)
        {
            var x0 = b.X / scale;
            var y0 = b.Y / scale;
            var w = b.Width / scale;
            var h = b.Height / scale;

            if (!isSideways)
            {
                var yTop = (int)(y0 + h * 0.25);
                var yBot = (int)(y0 + h * 0.75);
                var search = (int)Math.Max(15, w * 0.08);

                var leftTopX = FindEdgeX(source, (int)x0, yTop, search);
                var leftBotX = FindEdgeX(source, (int)x0, yBot, search);
                var rightTopX = FindEdgeX(source, (int)(x0 + w), yTop, search);
                var rightBotX = FindEdgeX(source, (int)(x0 + w), yBot, search);

                var tiltLeft = (leftBotX - leftTopX) / (yBot - yTop);
                var tiltRight = (rightBotX - rightTopX) / (yBot - yTop);
                var t = (tiltLeft + tiltRight) / 2.0;
                if (!double.IsNaN(t) && Math.Abs(t) < 0.25)
                {
                    tilts[b] = t;
                    tiltList.Add(t);
                }
            }
            else
            {
                var xLeft = (int)(x0 + w * 0.25);
                var xRight = (int)(x0 + w * 0.75);
                var search = (int)Math.Max(15, h * 0.08);

                var topY1 = FindEdgeY(source, xLeft, (int)y0, search);
                var topY2 = FindEdgeY(source, xRight, (int)y0, search);
                var botY1 = FindEdgeY(source, xLeft, (int)(y0 + h), search);
                var botY2 = FindEdgeY(source, xRight, (int)(y0 + h), search);

                var tiltTop = (topY2 - topY1) / (xRight - xLeft);
                var tiltBot = (botY2 - botY1) / (xRight - xLeft);
                var t = (tiltTop + tiltBot) / 2.0;
                if (!double.IsNaN(t) && Math.Abs(t) < 0.25)
                {
                    tilts[b] = t;
                    tiltList.Add(t);
                }
            }
        }

        var median = tiltList.Count == 0 ? 0 : Median(tiltList.ToArray());
        return (tilts, median);
    }

    private static double FindEdgeX(Image<Rgba32> img, int targetX, int targetY, int search)
    {
        var minX = Math.Max(1, targetX - search);
        var maxX = Math.Min(img.Width - 2, targetX + search);
        var maxGrad = -1d;
        var bestX = targetX;
        for (var x = minX; x <= maxX; x++)
        {
            var p1 = img[x - 1, targetY];
            var p2 = img[x + 1, targetY];
            var grad = Math.Abs((p2.R + p2.G + p2.B) - (p1.R + p1.G + p1.B));
            if (grad > maxGrad)
            {
                maxGrad = grad;
                bestX = x;
            }
        }
        return bestX;
    }

    private static double FindEdgeY(Image<Rgba32> img, int targetX, int targetY, int search)
    {
        var minY = Math.Max(1, targetY - search);
        var maxY = Math.Min(img.Height - 2, targetY + search);
        var maxGrad = -1d;
        var bestY = targetY;
        for (var y = minY; y <= maxY; y++)
        {
            var p1 = img[targetX, y - 1];
            var p2 = img[targetX, y + 1];
            var grad = Math.Abs((p2.R + p2.G + p2.B) - (p1.R + p1.G + p1.B));
            if (grad > maxGrad)
            {
                maxGrad = grad;
                bestY = y;
            }
        }
        return bestY;
    }

    private static CardQuad QuadOf(
        Box box, double scale, bool isSideways, bool topOnLeft, double tilt, int imgWidth, int imgHeight,
        double centerShiftFraction = 0.035)
    {
        var x0 = box.X / scale;
        var y0 = box.Y / scale;
        var w = box.Width / scale;
        var h = box.Height / scale;

        var norm = Math.Sqrt(1.0 + tilt * tilt);

        if (isSideways)
        {
            var lx = 1.0 / norm;
            var ly = tilt / norm;
            var sx = -tilt / norm;
            var sy = 1.0 / norm;

            var wHalf = (h * 1.04) / 2.0;
            var hHalf = (w * 1.08) / 2.0;

            var xCenter = x0 + w / 2.0;
            var yCenter = y0 + h / 2.0;

            if (topOnLeft)
            {
                var shift = w * 0.035;
                var cx = xCenter + shift * lx;
                var cy = yCenter + shift * ly;

                return new CardQuad(
                [
                    new ImagePoint(Math.Clamp(cx - hHalf * lx + wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy - hHalf * ly + wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx - hHalf * lx - wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy - hHalf * ly - wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx + hHalf * lx - wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy + hHalf * ly - wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx + hHalf * lx + wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy + hHalf * ly + wHalf * sy, 0, imgHeight - 1)),
                ]);
            }
            else
            {
                var shift = w * 0.035;
                var cx = xCenter - shift * lx;
                var cy = yCenter - shift * ly;

                return new CardQuad(
                [
                    new ImagePoint(Math.Clamp(cx + hHalf * lx - wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy + hHalf * ly - wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx + hHalf * lx + wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy + hHalf * ly + wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx - hHalf * lx + wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy - hHalf * ly + wHalf * sy, 0, imgHeight - 1)),
                    new ImagePoint(Math.Clamp(cx - hHalf * lx - wHalf * sx, 0, imgWidth - 1),
                                   Math.Clamp(cy - hHalf * ly - wHalf * sy, 0, imgHeight - 1)),
                ]);
            }
        }
        else
        {
            var ux = tilt / norm;
            var uy = 1.0 / norm;
            var vx = 1.0 / norm;
            var vy = -tilt / norm;

            var wHalf = (w * 1.04) / 2.0;
            var hHalf = (h * 1.08) / 2.0;

            var shift = h * centerShiftFraction;
            var cx = (x0 + w / 2.0) + shift * ux;
            var cy = (y0 + h / 2.0) + shift * uy;

            return new CardQuad(
            [
                new ImagePoint(Math.Clamp(cx - wHalf * vx - hHalf * ux, 0, imgWidth - 1),
                               Math.Clamp(cy - wHalf * vy - hHalf * uy, 0, imgHeight - 1)),
                new ImagePoint(Math.Clamp(cx + wHalf * vx - hHalf * ux, 0, imgWidth - 1),
                               Math.Clamp(cy + wHalf * vy - hHalf * uy, 0, imgHeight - 1)),
                new ImagePoint(Math.Clamp(cx + wHalf * vx + hHalf * ux, 0, imgWidth - 1),
                               Math.Clamp(cy + wHalf * vy + hHalf * uy, 0, imgHeight - 1)),
                new ImagePoint(Math.Clamp(cx - wHalf * vx + hHalf * ux, 0, imgWidth - 1),
                               Math.Clamp(cy - wHalf * vy + hHalf * uy, 0, imgHeight - 1)),
            ]);
        }
    }

    /// <summary>Every card-shaped box, at every size, whose four sides and interior hold up.</summary>
    private static List<Box> Propose(Frame frame, CardArrangementOptions opts, double aspect, double minEdge = -1)
    {
        var bar = minEdge < 0 ? opts.MinEdge : minEdge;
        var proposals = new List<Box>();
        var widest = (int)Math.Min(frame.Width, frame.Height * aspect) - 1;
        var narrowest = Math.Max(16, (int)(0.05 * Math.Min(frame.Width, frame.Height)));
        var step = Math.Max(1, frame.Width / 180);

        for (var boxWidth = narrowest; boxWidth <= widest; boxWidth += Math.Max(1, boxWidth / 40))
        {
            var boxHeight = (int)Math.Round(boxWidth / aspect);
            if (boxHeight >= frame.Height) break;

            for (var y = 0; y + boxHeight < frame.Height; y += step)
            {
                for (var x = 0; x + boxWidth < frame.Width; x += step)
                {
                    var box = Measure(frame, x, y, boxWidth, boxHeight);
                    if (box.Edge < bar || box.Detail < opts.MinDetail) continue;
                    proposals.Add(box);
                }
            }
        }

        return proposals;
    }

    /// <summary>Measures a candidate box: its weakest side, and how busy it is inside.</summary>
    private static Box Measure(Frame frame, int x, int y, int boxWidth, int boxHeight)
    {
        var band = Math.Max(1, Math.Min(boxWidth, boxHeight) / 30);

        var left = frame.MeanAcross(x - band, y, x + band + 1, y + boxHeight);
        var right = frame.MeanAcross(x + boxWidth - band, y, x + boxWidth + band + 1, y + boxHeight);
        var top = frame.MeanDown(x, y - band, x + boxWidth, y + band + 1);
        var bottom = frame.MeanDown(x, y + boxHeight - band, x + boxWidth, y + boxHeight + band + 1);

        var edge = Math.Min(Math.Min(left, right), Math.Min(top, bottom)) / frame.MeanGradient;

        var inset = (int)(Math.Min(boxWidth, boxHeight) * 0.18);
        var detail = frame.MeanAll(x + inset, y + inset, x + boxWidth - inset, y + boxHeight - inset)
                   / frame.MeanGradient;

        return new Box(x, y, boxWidth, boxHeight, edge, detail);
    }

    /// <summary>
    /// Lets the photograph vote on one card width, and keeps the boxes that carry it.
    /// <para>
    /// Each width is asked how much of the photograph it accounts for: the proposals at that width
    /// are thinned so that each place is counted once, and what survives is weighted by how much
    /// of the frame it covers. Counting proposals instead would elect the smallest width on the
    /// board every time, because a small box has far more places to sit — which is how a detector
    /// ends up reporting the specks inside one card's artwork as a dozen cards.
    /// </para>
    /// </summary>
    private static List<Box> KeepOneSize(List<Box> proposals, CardArrangementOptions opts, long frameArea, double aspect)
    {
        List<Box>? winner = null;
        var mostVotes = 0d;

        foreach (var candidate in proposals.Select(b => b.Width).Distinct().OrderBy(w => w))
        {
            var sized = Thin(proposals.Where(b => Math.Abs(b.Width - candidate) <= candidate * opts.SizeSlack), opts);
            var votes = sized.Sum(b => b.Edge) * ((double)candidate * candidate / aspect) / frameArea;

            if (votes > mostVotes) (mostVotes, winner) = (votes, sized);
        }

        return winner ?? [];
    }

    /// <summary>
    /// Decides whether the cards just found are in fact the halves of cards lying on their side,
    /// and if they are, finds the whole cards instead.
    /// <para>
    /// A binder page photographed sideways — the page turned a quarter turn, which is how anyone
    /// holding a phone in landscape photographs one — puts every card on its side. A card on its
    /// side is two upright cards wide, near enough: 63/88 is within two per cent of half of 88/63,
    /// so each half of it is card-shaped, has four sides, and is busy inside. Each half is also
    /// twice as common as the whole, so it wins the size vote outright. Nothing local tells the
    /// two apart.
    /// </para>
    /// <para>
    /// What tells them apart is the photograph repeating. Cards sit at a regular spacing, so a
    /// page slid sideways by one card's spacing lands card on card and looks like itself. Slid by
    /// half a card it lands a card's artwork on its text box, and does not. So the spacing of the
    /// boxes found is tested against the spacing of twice that: when the double spacing is the one
    /// the photograph agrees with, the boxes were halves, and the search is run again for cards on
    /// their side at twice the width. The bar is lower the second time, because the border that
    /// matters — the card's own, rather than the crisp edge of its text box — is the one that was
    /// too faint to win the first time.
    /// </para>
    /// </summary>
    private static List<Box>? WholeCardsFromHalves(Frame frame, List<Box> cards, CardArrangementOptions opts)
    {
        if (cards.Count < 4) return null;

        var boxWidth = (int)Math.Round(cards.Average(b => (double)b.Width));
        var pitch = (int)Math.Round(Pitch(cards.Select(b => b.X + b.Width / 2d).ToList(), boxWidth * 0.6));
        if (pitch < 8 || pitch * 2 >= frame.Width) return null;

        var region = (
            X0: cards.Min(b => b.X),
            Y0: cards.Min(b => b.Y),
            X1: cards.Max(b => b.X + b.Width),
            Y1: cards.Max(b => b.Y + b.Height));

        var single = frame.SelfSimilarity(region.X0, region.Y0, region.X1, region.Y1, pitch);
        var doubled = frame.SelfSimilarity(region.X0, region.Y0, region.X1, region.Y1, pitch * 2);
        if (doubled < opts.MinSelfSimilarity || doubled < single * opts.HalvesMargin) return null;

        var wide = Propose(frame, opts, OnItsSide, opts.MinEdge * opts.CompletionRelief)
            .Where(b => Math.Abs(b.Width - pitch * 2) <= pitch * 2 * opts.SizeSlack)
            .ToList();

        var whole = Thin(wide, opts);
        return whole.Count == 0 ? null : whole;
    }

    /// <summary>Keeps the best box of each cluster, so that one card is reported once.</summary>
    private static List<Box> Thin(IEnumerable<Box> boxes, CardArrangementOptions opts)
    {
        var kept = new List<Box>();
        foreach (var box in boxes.OrderByDescending(b => b.Edge))
        {
            if (kept.Any(k => Overlap(k, box) > opts.MaxOverlap)) continue;
            kept.Add(box);
        }
        return kept;
    }

    /// <summary>
    /// Looks again wherever the cards already found say another should be. The prediction only
    /// chooses where to look: a box still has to earn its place, at a lower bar than a card found
    /// unaided but a real one, so an empty pocket in the middle of a page is not invented.
    /// </summary>
    private static List<Box> Complete(Frame frame, List<Box> cards, CardArrangementOptions opts, double aspect,
        double searchWindowFraction = 0.3)
    {
        if (cards.Count < 2) return cards;

        var boxWidth = (int)Math.Round(cards.Average(b => (double)b.Width));
        var boxHeight = (int)Math.Round(boxWidth / aspect);

        var centresX = cards.Select(b => b.X + b.Width / 2d).ToList();
        var centresY = cards.Select(b => b.Y + b.Height / 2d).ToList();

        var pitchX = Pitch(centresX, boxWidth * 0.6);
        var pitchY = Pitch(centresY, boxHeight * 0.6);
        if (pitchX <= 0 || pitchY <= 0) return cards;

        var originX = Origin(centresX, pitchX);
        var originY = Origin(centresY, pitchY);

        // A page photographed from one side has its far column higher in the frame than its near
        // one, so the spacing is only a hint. The window is what absorbs that, and the tilt, and
        // the few pixels a card slides inside its pocket.
        var window = (int)(boxWidth * searchWindowFraction);
        var bar = opts.MinEdge * opts.CompletionRelief;

        var completed = new List<Box>(cards);
        for (var centreY = originY - pitchY * 4; centreY < frame.Height + pitchY * 4; centreY += pitchY)
        {
            for (var centreX = originX - pitchX * 4; centreX < frame.Width + pitchX * 4; centreX += pitchX)
            {
                Box? best = null;
                for (var dy = -window; dy <= window; dy += 2)
                {
                    for (var dx = -window; dx <= window; dx += 2)
                    {
                        var x = (int)Math.Round(centreX - boxWidth / 2d) + dx;
                        var y = (int)Math.Round(centreY - boxHeight / 2d) + dy;
                        if (x < 0 || y < 0 || x + boxWidth >= frame.Width || y + boxHeight >= frame.Height) continue;

                        var box = Measure(frame, x, y, boxWidth, boxHeight);
                        if (box.Edge < bar || box.Detail < opts.MinDetail) continue;
                        if (completed.Any(k => Overlap(k, box) > 0.25)) continue;
                        if (best is null || box.Edge > best.Edge) best = box;
                    }
                }

                if (best is not null) completed.Add(best);
            }
        }

        return completed;
    }

    /// <summary>
    /// Drops anything far flatter than the typical card in this photo. An empty binder pocket shows
    /// the backing of the page: it has four sides, because the pockets around it draw them, and it
    /// has a card's shape, because the pocket does. Only its emptiness gives it away, and only
    /// beside the cards it shares a page with — against a photograph's own average it is not
    /// unusually flat.
    /// </summary>
    private static List<Box> DropEmptyPockets(List<Box> cards, CardArrangementOptions opts)
    {
        if (cards.Count < 2) return cards;

        var typical = Median(cards.Select(b => b.Detail).ToArray());
        if (typical <= 0) return cards;

        return cards.Where(b => b.Detail >= typical * opts.MinRelativeDetail).ToList();
    }

    /// <summary>
    /// The spacing a set of centres share, by least squares over every pair once the pairs have
    /// been told how many places apart they are. Taking the smallest real gap as the unit and then
    /// fitting the rest is what lets a row with a hole in it measure the same spacing as a full one.
    /// </summary>
    private static double Pitch(List<double> centres, double smallestGap)
    {
        if (centres.Count < 2) return 0;

        var sorted = centres.OrderBy(v => v).ToList();
        var unit = double.PositiveInfinity;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            if (gap >= smallestGap && gap < unit) unit = gap;
        }
        if (double.IsInfinity(unit)) return 0;

        var numerator = 0d;
        var denominator = 0d;
        for (var i = 0; i < sorted.Count; i++)
        {
            for (var j = i + 1; j < sorted.Count; j++)
            {
                var distance = sorted[j] - sorted[i];
                var places = Math.Round(distance / unit);
                if (places < 1) continue;
                if (Math.Abs(distance - places * unit) > unit * 0.25) continue;

                numerator += distance * places;
                denominator += places * places;
            }
        }

        return denominator <= 0 ? unit : numerator / denominator;
    }

    /// <summary>Where the lattice of a given spacing falls, taken as the typical centre.</summary>
    private static double Origin(List<double> centres, double pitch)
    {
        var first = centres.Min();
        var offsets = centres
            .Select(c => c - Math.Round((c - first) / pitch) * pitch)
            .ToArray();
        return Median(offsets);
    }

    private static double Overlap(Box a, Box b)
    {
        var across = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var down = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return smaller <= 0 ? 0 : across * (double)down / smaller;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// The photo reduced to what the search asks about: how much across-edge, down-edge and total
    /// gradient lies in any rectangle. Summed-area tables make each of those a constant-time
    /// question, which is what lets every box at every size be measured rather than guessed at.
    /// </summary>
    private sealed class Frame
    {
        private readonly byte[] _luminance;
        private readonly double[] _across;
        private readonly double[] _down;
        private readonly double[] _all;

        public int Width { get; }
        public int Height { get; }
        public double MeanGradient { get; }

        public Frame(Image<Rgba32> image, int width, int height)
        {
            Width = width;
            Height = height;

            var luminance = _luminance = new byte[width * height];
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = row[x];
                        luminance[y * width + x] =
                            (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
                    }
                }
            });

            var across = new double[width * height];
            var down = new double[width * height];
            var all = new double[width * height];
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var i = y * width + x;
                    across[i] = Math.Abs(luminance[i + 1] - luminance[i - 1]);
                    down[i] = Math.Abs(luminance[i + width] - luminance[i - width]);
                    all[i] = across[i] + down[i];
                }
            }

            _across = Integral(across, width, height);
            _down = Integral(down, width, height);
            _all = Integral(all, width, height);

            MeanGradient = Sum(_all, width, 0, 0, width, height) / (width * (double)height);
        }

        /// <summary>
        /// How alike a region is to itself slid sideways by a given distance, as a correlation
        /// between -1 and 1. One card's spacing apart, a page of cards looks like itself.
        /// </summary>
        public double SelfSimilarity(int x0, int y0, int x1, int y1, int lag)
        {
            x0 = Math.Clamp(x0, 0, Width);
            x1 = Math.Clamp(x1, 0, Width);
            y0 = Math.Clamp(y0, 0, Height);
            y1 = Math.Clamp(y1, 0, Height);
            if (lag <= 0 || x1 - x0 <= lag || y1 <= y0) return 0;

            double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
            var count = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x + lag < x1; x++)
                {
                    double a = _luminance[y * Width + x];
                    double b = _luminance[y * Width + x + lag];
                    sumA += a; sumB += b; sumAA += a * a; sumBB += b * b; sumAB += a * b;
                    count++;
                }
            }
            if (count < 64) return 0;

            var meanA = sumA / count;
            var meanB = sumB / count;
            var varianceA = sumAA / count - meanA * meanA;
            var varianceB = sumBB / count - meanB * meanB;
            if (varianceA <= 1e-9 || varianceB <= 1e-9) return 0;

            return (sumAB / count - meanA * meanB) / Math.Sqrt(varianceA * varianceB);
        }

        public double MeanAcross(int x0, int y0, int x1, int y1) => Mean(_across, x0, y0, x1, y1);

        public double MeanDown(int x0, int y0, int x1, int y1) => Mean(_down, x0, y0, x1, y1);

        public double MeanAll(int x0, int y0, int x1, int y1) => Mean(_all, x0, y0, x1, y1);

        public double MeanLuminance(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, Width);
            x1 = Math.Clamp(x1, 0, Width);
            y0 = Math.Clamp(y0, 0, Height);
            y1 = Math.Clamp(y1, 0, Height);
            if (x1 <= x0 || y1 <= y0) return 0;
            double sum = 0;
            var count = 0;
            for (var y = y0; y < y1; y++)
            {
                var rowStart = y * Width;
                for (var x = x0; x < x1; x++)
                {
                    sum += _luminance[rowStart + x];
                    count++;
                }
            }
            return count == 0 ? 0 : sum / count;
        }

        private double Mean(double[] table, int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, Width);
            x1 = Math.Clamp(x1, 0, Width);
            y0 = Math.Clamp(y0, 0, Height);
            y1 = Math.Clamp(y1, 0, Height);

            var area = (x1 - x0) * (long)(y1 - y0);
            return area <= 0 ? 0 : Sum(table, Width, x0, y0, x1, y1) / area;
        }

        private static double[] Integral(double[] values, int width, int height)
        {
            var table = new double[(width + 1) * (height + 1)];
            for (var y = 0; y < height; y++)
            {
                var running = 0d;
                for (var x = 0; x < width; x++)
                {
                    running += values[y * width + x];
                    table[(y + 1) * (width + 1) + x + 1] = table[y * (width + 1) + x + 1] + running;
                }
            }
            return table;
        }

        private static double Sum(double[] table, int width, int x0, int y0, int x1, int y1)
            => table[y1 * (width + 1) + x1]
             - table[y0 * (width + 1) + x1]
             - table[y1 * (width + 1) + x0]
             + table[y0 * (width + 1) + x0];
    }
}

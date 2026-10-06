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
        cards = DropEmptyPockets(cards, opts);

        return cards
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .Select(b => new DetectedCard(
                QuadOf(b, scale),
                Math.Round(Math.Clamp(b.Edge / 2d, 0d, 1d), 3)))
            .ToList();
    }

    private static CardQuad QuadOf(Box box, double scale)
    {
        var x0 = box.X / scale;
        var y0 = box.Y / scale;
        var x1 = (box.X + box.Width) / scale;
        var y1 = (box.Y + box.Height) / scale;

        return new CardQuad(
        [
            new ImagePoint(x0, y0),
            new ImagePoint(x1, y0),
            new ImagePoint(x1, y1),
            new ImagePoint(x0, y1),
        ]);
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
    private static List<Box> Complete(Frame frame, List<Box> cards, CardArrangementOptions opts, double aspect)
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
        var window = (int)(boxWidth * 0.3);
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

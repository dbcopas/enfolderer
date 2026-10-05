using Enfolderer.Ai.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

/// <summary>Tuning for <see cref="PageGridDetector"/>.</summary>
public sealed record PageGridOptions
{
    /// <summary>Pockets down the page. Zero disables the page detector entirely.</summary>
    public int Rows { get; init; } = 3;

    /// <summary>Pockets across the page.</summary>
    public int Columns { get; init; } = 3;

    /// <summary>Longest edge, in pixels, the fit runs at.</summary>
    public int WorkingSize { get; init; } = 360;

    /// <summary>
    /// How far a pocket's proportions may stray from a card's before the fit is not believed,
    /// as a fraction either way. Perspective and the pocket's own margin both widen a cell a
    /// little, so this is not as tight as the card itself.
    /// </summary>
    public double AspectSlack { get; init; } = 0.10;

    /// <summary>
    /// How near a candidate grid line the supporting edge may be, in working pixels. A page
    /// photographed by hand is never perfectly square to the camera, so the line that separates
    /// two pockets wanders by a pixel or two along its length.
    /// </summary>
    public int LineRadius { get; init; } = 3;

    /// <summary>
    /// Least share of the photo the whole grid must cover before the fit is believed.
    /// <para>
    /// This is the one check that says "that was a page" rather than "that was nine of
    /// something". A grid can always be laid over part of a photo and scored well, and a small
    /// one that lands on a single card divides it into its art box and text box — exactly the
    /// failure this detector exists to avoid. Someone photographing a page fills the frame with
    /// it, so a grid covering a third of the frame is not the page.
    /// </para>
    /// </summary>
    public double MinCoverage { get; init; } = 0.40;

    /// <summary>
    /// How much of a cell is ignored at its edge when judging whether a pocket holds a card, as
    /// a fraction of the cell. Keeps the pocket seam and the card's own border out of it.
    /// </summary>
    public double CellInset { get; init; } = 0.15;

    /// <summary>
    /// How much detail a pocket must show, relative to the typical pocket on this page, to count
    /// as holding a card. An empty pocket shows the binder's backing: flat and usually dark.
    /// </summary>
    public double MinCellDetail { get; init; } = 0.40;

    /// <summary>Least number of filled pockets before the fit is reported at all.</summary>
    public int MinFilledCells { get; init; } = 2;

    /// <summary>
    /// How much edge the weakest line of the grid must carry, as a z-score of the photo's own
    /// edge profile.
    /// <para>
    /// Every line is judged, not the average of them, because the average is exactly what a wrong
    /// grid exploits: a grid laid across a single row of cards has four strong verticals and two
    /// horizontals running through the middle of the artwork, and that averages well. A page has
    /// no weak lines, because every one of them is a pocket seam.
    /// </para>
    /// </summary>
    public double MinLineSupport { get; init; } = 0.5;
}

/// <summary>
/// Finds the cards on a page of a binder by fitting the page's own pocket grid to the photo.
/// <para>
/// A photograph of a binder page is not a photograph of loose cards, and the difference defeats
/// <see cref="CardDetector"/>'s approach entirely. There is no background between the cards for a
/// flood to enter: the pockets abut, the whole page is one object, and what little shows between
/// two cards is the same dark plastic that surrounds the page. Worse, every card is behind a
/// sheet of polypropylene that reflects the room, so a card's own border is often the weakest
/// edge in its neighbourhood rather than the strongest.
/// </para>
/// <para>
/// But a page offers something a scattering of cards does not: its pockets are a rigid lattice of
/// nine identical cells, so once the photo is framed the entire layout is four numbers — a pitch
/// across, a pitch down, and where the first line falls on each axis. That is few enough to search
/// exhaustively, which is what this does. Each candidate is scored by how much edge lies along its
/// lines, summed over the four verticals and four horizontals, and the lines are real: a pocket
/// seam, a card border, or both together.
/// </para>
/// <para>
/// Fixing the number of pockets in advance is what makes this stable where measuring it from the
/// photo was not. A card's proportions are near enough 1/sqrt(2) that a block of them, and equally
/// a piece of one, has a card's shape too, so a free-running fit is as happy to call a card's art
/// box a pocket as it is to call a pocket a pocket. The page tells us there are nine, so the only
/// question left is where they are, and that has one good answer.
/// </para>
/// <para>
/// Empty pockets are then dropped on their own evidence rather than the grid's: a pocket with a
/// card in it is full of detail, and one showing the binder's backing is flat.
/// </para>
/// </summary>
public static class PageGridDetector
{
    private const double CardAspect = PerspectiveCropper.StandardCardAspect;

    /// <summary>A grid fitted to a photo, in source-image pixels.</summary>
    /// <param name="Cards">One quad per filled pocket, in reading order.</param>
    /// <param name="Confidence">Mean edge support along the grid lines, as a z-score.</param>
    public sealed record PageGrid(IReadOnlyList<DetectedCard> Cards, double Confidence);

    /// <summary>
    /// Fits the page grid to an already-decoded photo, or returns null when the photo does not
    /// look like a page of pockets.
    /// </summary>
    public static PageGrid? TryDetect(Image<Rgba32> source, PageGridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var opts = options ?? new PageGridOptions();
        if (opts.Rows < 1 || opts.Columns < 1) return null;

        var scale = Math.Min(1d, (double)opts.WorkingSize / Math.Max(source.Width, source.Height));
        var width = Math.Max(16, (int)Math.Round(source.Width * scale));
        var height = Math.Max(16, (int)Math.Round(source.Height * scale));

        using var working = source.Clone(c => c.Resize(width, height));
        var luminance = Luminance(working, width, height);

        var fit = Fit(luminance, width, height, opts);
        if (fit is null) return null;

        var (score, pitchX, pitchY, originX, originY) = fit.Value;

        var detail = CellDetail(luminance, width, height, opts, pitchX, pitchY, originX, originY);
        var typical = Median(detail);
        if (typical <= 0) return null;

        var cards = new List<DetectedCard>();
        for (var row = 0; row < opts.Rows; row++)
        {
            for (var column = 0; column < opts.Columns; column++)
            {
                if (detail[row * opts.Columns + column] < typical * opts.MinCellDetail) continue;

                var x0 = (originX + column * pitchX) / scale;
                var y0 = (originY + row * pitchY) / scale;
                var x1 = (originX + (column + 1) * pitchX) / scale;
                var y1 = (originY + (row + 1) * pitchY) / scale;

                var quad = new CardQuad(
                [
                    new ImagePoint(x0, y0),
                    new ImagePoint(x1, y0),
                    new ImagePoint(x1, y1),
                    new ImagePoint(x0, y1),
                ]);

                cards.Add(new DetectedCard(quad, Math.Round(Math.Clamp(score / 3d, 0d, 1d), 3)));
            }
        }

        return cards.Count < opts.MinFilledCells ? null : new PageGrid(cards, Math.Round(score, 3));
    }

    private static byte[] Luminance(Image<Rgba32> image, int width, int height)
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
    /// Edge energy per column and per row, standardised so the two can be added together and so
    /// that a dim photo scores like a bright one.
    /// </summary>
    private static (double[] Columns, double[] Rows) Profiles(byte[] luminance, int width, int height)
    {
        var columns = new double[width];
        var rows = new double[height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width - 1; x++)
                columns[x] += Math.Abs(luminance[y * width + x + 1] - luminance[y * width + x]);
        }

        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width; x++)
                rows[y] += Math.Abs(luminance[(y + 1) * width + x] - luminance[y * width + x]);
        }

        for (var x = 0; x < width; x++) columns[x] /= height;
        for (var y = 0; y < height; y++) rows[y] /= width;

        return (Standardise(columns), Standardise(rows));
    }

    private static double[] Standardise(double[] values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        var deviation = Math.Sqrt(variance);
        if (deviation < 1e-9) deviation = 1e-9;

        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = (values[i] - mean) / deviation;
        return result;
    }

    /// <summary>
    /// Takes the strongest value within <paramref name="radius"/> of each position, so that a grid
    /// line still scores when the page is a degree or two out of square and its edge drifts across
    /// a column or two along its length.
    /// </summary>
    private static double[] Spread(double[] profile, int radius)
    {
        var result = new double[profile.Length];
        for (var i = 0; i < profile.Length; i++)
        {
            var best = double.NegativeInfinity;
            var from = Math.Max(0, i - radius);
            var to = Math.Min(profile.Length - 1, i + radius);
            for (var j = from; j <= to; j++) best = Math.Max(best, profile[j]);
            result[i] = best;
        }
        return result;
    }

    /// <summary>Best worst-line support, and where it falls, for a given count and pitch.</summary>
    private static (double Score, int Origin)? BestOffset(double[] profile, int count, int pitch)
    {
        var span = count * pitch;
        if (span >= profile.Length) return null;

        (double Score, int Origin)? best = null;
        for (var origin = 0; origin + span < profile.Length; origin++)
        {
            var weakest = double.PositiveInfinity;
            for (var i = 0; i <= count; i++) weakest = Math.Min(weakest, profile[origin + i * pitch]);

            if (best is null || weakest > best.Value.Score) best = (weakest, origin);
        }
        return best;
    }

    /// <summary>
    /// Searches every grid whose cells have a card's proportions and keeps the best supported.
    /// <para>
    /// The two axes are scored independently and only then combined, which is what keeps this
    /// cheap: the across-fit does not depend on the down-fit except through the shape of a cell,
    /// so each is solved once per pitch rather than once per pair.
    /// </para>
    /// </summary>
    private static (double Score, int PitchX, int PitchY, int OriginX, int OriginY)? Fit(
        byte[] luminance, int width, int height, PageGridOptions opts)
    {
        var (columnProfile, rowProfile) = Profiles(luminance, width, height);
        var across = Spread(columnProfile, opts.LineRadius);
        var down = Spread(rowProfile, opts.LineRadius);

        var acrossFits = new Dictionary<int, (double Score, int Origin)>();
        for (var pitch = width / 8; pitch * opts.Columns < width; pitch++)
        {
            var fit = BestOffset(across, opts.Columns, pitch);
            if (fit is not null) acrossFits[pitch] = fit.Value;
        }

        var downFits = new Dictionary<int, (double Score, int Origin)>();
        for (var pitch = height / 8; pitch * opts.Rows < height; pitch++)
        {
            var fit = BestOffset(down, opts.Rows, pitch);
            if (fit is not null) downFits[pitch] = fit.Value;
        }

        (double Score, int PitchX, int PitchY, int OriginX, int OriginY)? best = null;
        var smallest = opts.MinCoverage * width * height;
        foreach (var (pitchX, acrossFit) in acrossFits)
        {
            // A pocket is a card plus a little margin, so its shape is the card's shape. Tying the
            // two pitches together this way is what stops the fit from finding nine of something
            // that is not a card.
            var ideal = pitchX / CardAspect;
            var from = (int)Math.Floor(ideal * (1 - opts.AspectSlack));
            var to = (int)Math.Ceiling(ideal * (1 + opts.AspectSlack));

            for (var pitchY = from; pitchY <= to; pitchY++)
            {
                if (!downFits.TryGetValue(pitchY, out var downFit)) continue;

                // A grid can be laid over any part of any photo and scored well, and a small one
                // that lands on a single card divides it into its art box and its text box. Only
                // a grid big enough to be the page itself is considered at all, rather than
                // picking the best-scoring grid and rejecting it afterwards, which would throw
                // away a good fit because a bad one outscored it.
                if ((double)opts.Columns * pitchX * opts.Rows * pitchY < smallest) continue;

                var score = Math.Min(acrossFit.Score, downFit.Score);
                if (score < opts.MinLineSupport) continue;
                if (best is null || score > best.Value.Score)
                    best = (score, pitchX, pitchY, acrossFit.Origin, downFit.Origin);
            }
        }

        return best;
    }

    /// <summary>Mean gradient inside each cell: high for a card, near zero for an empty pocket.</summary>
    private static double[] CellDetail(
        byte[] luminance, int width, int height, PageGridOptions opts,
        int pitchX, int pitchY, int originX, int originY)
    {
        var detail = new double[opts.Rows * opts.Columns];
        var insetX = (int)Math.Round(pitchX * opts.CellInset);
        var insetY = (int)Math.Round(pitchY * opts.CellInset);

        for (var row = 0; row < opts.Rows; row++)
        {
            for (var column = 0; column < opts.Columns; column++)
            {
                var x0 = Math.Clamp(originX + column * pitchX + insetX, 0, width - 2);
                var x1 = Math.Clamp(originX + (column + 1) * pitchX - insetX, x0 + 1, width - 1);
                var y0 = Math.Clamp(originY + row * pitchY + insetY, 0, height - 2);
                var y1 = Math.Clamp(originY + (row + 1) * pitchY - insetY, y0 + 1, height - 1);

                var total = 0d;
                var count = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var i = y * width + x;
                        total += Math.Abs(luminance[i + 1] - luminance[i])
                               + Math.Abs(luminance[i + width] - luminance[i]);
                        count++;
                    }
                }

                detail[row * opts.Columns + column] = count == 0 ? 0 : total / count;
            }
        }

        return detail;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}

using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Imaging;

/// <summary>
/// Projective (perspective) transform mapping the unit square onto a quadrilateral. This is what
/// makes an obliquely photographed card come out rectangular; a plain affine or bilinear mapping
/// visibly skews the card face and degrades identification.
/// </summary>
public readonly struct Homography
{
    private readonly double _a, _b, _c, _d, _e, _f, _g, _h;

    private Homography(double a, double b, double c, double d, double e, double f, double g, double h)
    {
        _a = a; _b = b; _c = c; _d = d; _e = e; _f = f; _g = g; _h = h;
    }

    /// <summary>
    /// Builds the transform sending (0,0), (1,0), (1,1), (0,1) onto the quad's four points, which
    /// are ordered top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    public static Homography FromUnitSquare(CardQuad quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (!quad.IsValid)
            throw new ArgumentException($"A quad must have exactly {CardQuad.RequiredPointCount} points.", nameof(quad));

        var p0 = quad.Points[0];
        var p1 = quad.Points[1];
        var p2 = quad.Points[2];
        var p3 = quad.Points[3];

        var dx1 = p1.X - p2.X;
        var dx2 = p3.X - p2.X;
        var dx3 = p0.X - p1.X + p2.X - p3.X;
        var dy1 = p1.Y - p2.Y;
        var dy2 = p3.Y - p2.Y;
        var dy3 = p0.Y - p1.Y + p2.Y - p3.Y;

        var determinant = dx1 * dy2 - dy1 * dx2;

        // A parallelogram (or a degenerate quad) reduces to the affine case.
        if (Math.Abs(determinant) < 1e-12 || (Math.Abs(dx3) < 1e-12 && Math.Abs(dy3) < 1e-12))
        {
            return new Homography(
                p1.X - p0.X, p3.X - p0.X, p0.X,
                p1.Y - p0.Y, p3.Y - p0.Y, p0.Y,
                0, 0);
        }

        var g = (dx3 * dy2 - dy3 * dx2) / determinant;
        var h = (dx1 * dy3 - dy1 * dx3) / determinant;

        return new Homography(
            p1.X - p0.X + g * p1.X, p3.X - p0.X + h * p3.X, p0.X,
            p1.Y - p0.Y + g * p1.Y, p3.Y - p0.Y + h * p3.Y, p0.Y,
            g, h);
    }

    /// <summary>Maps a unit-square coordinate onto source-image pixel coordinates.</summary>
    public (double X, double Y) Project(double u, double v)
    {
        var w = _g * u + _h * v + 1d;
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return ((_a * u + _b * v + _c) / w, (_d * u + _e * v + _f) / w);
    }
}

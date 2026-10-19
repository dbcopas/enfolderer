using System.Runtime.InteropServices;
using Enfolderer.Ai.Contracts;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

internal static class CardContourDetector
{
    public static IReadOnlyList<DetectedCard> Detect(Image<Rgba32> source, CardDetectorOptions options,
        bool includeFrameAnchors = true)
    {
        var scale = Math.Min(1d, options.WorkingSize / (double)Math.Max(source.Width, source.Height));
        using var working = source.Clone(c => c.Resize(
            Math.Max(8, (int)Math.Round(source.Width * scale)), Math.Max(8, (int)Math.Round(source.Height * scale))));
        var pixels = new Rgba32[working.Width * working.Height];
        working.CopyPixelDataTo(pixels);
        using var rgba = new Mat(working.Height, working.Width, MatType.CV_8UC4);
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        Marshal.Copy(bytes, 0, rgba.Data, bytes.Length);
        using var gray = new Mat();
        Cv2.CvtColor(rgba, gray, ColorConversionCodes.RGBA2GRAY);
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);
        var candidates = new List<DetectedCard>();
        var bars = new List<RotatedRect>();
        var allSegments = new List<Vec4f>();
        // Foil and coloured frames can disappear into grayscale or a dark tabletop.
        for (var channelIndex = -1; channelIndex < 3; channelIndex++)
        {
            using var channel = new Mat();
            if (channelIndex < 0) gray.CopyTo(channel);
            else
            {
                Cv2.ExtractChannel(rgba, channel, channelIndex);
                Cv2.GaussianBlur(channel, channel, new OpenCvSharp.Size(3, 3), 0);
            }
            foreach (var threshold in new[] { 5, 15, 30, 60 })
            {
                using var edges = new Mat();
                Cv2.Canny(channel, edges, threshold, threshold * 3);
                Cv2.FindContours(edges, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List,
                    ContourApproximationModes.ApproxSimple);
                foreach (var contour in contours)
                {
                    var hull = Cv2.ConvexHull(contour);
                    if (hull.Length < 4) continue;
                    var rect = Cv2.MinAreaRect(hull);
                    var shortSide = Math.Min(rect.Size.Width, rect.Size.Height);
                    var longSide = Math.Max(rect.Size.Width, rect.Size.Height);
                    var polygon = Cv2.ApproxPolyDP(hull, Cv2.ArcLength(hull, true) * 0.025, true);
                    if (polygon.Length == 4 && shortSide >= options.MinCellShortSide
                        && Math.Abs(shortSide / longSide / PerspectiveCropper.StandardCardAspect - 1) < 0.15
                        && Cv2.ContourArea(polygon) > working.Width * working.Height * options.MinAreaFraction
                        && Cv2.ContourArea(polygon) < working.Width * working.Height * options.MaxAreaFraction)
                    {
                        // A tapered artwork hull is not a physical card's perspective transform.
                        // Enclose it conservatively, including the title and collector footer.
                        var enclosing = new RotatedRect(rect.Center, new Size2f(
                            rect.Size.Width * (rect.Size.Width > rect.Size.Height ? 1.08f : 1),
                            rect.Size.Height * (rect.Size.Height >= rect.Size.Width ? 1.08f : 1)), rect.Angle);
                        var corners = HasCardCorners(polygon)
                            ? polygon.Select(p => new Point2f(p.X, p.Y)).ToArray() : enclosing.Points();
                        var ordered = corners.OrderBy(p => Math.Atan2(p.Y - rect.Center.Y, p.X - rect.Center.X)).ToArray();
                        var start = Enumerable.Range(0, 4).MinBy(i => ordered[i].X + ordered[i].Y);
                        var points = Enumerable.Range(0, 4).Select(i => ordered[(start + i) % 4])
                            .Select(p => new ImagePoint(Math.Clamp(p.X / scale, 0, source.Width - 1),
                                Math.Clamp(p.Y / scale, 0, source.Height - 1))).ToList();
                        var quad = new CardQuad(points);
                        var box = quad.BoundingBox();
                        if (box.Width / box.Height is > 0.55 and < 0.9)
                            candidates.Add(new(quad, 1));
                    }
                    if (longSide < options.MinCellShortSide || longSide > working.Width * 0.5 || shortSide < 4) continue;
                    if (longSide / shortSide < 6 || longSide / shortSide > 20) continue;
                    if (Cv2.ContourArea(hull) / (shortSide * longSide) < 0.8) continue;
                    bars.Add(rect);
                }
            }
            using var lineDetector = LineSegmentDetector.Create();
            lineDetector.Detect(channel, out Vec4f[] segments, out _, out _, out _);
            allSegments.AddRange(segments);
        }
        var horizontal = allSegments.Where(s => Math.Abs(s.Item2 - s.Item0) > options.MinCellShortSide * 1.5
            && Math.Abs((s.Item3 - s.Item1) / (s.Item2 - s.Item0)) < 0.2).ToList();
        foreach (var first in horizontal)
            foreach (var second in horizontal)
            {
                var a0 = Math.Min(first.Item0, first.Item2);
                var a1 = Math.Max(first.Item0, first.Item2);
                var b0 = Math.Min(second.Item0, second.Item2);
                var b1 = Math.Max(second.Item0, second.Item2);
                var x0 = Math.Max(a0, b0);
                var x1 = Math.Min(a1, b1);
                var w = x1 - x0;
                if (w < options.MinCellShortSide * 1.5 || w < Math.Max(a1 - a0, b1 - b0) * 0.85) continue;
                var slopeA = (first.Item3 - first.Item1) / (first.Item2 - first.Item0);
                var slopeB = (second.Item3 - second.Item1) / (second.Item2 - second.Item0);
                if (Math.Abs(slopeA - slopeB) > 0.03) continue;
                var x = (x0 + x1) / 2;
                var y0 = first.Item1 + (x - first.Item0) * slopeA;
                var y1 = second.Item1 + (x - second.Item0) * slopeB;
                var h = y1 - y0;
                if (h < 4 || w / h < 6 || w / h > 20) continue;
                bars.Add(new RotatedRect(new Point2f(x, (y0 + y1) / 2), new Size2f(w, h),
                    (float)(Math.Atan((slopeA + slopeB) / 2) * 180 / Math.PI)));
            }
        // Extended-art title frames remain measurable when the physical border blends into black.
        foreach (var rect in bars.Where(_ => includeFrameAnchors))
        {
            var longSide = Math.Max(rect.Size.Width, rect.Size.Height);
            var corners = rect.Points();
            var edge = Enumerable.Range(0, 4).MaxBy(i =>
                Math.Pow(corners[(i + 1) % 4].X - corners[i].X, 2)
                + Math.Pow(corners[(i + 1) % 4].Y - corners[i].Y, 2));
            var dx = (corners[(edge + 1) % 4].X - corners[edge].X) / longSide;
            var dy = (corners[(edge + 1) % 4].Y - corners[edge].Y) / longSide;
            if (dx < 0) (dx, dy) = (-dx, -dy);
            if (Math.Abs(dy) > 0.2) continue;
            var w = longSide * 1.15;
            var h = w / PerspectiveCropper.StandardCardAspect * 1.08;
            var cx = rect.Center.X - dy * h * 0.44;
            var cy = rect.Center.Y + dx * h * 0.44;
            var points = new[]
            {
                Point(-0.5, -0.5), Point(0.5, -0.5), Point(0.5, 0.5), Point(-0.5, 0.5)
            };
            if (points.Any(p => p.X < -3 || p.Y < -3 || p.X > working.Width + 3 || p.Y > working.Height + h * 0.15)) continue;
            var support = SurfaceSupport(points, pixels, working.Width, working.Height);
            if (support < 0.5) continue;
            candidates.Add(new(new CardQuad(points.Select(p => new ImagePoint(
                Math.Clamp(p.X / scale, 0, source.Width - 1), Math.Clamp(p.Y / scale, 0, source.Height - 1))).ToList()), support));

            ImagePoint Point(double x, double y) => new(cx + x * w * dx - y * h * dy, cy + x * w * dy + y * h * dx);
        }
        var kept = new List<DetectedCard>();
        var bestVote = 0d;
        foreach (var size in candidates.Select(c => (int)(c.Quad.BoundingBox().Width * scale / 5) * 5).Distinct())
        {
            var trial = new List<DetectedCard>();
            foreach (var card in candidates.Where(c => Math.Abs(c.Quad.BoundingBox().Width * scale - size) < size * 0.15)
                .OrderByDescending(c => c.Confidence).ThenByDescending(c => Area(c.Quad)))
            {
                if (trial.Any(c => Overlap(c.Quad, card.Quad) > 0.25)) continue;
                trial.Add(card);
            }
            var vote = trial.Sum(c => Area(c.Quad) * c.Confidence);
            if (vote > bestVote) (kept, bestVote) = (trial, vote);
        }
        return kept.OrderBy(c => c.Quad.BoundingBox().Y).ThenBy(c => c.Quad.BoundingBox().X).ToList();
    }
    private static double SurfaceSupport(ImagePoint[] points, Rgba32[] pixels, int width, int height)
    {
        var dark = 0;
        var samples = 0;
        for (var side = 0; side < 4; side++)
        {
            var a = points[side];
            var b = points[(side + 1) % 4];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            for (var t = 0.1; t <= 0.9; t += 0.05)
            {
                var x = (int)Math.Round(a.X + dx * t + dy / length * 3);
                var y = (int)Math.Round(a.Y + dy * t - dx / length * 3);
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                var pixel = pixels[y * width + x];
                var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                samples++;
                if (max < 110 && max - min < 35) dark++;
            }

        }
        return samples == 0 ? 0 : dark / (double)samples;
    }

    private static bool HasCardCorners(OpenCvSharp.Point[] points)
    {
        for (var i = 0; i < 4; i++)
        {
            var a = points[(i + 3) % 4];
            var b = points[i];
            var c = points[(i + 1) % 4];
            var ax = (double)a.X - b.X;
            var ay = (double)a.Y - b.Y;
            var cx = (double)c.X - b.X;
            var cy = (double)c.Y - b.Y;
            var length = Math.Sqrt((ax * ax + ay * ay) * (cx * cx + cy * cy));
            if (length == 0 || Math.Abs(ax * cx + ay * cy) / length > 0.25) return false;
        }
        return true;
    }

    private static double Area(CardQuad quad)
    {
        var b = quad.BoundingBox();
        return b.Width * b.Height;
    }

    private static double Overlap(CardQuad first, CardQuad second)
    {
        var a = first.BoundingBox();
        var b = second.BoundingBox();
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Min(Area(first), Area(second));
    }
}

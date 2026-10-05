using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;

namespace Enfolderer.Ai.Imaging.TrainingCheck;

/// <summary>
/// Runs <see cref="CardDetector"/> over the photographs in <c>training/</c> and compares what it
/// finds with the cards listed beside each one.
/// <para>
/// These are real photographs of real binder pages, which is the only kind of evidence that counts
/// here: every synthetic test written for this detector passed against code that found two cards
/// out of nine in a real photo. Run it with <c>dotnet run --project
/// tools/Enfolderer.Ai.Imaging.TrainingCheck</c> after touching anything in
/// <c>Enfolderer.Ai.Imaging</c>.
/// </para>
/// <para>
/// Each <c>cards.txt</c> names the pocket each card sits in as a pair of indices. The two indices
/// are not in a consistent order across the files — <c>training/01</c> lists column first and the
/// others list row first — so a layout is accepted if it matches either way round. What is checked
/// is the shape of the layout, not which corner the counting starts from.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : FindTrainingDirectory();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine("Could not find the training directory. Pass its path as an argument.");
            return 2;
        }

        var failures = 0;
        var totalExpected = 0;
        var totalFound = 0;

        foreach (var directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var listing = Path.Combine(directory, "cards.txt");
            var photo = Directory.GetFiles(directory)
                .FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

            if (!File.Exists(listing) || photo is null) continue;

            var name = Path.GetFileName(directory);
            var expected = ReadExpectedCells(listing);

            using var stream = File.OpenRead(photo);
            var detected = CardDetector.Detect(stream);
            var actual = CellsOf(detected);

            totalExpected += expected.Count;
            totalFound += detected.Count;

            var matches = SameLayout(expected, actual) || SameLayout(Transpose(expected), actual);
            var verdict = matches ? "ok  " : "FAIL";
            if (!matches) failures++;

            Console.WriteLine($"{verdict} {name}: expected {expected.Count} cards, found {detected.Count}");
            Console.WriteLine($"       listed  {Describe(expected)}");
            Console.WriteLine($"       located {Describe(actual)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{totalFound} of {totalExpected} cards located; {failures} photo(s) wrong.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Walks up from the executable to the repository root, which is where training/ lives.</summary>
    private static string? FindTrainingDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "training");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>Reads the pocket of each card from a <c>cards.txt</c>: <c>a,b;name;set;...</c>.</summary>
    private static HashSet<(int A, int B)> ReadExpectedCells(string path)
    {
        var cells = new HashSet<(int, int)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var location = line.Split(';', 2)[0].Split(',');
            if (location.Length != 2) continue;
            if (!int.TryParse(location[0].Trim(), out var a)) continue;
            if (!int.TryParse(location[1].Trim(), out var b)) continue;

            cells.Add((a, b));
        }
        return cells;
    }

    /// <summary>
    /// Places each located card on a lattice of its own, so that a layout can be compared with the
    /// listing without knowing where in the photo the page happened to sit.
    /// </summary>
    private static HashSet<(int A, int B)> CellsOf(IReadOnlyList<DetectedCard> cards)
    {
        var cells = new HashSet<(int, int)>();
        if (cards.Count == 0) return cells;

        var centres = cards.Select(card =>
        {
            var (x, y, w, h) = card.Quad.BoundingBox();
            return (X: x + w / 2, Y: y + h / 2, W: w, H: h);
        }).ToList();

        var pitchX = Math.Max(1, centres.Max(c => c.W));
        var pitchY = Math.Max(1, centres.Max(c => c.H));
        var leftmost = centres.Min(c => c.X);
        var topmost = centres.Min(c => c.Y);

        foreach (var centre in centres)
        {
            var row = (int)Math.Round((centre.Y - topmost) / pitchY);
            var column = (int)Math.Round((centre.X - leftmost) / pitchX);
            cells.Add((row, column));
        }

        return cells;
    }

    private static HashSet<(int A, int B)> Transpose(HashSet<(int A, int B)> cells)
        => cells.Select(c => (c.B, c.A)).ToHashSet();

    /// <summary>
    /// Compares two layouts after sliding each to the origin, because the listing counts from the
    /// corner of the page and the detector counts from the first card it located.
    /// </summary>
    private static bool SameLayout(HashSet<(int A, int B)> left, HashSet<(int A, int B)> right)
    {
        if (left.Count != right.Count) return false;
        if (left.Count == 0) return true;

        return Normalise(left).SetEquals(Normalise(right));
    }

    private static HashSet<(int A, int B)> Normalise(HashSet<(int A, int B)> cells)
    {
        var a = cells.Min(c => c.A);
        var b = cells.Min(c => c.B);
        return cells.Select(c => (c.A - a, c.B - b)).ToHashSet();
    }

    private static string Describe(HashSet<(int A, int B)> cells)
        => cells.Count == 0
            ? "(none)"
            : string.Join(" ", cells.OrderBy(c => c.A).ThenBy(c => c.B).Select(c => $"{c.A},{c.B}"));
}

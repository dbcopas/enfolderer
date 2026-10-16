using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Globalization;
using System.Text;

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
/// Each <c>cards.txt</c> names the pocket each card sits in as <c>row,column</c>, counting from
/// zero at the top left. The comparison slides both layouts to the origin first, because the
/// listing counts from the corner of the page and a photograph may not show that corner — a photo
/// of the middle of a page is still right if the cards are in the right places relative to each
/// other.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--strip-private-metadata")
        {
            var bytes = JpegMetadataSanitizer.StripPrivateMetadata(File.ReadAllBytes(args[1]));
            File.WriteAllBytes(args[2], bytes);
            Console.WriteLine("Removed JPEG private metadata without re-encoding pixels; display orientation retained.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--inspect")
        {
            Directory.CreateDirectory(args[2]);
            using var input = File.OpenRead(args[1]);
            var dimensions = PerspectiveCropper.ReadDimensions(input);
            input.Position = 0;
            using (var original = Image.Load<Rgba32>(input))
                Console.WriteLine($"EXIF orientation: {original.Metadata.ExifProfile?.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation)?.Value}");
            input.Position = 0;
            var cards = CardDetector.Detect(input);
            Console.WriteLine($"Source {dimensions.Width}x{dimensions.Height}; detected {cards.Count}");
            for (var i = 0; i < cards.Count; i++)
            {
                Console.WriteLine($"{i}: {cards[i].Quad.BoundingBox()}");
                input.Position = 0;
                using var output = File.Create(Path.Combine(args[2], $"card-{i:D2}.png"));
                PerspectiveCropper.CropToPng(input, cards[i].Quad, output);
            }
            return 0;
        }
        if (args.Length == 2 && args[0] == "--results")
            return CompareResults(args[1]);
        var root = args.Length > 0 ? args[0] : FindTrainingDirectory();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine("Could not find the training directory. Pass its path as an argument.");
            return 2;
        }

        static int CompareResults(string csvPath)
        {
            var listing = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(csvPath))!, "cards.txt");
            var malformed = MalformedLines(listing);
            if (malformed.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, malformed));
            var expected = File.ReadAllLines(listing).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Split(';'))
                .Select(f => (Name: NormalizeName(f[1]), Set: f[2].Trim().ToLowerInvariant(),
                    Number: NormalizeNumber(f[3]), Language: NormalizeLanguage(f[5]))).ToList();
            var actual = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l =>
                {
                    var f = l.Split(';');
                    if (f.Length != 5) throw new InvalidDataException("Expected SET;NUMBER;MODIFIER;LANGUAGE;NAME: " + l);
                    return (Name: NormalizeName(f[4]), Set: f[0].Trim().ToLowerInvariant(),
                        Number: NormalizeNumber(f[1]), Language: NormalizeLanguage(f[3]));
                }).ToList();
            int Matches(IEnumerable<string> wanted, IEnumerable<string> found)
            {
                var counts = found.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
                var matched = 0;
                foreach (var key in wanted)
                    if (counts.TryGetValue(key, out var count) && count > 0)
                    {
                        counts[key]--;
                        matched++;
                    }
                return matched;
            }
            var identity = Matches(expected.Select(c => c.Name), actual.Select(c => c.Name));
            var language = Matches(expected.Select(c => $"{c.Name}|{c.Language}"), actual.Select(c => $"{c.Name}|{c.Language}"));
            var printing = Matches(expected.Select(c => $"{c.Name}|{c.Set}|{c.Number}|{c.Language}"),
                actual.Select(c => $"{c.Name}|{c.Set}|{c.Number}|{c.Language}"));
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(csvPath))}: {actual.Count}/{expected.Count} rows; "
                + $"identity {identity}/{expected.Count}, identity+language {language}/{expected.Count}, "
                + $"printing+language {printing}/{expected.Count}. Finish ignored; order ignored; duplicate counts preserved.");
            return actual.Count == expected.Count && printing == expected.Count ? 0 : 1;
        }

        static string NormalizeLanguage(string value) =>
            value.Trim().ToLowerInvariant() is "jp" or "jpn" ? "ja" : value.Trim().ToLowerInvariant();

        static string NormalizeNumber(string value) =>
            value.Trim().TrimStart('0').ToLowerInvariant() is { Length: > 0 } number ? number : "0";

        static string NormalizeName(string value) =>
            new string(value.Split("//")[0].Trim().Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

        var failures = 0;
        var totalExpected = 0;
        var totalFound = 0;

        foreach (var directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var listing = Path.Combine(directory, "cards.txt");
            var photo = Directory.GetFiles(directory)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Equals("image", StringComparison.OrdinalIgnoreCase))
                .ThenBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();

            if (!File.Exists(listing) || photo is null) continue;

            var name = Path.GetFileName(directory);

            var malformed = MalformedLines(listing);
            if (malformed.Count > 0)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: cards.txt is not in the documented format");
                foreach (var complaint in malformed) Console.WriteLine($"       {complaint}");
                continue;
            }

            var expected = ReadExpectedCells(listing);

            using var stream = File.OpenRead(photo);
            var detected = CardDetector.Detect(stream);
            var actual = CellsOf(detected);

            // A page is often photographed sideways, so that the cards lie on their side in the
            // frame. The listing is written the way the cards read, so the two differ by a quarter
            // turn, and which turn it is cannot be told from the shapes alone: only the printing on
            // the card says which end is its top, and that is the identification agent's business.
            var sideways = detected.Count > 0 && detected.Average(card =>
            {
                var (_, _, w, h) = card.Quad.BoundingBox();
                return w - h;
            }) > 0;

            if (name is "04" or "05" or "06" or "07")
            {
                Directory.CreateDirectory(Path.Combine("artifacts", "crops", name));
                for (int i = 0; i < detected.Count; i++)
                {
                    stream.Position = 0;
                    using var cropStream = File.Create(Path.Combine("artifacts", "crops", name, $"card-{i:D2}.png"));
                    PerspectiveCropper.CropToPng(stream, detected[i].Quad, cropStream);
                }
            }

            totalExpected += expected.Count;
            totalFound += detected.Count;

            var matches = SameLayout(expected, actual)
                || (sideways && (SameLayout(expected, Turn(actual)) || SameLayout(expected, Turn(Turn(Turn(actual))))));
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

    /// <summary>
    /// Complains about any line of a <c>cards.txt</c> that is not
    /// <c>row,column;name;edition;number;foil;language</c>.
    /// <para>
    /// The answer key is read by people, not only by this check — it is what a scan's output is
    /// compared against by hand — so a line in the wrong order is worse than useless: it makes a
    /// correct reading look wrong. The edition and number columns are the pair that get swapped,
    /// because both are short and only one of them looks like a number, so they are the pair worth
    /// testing: an edition has a letter in it and a collector number has a digit.
    /// </para>
    /// <para>
    /// This is checked rather than ignored because the parsing below only ever wanted the first
    /// field, so every other mistake in the file would otherwise pass in silence.
    /// </para>
    /// </summary>
    private static List<string> MalformedLines(string path)
    {
        var complaints = new List<string>();

        foreach (var (line, index) in File.ReadAllLines(path).Select((l, i) => (l, i + 1)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = line.Split(';');
            if (fields.Length != 6)
            {
                complaints.Add($"line {index}: expected 6 fields, found {fields.Length} — {line}");
                continue;
            }

            var location = fields[0].Split(',');
            if (location.Length != 2 || !int.TryParse(location[0].Trim(), out _) || !int.TryParse(location[1].Trim(), out _))
                complaints.Add($"line {index}: '{fields[0]}' is not a row,column pocket — {line}");

            if (string.IsNullOrWhiteSpace(fields[1]))
                complaints.Add($"line {index}: the name is empty — {line}");

            if (!fields[2].Any(char.IsLetter))
                complaints.Add($"line {index}: '{fields[2]}' is not an edition code; are the edition and number swapped? — {line}");

            if (!fields[3].Any(char.IsDigit))
                complaints.Add($"line {index}: '{fields[3]}' is not a collector number; are the edition and number swapped? — {line}");
        }

        return complaints;
    }

    /// <summary>Reads the pocket of each card from a <c>cards.txt</c>: <c>a,b;name;set;...</c>.</summary>
    private static HashSet<(int Row, int Column)> ReadExpectedCells(string path)
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
    /// Works out which row and column each located card sits in, by grouping the cards that share
    /// one, rather than by rounding their centres onto a lattice.
    /// <para>
    /// A page photographed from one side has its far column higher in the frame than its near one,
    /// by a good fraction of a card, so a fixed lattice puts cards in the wrong row. What does hold
    /// is that two cards in the same row are nearer each other, down the photo, than either is to
    /// the next row.
    /// </para>
    /// </summary>
    private static HashSet<(int Row, int Column)> CellsOf(IReadOnlyList<DetectedCard> cards)
    {
        var cells = new HashSet<(int, int)>();
        if (cards.Count == 0) return cells;

        var centres = cards.Select(card =>
        {
            var (x, y, w, h) = card.Quad.BoundingBox();
            return (X: x + w / 2, Y: y + h / 2, W: w, H: h);
        }).ToList();

        var rows = Group(centres.Select(c => c.Y).ToList(), centres.Average(c => c.H) * 0.6);
        var columns = Group(centres.Select(c => c.X).ToList(), centres.Average(c => c.W) * 0.6);

        foreach (var centre in centres)
            cells.Add((IndexOf(rows, centre.Y), IndexOf(columns, centre.X)));

        return cells;
    }

    /// <summary>Splits sorted positions wherever the gap between them exceeds <paramref name="apart"/>.</summary>
    private static List<double> Group(List<double> positions, double apart)
    {
        var sorted = positions.OrderBy(v => v).ToList();
        var groups = new List<double>();
        var start = 0;
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] - sorted[i - 1] <= apart) continue;
            groups.Add(sorted.Skip(start).Take(i - start).Average());
            start = i;
        }
        return groups;
    }

    private static int IndexOf(List<double> groups, double position)
    {
        var best = 0;
        for (var i = 1; i < groups.Count; i++)
        {
            if (Math.Abs(groups[i] - position) < Math.Abs(groups[best] - position)) best = i;
        }
        return best;
    }

    /// <summary>
    /// Compares two layouts after sliding each to the origin, because the listing counts from the
    /// corner of the page and the detector counts from the first card it located.
    /// </summary>
    private static bool SameLayout(HashSet<(int Row, int Column)> left, HashSet<(int Row, int Column)> right)
    {
        if (left.Count != right.Count) return false;
        if (left.Count == 0) return true;

        return Normalise(left).SetEquals(Normalise(right));
    }

    /// <summary>Turns a layout a quarter turn clockwise.</summary>
    private static HashSet<(int Row, int Column)> Turn(HashSet<(int Row, int Column)> cells)
        => cells.Select(c => (c.Column, -c.Row)).ToHashSet();

    private static HashSet<(int Row, int Column)> Normalise(HashSet<(int Row, int Column)> cells)
    {
        var row = cells.Min(c => c.Row);
        var column = cells.Min(c => c.Column);
        return cells.Select(c => (c.Row - row, c.Column - column)).ToHashSet();
    }

    private static string Describe(HashSet<(int Row, int Column)> cells)
        => cells.Count == 0
            ? "(none)"
            : string.Join(" ", cells.OrderBy(c => c.Row).ThenBy(c => c.Column).Select(c => $"{c.Row},{c.Column}"));
}

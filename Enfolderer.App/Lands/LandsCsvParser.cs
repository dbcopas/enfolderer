using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enfolderer.App.Lands;

internal record LandsCsvResult(int[] BinderPageCounts, List<LandEntry> Entries);

internal static class LandsCsvParser
{
    /// <summary>
    /// Reads land entries from a semicolon-delimited CSV file.
    /// Line 1: comma-separated binder page counts (e.g. "20,20,20,20,20,20,20,24,20,20")
    /// Remaining lines: card data (Set;№;Name).
    /// </summary>
    public static LandsCsvResult Parse(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Lands CSV not found.", csvPath);

        var results = new List<LandEntry>();
        using var reader = new StreamReader(csvPath);

        // Line 1: binder page counts
        var sizesLine = reader.ReadLine();
        int[] binderPageCounts = Array.Empty<int>();
        if (sizesLine is not null)
        {
            binderPageCounts = sizesLine.Split(',')
                .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0)
                .Where(v => v > 0)
                .ToArray();
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(';');
            if (parts.Length < 3)
                continue;

            var set = parts[0].Trim();
            var numStr = parts[1].Trim();
            var name = parts[2].Trim();

            if (string.IsNullOrWhiteSpace(set) || string.IsNullOrWhiteSpace(numStr) || string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new LandEntry(set, numStr, name));
        }

        return new LandsCsvResult(binderPageCounts, results);
    }
}

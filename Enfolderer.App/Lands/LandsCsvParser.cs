using System;
using System.Collections.Generic;
using System.IO;

namespace Enfolderer.App.Lands;

internal static class LandsCsvParser
{
    /// <summary>
    /// Reads land entries from a semicolon-delimited CSV file.
    /// Expected header: Set;№;Name
    /// Position is determined by index in the list, not by a column value.
    /// </summary>
    public static List<LandEntry> Parse(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Lands CSV not found.", csvPath);

        var results = new List<LandEntry>();
        using var reader = new StreamReader(csvPath);

        // Skip header line
        var header = reader.ReadLine();
        if (header is null)
            return results;

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

        return results;
    }
}

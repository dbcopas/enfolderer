using System;
using System.Collections.Generic;
using System.IO;

namespace Enfolderer.App.Tokens;

internal static class TokensCsvParser
{
    /// <summary>
    /// Reads token entries from a semicolon-delimited CSV file.
    /// Each line: Set;CollectorNumber;Name
    /// </summary>
    public static List<TokenEntry> Parse(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Tokens CSV not found.", csvPath);

        var results = new List<TokenEntry>();
        using var reader = new StreamReader(csvPath);

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

            results.Add(new TokenEntry(set, numStr, name));
        }

        return results;
    }
}

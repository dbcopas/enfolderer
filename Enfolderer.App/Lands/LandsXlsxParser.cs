using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;

namespace Enfolderer.App.Lands;

internal static class LandsXlsxParser
{
    /// <summary>
    /// Reads land entries from the first sheet of the given xlsx file.
    /// Finds the header row (column A == "Set") and reads data rows below it.
    /// Any rows above the header (layout helpers, etc.) are ignored entirely.
    /// </summary>
    public static List<LandEntry> Parse(string xlsxPath)
    {
        if (!File.Exists(xlsxPath))
            throw new FileNotFoundException("Lands xlsx not found.", xlsxPath);

        var results = new List<LandEntry>();
        using var wb = new XLWorkbook(xlsxPath);
        var ws = wb.Worksheets.Worksheet(1);

        // Find the header row (first row where column A == "Set")
        int headerRow = -1;
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = 1; r <= lastRow; r++)
        {
            var val = ws.Cell(r, 1).GetString().Trim();
            if (string.Equals(val, "Set", StringComparison.OrdinalIgnoreCase))
            {
                headerRow = r;
                break;
            }
        }

        if (headerRow < 0)
            throw new InvalidOperationException("Could not find header row with 'Set' in column A.");

        // Data starts at headerRow + 1. Only columns A (Set), C (collector #), D (Name), I (Artist) are used.
        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            var set = ws.Cell(r, 1).GetString().Trim();       // Column A: Set
            if (string.IsNullOrWhiteSpace(set)) continue;

            var numStr = ws.Cell(r, 3).GetString().Trim();     // Column C: collector number
            var name = ws.Cell(r, 4).GetString().Trim();        // Column D: Name
            var artist = ws.Cell(r, 9).GetString().Trim();      // Column I: Artist

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(numStr))
                continue;

            results.Add(new LandEntry(set, numStr, name, artist));
        }

        return results;
    }
}

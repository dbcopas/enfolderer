using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ExcelDataReader;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Reads an .xlsx file (all sheets). For each row: column 0 => card name (strip bracketed text [...]), column 2 => inventory qty.
/// Aggregates quantities per normalized name only for cards that have at least one COMMON rarity printing in mainDb.
/// Outputs a semicolon-delimited CSV: Name;TotalInventory
/// </summary>
public static class ExcelCommonInventoryExporter
{
    private static readonly Regex BracketRegex = new(@"\s*\[[^\]]*\]", RegexOptions.Compiled);

    private static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Remove bracketed segments (e.g., "Lightning Bolt [MB1]" -> "Lightning Bolt")
        var noBrackets = BracketRegex.Replace(raw, string.Empty);
        return noBrackets.Trim();
    }

    public static string Export(string xlsxPath, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath)) throw new FileNotFoundException("Excel file not found", xlsxPath);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var totals = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);

        using (var stream = File.Open(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            do
            {
                while (reader.Read())
                {
                    // Expect at least 3 columns (0-based: 0=name, 2=inventory)
                    if (reader.FieldCount < 3) continue;
                    object? nameObj = reader.GetValue(0);
                    if (nameObj == null) continue;
                    string nameRaw = nameObj.ToString() ?? string.Empty;
                    string name = NormalizeName(nameRaw);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    object? qtyObj = reader.GetValue(2);
                    int qty = 0;
                    if (qtyObj != null)
                    {
                        var s = qtyObj.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) int.TryParse(s.Trim(), out qty);
                    }
                    if (qty <= 0) continue; // ignore non-positive inventory
                    if (!totals.ContainsKey(name)) totals[name] = 0;
                    totals[name] += qty;
                }
            } while (reader.NextResult());
        }

        if (totals.Count == 0) throw new InvalidOperationException("No aggregatable rows found in Excel file.");

        // Filter to names with at least one common printing in mainDb
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string mainDbPath = Path.Combine(exeDir, "mainDb.db");
        if (!File.Exists(mainDbPath)) throw new FileNotFoundException("mainDb.db not found", mainDbPath);

        var commons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
        {
            con.Open();
            // Query all commons; rarity column assumed (mirrors PlaysetNeedsExporter assumptions)
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT name FROM cards WHERE rarity = 'common' COLLATE NOCASE";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (!rdr.IsDBNull(0))
                {
                    var nm = rdr.GetString(0);
                    if (!string.IsNullOrWhiteSpace(nm)) commons.Add(nm.Trim());
                }
            }
        }

        var filtered = totals.Where(kv => commons.Contains(kv.Key))
                              .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                              .ToList();

        if (filtered.Count == 0) throw new InvalidOperationException("No names with common printings were found.");

        string outPath = outputPath ?? Path.Combine(Path.GetDirectoryName(xlsxPath) ?? exeDir, Path.GetFileNameWithoutExtension(xlsxPath) + "_commons.csv");
        using (var sw = new StreamWriter(outPath, false, Encoding.UTF8))
        {
            sw.WriteLine("Name;TotalInventory");
            foreach (var kv in filtered)
            {
                // Escape semicolons by wrapping in quotes if necessary
                string nameEsc = kv.Key.Contains(';') ? '"' + kv.Key.Replace("\"","'") + '"' : kv.Key;
                sw.WriteLine($"{nameEsc};{kv.Value}");
            }
        }
        return outPath;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Importing;

public record CsvUpdateResult(int Updated, int Inserted, int Errors, string DatabasePath);

/// <summary>
/// Parses a semicolon-delimited CSV file and updates (or inserts) rows in mainDb.db
/// located alongside the CSV (or an explicitly supplied db path). The updater:
///  - Treats only CRLF (\r\n) as line terminators preserving lone LF inside fields.
///  - Dynamically detects optional columns (modifier, version).
///  - Supports multiple collector number formats and normalizes leading zeros.
///  - Skips malformed / underspecified lines silently (except counting as errors when critical fields missing).
/// </summary>
public static class CsvMainDbUpdater
{
    public static CsvUpdateResult Process(string csvPath, string? explicitDbPath = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath required");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found", csvPath);
        string dbPath = explicitDbPath ?? Path.Combine(Path.GetDirectoryName(csvPath) ?? string.Empty, "mainDb.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found in CSV folder", dbPath);

        // Custom read honoring only CRLF as line terminator.
        var rawText = File.ReadAllText(csvPath);
        var lines = new List<string>();
        var sbLine = new StringBuilder();
        for (int i = 0; i < rawText.Length; i++)
        {
            char c = rawText[i];
            if (c == '\r')
            {
                if (i + 1 < rawText.Length && rawText[i + 1] == '\n')
                {
                    lines.Add(sbLine.ToString());
                    sbLine.Clear();
                    i++; // skip \n
                    continue;
                }
                sbLine.Append(c); // solitary CR kept
            }
            else sbLine.Append(c);
        }
        if (sbLine.Length > 0) lines.Add(sbLine.ToString());

        int updated = 0, inserted = 0, errors = 0;

        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();

        bool hasModifier = false, hasVersion = false;
        try
        {
            using var pragma = con.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(Cards)";
            using var r = pragma.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var colName = r.GetString(1);
                    if (string.Equals(colName, "modifier", StringComparison.OrdinalIgnoreCase)) hasModifier = true;
                    else if (string.Equals(colName, "version", StringComparison.OrdinalIgnoreCase)) hasVersion = true;
                }
                catch { }
            }
        }
        catch { }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(';');
            if (cols.Length < 6) continue; // silently skip insufficient columns per prior behavior

            string nameRaw = cols[0].Trim();
            string baseName = nameRaw;
            string? modifier = null; int? versionNumber = null;
            int lb = nameRaw.IndexOf('['); int rb = lb >= 0 ? nameRaw.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb)
            {
                modifier = nameRaw.Substring(lb + 1, rb - lb - 1).Trim();
                baseName = nameRaw.Substring(0, lb).Trim();
            }
            var verMatch = Regex.Match(baseName, "\\((\\d+)\\)");
            if (verMatch.Success)
            {
                if (int.TryParse(verMatch.Groups[1].Value, out var vParsed)) versionNumber = vParsed;
                baseName = Regex.Replace(baseName, "\\(\\d+\\)", "").Trim();
                baseName = Regex.Replace(baseName, "\\s{2,}", " ");
            }
            string name = baseName;
            string id = cols[3].Trim();
            string cardNumRarity = cols[4].Trim();
            string edition = cols[5].Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(cardNumRarity) || string.IsNullOrEmpty(edition)) { errors++; continue; }

            string cardNum = ParseCollectorNumber(cardNumRarity);
            if (string.IsNullOrEmpty(cardNum) || !Regex.IsMatch(cardNum, "^\\d+$")) { errors++; continue; }

            using var updateCmd = con.CreateCommand();
            var updateSb = new StringBuilder("UPDATE Cards SET id=@id");
            if (hasModifier) updateSb.Append(", modifier=@modifier");
            if (hasVersion && versionNumber.HasValue) updateSb.Append(", version=@version");
            updateSb.Append(" WHERE edition=@edition AND collectorNumberValue=@number");
            updateCmd.CommandText = updateSb.ToString();
            updateCmd.Parameters.AddWithValue("@id", id);
            updateCmd.Parameters.AddWithValue("@edition", edition);
            updateCmd.Parameters.AddWithValue("@number", cardNum);
            if (hasModifier) updateCmd.Parameters.AddWithValue("@modifier", (object?)modifier ?? DBNull.Value);
            if (hasVersion && versionNumber.HasValue) updateCmd.Parameters.AddWithValue("@version", versionNumber.Value);
            int rows = 0;
            try { rows = updateCmd.ExecuteNonQuery(); } catch { }
            if (rows > 0) { updated++; continue; }

            using var insertCmd = con.CreateCommand();
            if (hasModifier && hasVersion && versionNumber.HasValue)
                insertCmd.CommandText = "INSERT INTO Cards (id, edition, collectorNumberValue, name, modifier, version) VALUES (@id,@edition,@number,@name,@modifier,@version)";
            else if (hasModifier && !(hasVersion && versionNumber.HasValue))
                insertCmd.CommandText = "INSERT INTO Cards (id, edition, collectorNumberValue, name, modifier) VALUES (@id,@edition,@number,@name,@modifier)";
            else if (!hasModifier && hasVersion && versionNumber.HasValue)
                insertCmd.CommandText = "INSERT INTO Cards (id, edition, collectorNumberValue, name, version) VALUES (@id,@edition,@number,@name,@version)";
            else
                insertCmd.CommandText = "INSERT INTO Cards (id, edition, collectorNumberValue, name) VALUES (@id,@edition,@number,@name)";
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@edition", edition);
            insertCmd.Parameters.AddWithValue("@number", cardNum);
            insertCmd.Parameters.AddWithValue("@name", name);
            if (hasModifier) insertCmd.Parameters.AddWithValue("@modifier", (object?)modifier ?? DBNull.Value);
            if (hasVersion && versionNumber.HasValue) insertCmd.Parameters.AddWithValue("@version", versionNumber.Value);
            try { insertCmd.ExecuteNonQuery(); inserted++; } catch { errors++; }
        }
        return new CsvUpdateResult(updated, inserted, errors, dbPath);
    }

    private static string ParseCollectorNumber(string raw)
    {
        raw = raw.Trim();
        string cardNum;
        if (raw.Contains('/'))
        {
            var parts = raw.Split('/', 2);
            var left = parts[0].Trim();
            var match = Regex.Match(left, "^\\d+");
            cardNum = match.Success ? match.Value : left;
        }
        else
        {
            var digitSeqs = Regex.Matches(raw, "\\d+");
            if (digitSeqs.Count == 1)
                cardNum = digitSeqs[0].Value;
            else
            {
                var letterNumMatch = Regex.Match(raw, @"^[A-Za-z]+\s+0*(\d+)$");
                if (letterNumMatch.Success)
                    cardNum = letterNumMatch.Groups[1].Value;
                else
                {
                    var match = Regex.Match(raw, "^\\d+");
                    if (match.Success)
                        cardNum = match.Value;
                    else if (Regex.IsMatch(raw, @"^\\d+\s+[A-Za-z]$"))
                        cardNum = raw[..raw.LastIndexOf(' ')].Trim();
                    else if (Regex.IsMatch(raw, @"^\\d+[A-Za-z]$"))
                        cardNum = raw[..^1];
                    else
                        cardNum = raw;
                }
            }
        }
        if (Regex.IsMatch(cardNum, "^0+\\d+$"))
        {
            cardNum = cardNum.TrimStart('0');
            if (cardNum.Length == 0) cardNum = "0";
        }
        return cardNum;
    }
}

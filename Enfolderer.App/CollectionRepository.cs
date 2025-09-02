using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App;

public sealed class CollectionRepository
{
    private readonly CardCollectionData _data;
    public CollectionRepository(CardCollectionData data) => _data = data;

    public void EnsureLoaded(string? folder)
    {
        try
        {
            if (!_data.IsLoaded && !string.IsNullOrEmpty(folder)) _data.Load(folder);
        }
        catch (Exception ex)
        { System.Diagnostics.Debug.WriteLine($"[CollectionRepo] Ensure load failed: {ex.Message}"); }
    }

    public int? ResolveCardId(string? folder, string setOriginal, string baseNum, string trimmed)
    {
        try
        {
            if (string.IsNullOrEmpty(folder)) return null;
            string mainDb = Path.Combine(folder, "mainDb.db");
            if (!File.Exists(mainDb)) return null;
            using var con = new SqliteConnection($"Data Source={mainDb};Mode=ReadOnly");
            con.Open();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = con.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Cards)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) { try { cols.Add(r.GetString(1)); } catch { } }
            }
            string? idCol = cols.Contains("id") ? "id" : (cols.Contains("cardId") ? "cardId" : null);
            string? editionCol = cols.Contains("edition") ? "edition" : (cols.Contains("set") ? "set" : null);
            string? numberValueCol = cols.Contains("collectorNumberValue") ? "collectorNumberValue" : (cols.Contains("numberValue") ? "numberValue" : null);
            if (idCol == null || editionCol == null || numberValueCol == null) return null;
            int parenIndex = baseNum.IndexOf('(');
            if (parenIndex > 0) baseNum = baseNum.Substring(0, parenIndex);
            var candidates = new List<string>();
            void AddCand(string c) { if (!string.IsNullOrWhiteSpace(c) && !candidates.Contains(c, StringComparer.OrdinalIgnoreCase)) candidates.Add(c); }
            AddCand(baseNum);
            if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal)) AddCand(trimmed);
            if (baseNum.IndexOf('★') >= 0)
            {
                var stripped = baseNum.Replace("★", string.Empty);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    AddCand(stripped);
                    var strippedTrim = stripped.TrimStart('0'); if (strippedTrim.Length == 0) strippedTrim = "0";
                    if (!string.Equals(strippedTrim, stripped, StringComparison.Ordinal)) AddCand(strippedTrim);
                }
            }
            string prog = baseNum;
            while (prog.Length > 0 && !char.IsDigit(prog[^1])) { prog = prog[..^1]; if (prog.Length == 0) break; AddCand(prog); }
            List<string> baseForPad = new();
            if (int.TryParse(baseNum, out _)) baseForPad.Add(baseNum);
            if (int.TryParse(trimmed, out _)) baseForPad.Add(trimmed);
            foreach (var b in baseForPad.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (b.Length < 3)
                {
                    if (b.Length == 1) { AddCand("0" + b); AddCand("00" + b); }
                    else if (b.Length == 2) { AddCand("0" + b); }
                }
            }
            var editionCandidates = new List<string>();
            if (!string.IsNullOrEmpty(setOriginal)) editionCandidates.Add(setOriginal);
            var upper = setOriginal?.ToUpperInvariant(); var lower = setOriginal?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(upper) && !editionCandidates.Contains(upper)) editionCandidates.Add(upper);
            if (!string.IsNullOrEmpty(lower) && !editionCandidates.Contains(lower)) editionCandidates.Add(lower);
            foreach (var editionCandidate in editionCandidates)
                foreach (var cand in candidates)
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = $"SELECT {idCol} FROM Cards WHERE {editionCol}=@set COLLATE NOCASE AND {numberValueCol}=@num LIMIT 1";
                    cmd.Parameters.AddWithValue("@set", editionCandidate);
                    cmd.Parameters.AddWithValue("@num", cand);
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value && int.TryParse(val.ToString(), out int idVal)) return idVal;
                }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CollectionRepo] Direct cardId resolve failed: {ex.Message}"); }
        return null;
    }
}

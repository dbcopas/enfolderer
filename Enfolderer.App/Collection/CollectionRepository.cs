using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Collection;

public sealed class CollectionRepository : ICollectionRepository, IQuantityRepository
{
    private readonly CardCollectionData _data;
    private readonly Enfolderer.App.Core.Abstractions.ILogSink? _log;
    public CollectionRepository(CardCollectionData data, Enfolderer.App.Core.Abstractions.ILogSink? log = null) { _data = data; _log = log; }

    public void EnsureLoaded(string? folder)
    {
        try
        {
        // Ignore provided folder; always use exe directory
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_data.IsLoaded) _data.Load(exeDir);
        }
        catch (Exception ex)
    { _log?.Log($"Ensure load failed: {ex.Message}", "CollectionRepo"); }
    }

    public int? ResolveCardId(string? folder, string setOriginal, string baseNum, string trimmed)
    {
        try
        {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string mainDb = Path.Combine(exeDir, "mainDb.db");
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
    catch (Exception ex) { _log?.Log($"Direct cardId resolve failed: {ex.Message}", "CollectionRepo"); }
        return null;
    }

    public int? UpdateCustomCardQuantity(string mainDbPath, int cardId, int newQty, bool qtyDebug)
    {
        try
        {
            if (!File.Exists(mainDbPath)) return null;
            using var conMain = new SqliteConnection($"Data Source={mainDbPath}");
            conMain.Open();
            using (var cmd = conMain.CreateCommand())
            {
                cmd.CommandText = "UPDATE Cards SET Qty=@q WHERE id=@id";
                cmd.Parameters.AddWithValue("@q", newQty);
                cmd.Parameters.AddWithValue("@id", cardId);
                cmd.ExecuteNonQuery();
            }
            using (var verify = conMain.CreateCommand())
            {
                verify.CommandText = "SELECT Qty FROM Cards WHERE id=@id";
                verify.Parameters.AddWithValue("@id", cardId);
                var obj = verify.ExecuteScalar();
                if (obj != null && obj != DBNull.Value) return Convert.ToInt32(obj);
            }
        }
        catch (Exception ex)
    { _log?.Log($"Custom write failed: {ex.Message}", "QuantityRepo.Custom"); }
        return null;
    }

    public int? UpsertStandardCardQuantity(string collectionFilePath, int cardId, int newQty, bool qtyDebug)
    {
        try
        {
            if (!File.Exists(collectionFilePath)) return null;
            using var con = new SqliteConnection($"Data Source={collectionFilePath}");
            con.Open();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                cmd.Parameters.AddWithValue("@q", newQty);
                cmd.Parameters.AddWithValue("@id", cardId);
                int rows = cmd.ExecuteNonQuery();
                if (qtyDebug) _log?.Log($"UPDATE rows={rows} cardId={cardId} qty={newQty}", "QuantityRepo.Std");
                if (rows == 0)
                {
                    using var ins = con.CreateCommand();
                    ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                    ins.Parameters.AddWithValue("@id", cardId);
                    ins.Parameters.AddWithValue("@q", newQty);
                    var added = DateTime.Now.ToString("s").Replace('T',' ');
                    ins.Parameters.AddWithValue("@added", added);
                    try
                    {
                        int insRows = ins.ExecuteNonQuery();
                        if (qtyDebug) _log?.Log($"INSERT rows={insRows} cardId={cardId} qty={newQty}", "QuantityRepo.Std");
                    }
                    catch (Exception exIns)
                    { _log?.Log($"Insert exception CardId {cardId}: {exIns.Message}", "QuantityRepo.Std"); }
                }
                using var verify = con.CreateCommand();
                verify.CommandText = "SELECT Qty FROM CollectionCards WHERE CardId=@id";
                verify.Parameters.AddWithValue("@id", cardId);
                var obj = verify.ExecuteScalar();
                if (obj != null && obj != DBNull.Value) return Convert.ToInt32(obj);
            }
        }
        catch (Exception ex)
    { _log?.Log($"Std write failed: {ex.Message}", "QuantityRepo.Std"); }
        return null;
    }
}

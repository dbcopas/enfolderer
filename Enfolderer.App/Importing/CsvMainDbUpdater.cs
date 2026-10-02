using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Importing;

public record CsvUpdateResult(int Updated, int Inserted, int Errors, string DatabasePath);
public record CsvMtgsMapResult(int UpdatedMtgsIds, int InsertedNew, int SkippedExisting, int Conflicts, int Errors, string DatabasePath, string? UnmatchedLogPath);

public enum StudioMatchKind { Update, Conflict, SkippedAlreadyMapped, SkippedDuplicate, Unmatched, Error }

public record StudioCsvPlanEntry(
    StudioMatchKind Kind,
    int MtgsId,
    int? FoundId,
    int? OrigQty,
    string Edition,
    string Name,
    string? CardNum,
    string RawLine,
    string? ExistingMtgsVal = null
);

public class StudioCsvPlan
{
    public List<StudioCsvPlanEntry> Entries { get; } = new();
    public string DbPath { get; init; } = "";
    public string CsvPath { get; init; } = "";
    public string IdCol { get; init; } = "id";
    public string MtgsCol { get; init; } = "MtgsId";
    public bool HasQtyCol { get; init; }
}

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
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= max) return text;
        return text.Substring(0, Math.Max(0, max - 3)) + "...";
    }
    public static CsvMtgsMapResult ProcessMtgsMapping(string csvPath, string? explicitDbPath = null, bool dryRun = true, bool insertMissing = false, Action<int,int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath required");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found", csvPath);
    // Force mainDb to reside ONLY beside the executable (ignore any explicit path or CSV folder fallback)
    string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string dbPath = Path.Combine(exeDir, "mainDb.db");
    if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found in executable directory", dbPath);

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
                    i++;
                    continue;
                }
                sbLine.Append(c);
            }
            else sbLine.Append(c);
        }
        if (sbLine.Length > 0) lines.Add(sbLine.ToString());

    int updatedMtgs = 0, inserted = 0, skippedExisting = 0, conflicts = 0, errors = 0;
    string? unmatchedLogPath = null;
    // categorized logs
    var errorLines = new List<string>();
    var conflictLines = new List<string>();
    var skippedLines = new List<string>();
    var updateLines = new List<string>();
    var unmatchedInsertLines = new List<string>();
    var debugLines = new List<string>(); // detailed per-row debug diagnostics

        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();

        // Detect column names
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Cards)";
            using var r = pragma.ExecuteReader();
            while (r.Read()) { try { cols.Add(r.GetString(1)); } catch (System.Exception) { throw; } }
        }
        string idCol = cols.Contains("id") ? "id" : (cols.Contains("cardId") ? "cardId" : "id");
        string editionCol = cols.Contains("edition") ? "edition" : (cols.Contains("set") ? "set" : "edition");
        string numberCol = cols.Contains("collectorNumberValue") ? "collectorNumberValue" : (cols.Contains("numberValue") ? "numberValue" : "collectorNumberValue");
        string nameCol = cols.Contains("name") ? "name" : "name";
    string mtgsCol = cols.Contains("MtgsId") ? "MtgsId" : (cols.Contains("mtgsid") ? "mtgsid" : (cols.Contains("MTGSID") ? "MTGSID" : ""));
    if (string.IsNullOrEmpty(mtgsCol)) throw new InvalidOperationException("Cards table has no MtgsId column.");
    bool hasModifierCol = cols.Contains("modifier");
    bool hasVersionCol = cols.Contains("version");
    bool hasQtyCol = cols.Contains("Qty");
    int qtyMoveSuccess = 0, qtyMoveFail = 0, qtyMoveSkippedNoQty = 0;
    int idRekeySuccess = 0, idRekeyFail = 0; // rekey Cards.id -> MtgsId
    int qtyMoveAttempted = 0, preExistingMtgs = 0;

        int total = 0, processed = 0;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++) if (!string.IsNullOrWhiteSpace(lines[i])) total++;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            processed++;
            var csv = SplitSemicolonCsv(line);
            if (csv.Length < 6) continue; // underspecified
            string nameRaw = csv[0].Trim();
            string? csvModifier = null;
            int? csvVersion = null;
            // Extract modifier if present
            int lb = nameRaw.IndexOf('['); int rb = lb >= 0 ? nameRaw.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb)
            {
                csvModifier = nameRaw.Substring(lb + 1, rb - lb - 1).Trim();
            }
            // Extract version from anywhere in the name (commonly at the end)
            var verMatch = Regex.Match(nameRaw, "\\((\\d+)\\)");
            if (verMatch.Success && int.TryParse(verMatch.Groups[1].Value, out var vParsed)) csvVersion = vParsed;
            // Build base name by removing modifier [..] and version (n)
            string name = nameRaw;
            name = Regex.Replace(name, "\\s*\\[[^\\]]*\\]\\s*", " ");
            name = Regex.Replace(name, "\\(\\d+\\)", "");
            name = Regex.Replace(name, "\\s{2,}", " ").Trim();
            string mtgsIdStr = csv[3].Trim();
            string rawNum = csv[4].Trim();
            string edition = csv[5].Trim();
            if (!int.TryParse(Regex.Match(mtgsIdStr, "^\\d+").Value, out var mtgsId)) { errors++; errorLines.Add(line + " | ERROR invalid MtgsId"); continue; }
            string cardNumParsed = ParseCollectorNumber(rawNum);
            string? cardNum = string.IsNullOrWhiteSpace(cardNumParsed) ? null : cardNumParsed;

            // Deduplicate exact key across CSV (edition+name+modifier+version+number)
            string key = string.Join("|", edition.ToLowerInvariant(), name.ToLowerInvariant(), (csvModifier ?? "").ToLowerInvariant(), (csvVersion?.ToString() ?? ""), (cardNum ?? ""));
            if (!seenKeys.Add(key)) { skippedExisting++; skippedLines.Add(line + " | SKIPPED duplicate CSV row"); goto ProgressOnly; }

            // Skip if another row already has this MtgsId
            using (var chk = con.CreateCommand())
            {
                chk.CommandText = $"SELECT 1 FROM Cards WHERE {mtgsCol}=@m LIMIT 1";
                chk.Parameters.AddWithValue("@m", mtgsId);
                var exists = chk.ExecuteScalar();
                if (exists != null && exists != DBNull.Value) { skippedExisting++; skippedLines.Add(line + " | SKIPPED MtgsId already mapped in another row"); continue; }
            }

            // Find by exact name/edition/number
            int? foundId = null; object? foundMtgs = null; int? foundVersion = null; string? foundModifier = null;
            using (var find = con.CreateCommand())
            {
                var sb = new StringBuilder();
                sb.Append($"SELECT {idCol}, {mtgsCol}");
                if (hasVersionCol) sb.Append(", version");
                if (hasModifierCol) sb.Append(", modifier");
                sb.Append($" FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                if (cardNum == null)
                    sb.Append($" AND ( {numberCol} IS NULL OR TRIM({numberCol})='' )");
                else
                    sb.Append($" AND {numberCol}=@n");
                sb.Append($" AND {nameCol}=@nm COLLATE NOCASE");
                if (hasVersionCol) sb.Append(" AND ((@ver IS NULL AND version IS NULL) OR version=@ver)");
                sb.Append(" LIMIT 1");
                find.CommandText = sb.ToString();
                if (hasVersionCol) find.Parameters.AddWithValue("@ver", (object?)csvVersion ?? DBNull.Value);
                find.Parameters.AddWithValue("@e", edition);
                if (cardNum != null) find.Parameters.AddWithValue("@n", cardNum);
                find.Parameters.AddWithValue("@nm", name);
                using var rr = find.ExecuteReader();
                if (rr.Read())
                {
                    try
                    {
                        foundId = rr.IsDBNull(0) ? null : rr.GetInt32(0);
                        foundMtgs = rr.IsDBNull(1) ? null : rr.GetValue(1);
                        if (hasVersionCol)
                        {
                            try { int idx = rr.GetOrdinal("version"); if (idx >= 0) foundVersion = rr.IsDBNull(idx) ? null : rr.GetInt32(idx); } catch (System.Exception) { throw; }
                        }
                        if (hasModifierCol)
                        {
                            try { int idxm = rr.GetOrdinal("modifier"); if (idxm >= 0) foundModifier = rr.IsDBNull(idxm) ? null : rr.GetString(idxm); } catch (System.Exception) { throw; }
                        }
                    }
                    catch (System.Exception) { throw; }
                }
            }

            // Fallback: if not found and name looks like a single-slash separated pair, try spaced double-slash form used in DB (e.g. "A/B" -> "A // B")
            if (!foundId.HasValue && name.Contains('/') && !name.Contains(" // "))
            {
                string alt = System.Text.RegularExpressions.Regex.Replace(name, "\\s*/\\s*", " // ").Trim();
                if (!string.Equals(alt, name, StringComparison.OrdinalIgnoreCase))
                {
                    // First attempt: keep collector number constraint (if any)
                    using (var findAlt = con.CreateCommand())
                    {
                        var sb2 = new StringBuilder();
                        sb2.Append($"SELECT {idCol}, {mtgsCol}");
                        if (hasVersionCol) sb2.Append(", version");
                        if (hasModifierCol) sb2.Append(", modifier");
                        sb2.Append($" FROM Cards WHERE {editionCol}=@eA COLLATE NOCASE");
                        if (cardNum == null) sb2.Append($" AND ( {numberCol} IS NULL OR TRIM({numberCol})='' )"); else sb2.Append($" AND {numberCol}=@nA");
                        sb2.Append($" AND {nameCol}=@nmA COLLATE NOCASE");
                        if (hasVersionCol) sb2.Append(" AND ((@verA IS NULL AND version IS NULL) OR version=@verA)");
                        sb2.Append(" LIMIT 1");
                        findAlt.CommandText = sb2.ToString();
                        if (hasVersionCol) findAlt.Parameters.AddWithValue("@verA", (object?)csvVersion ?? DBNull.Value);
                        findAlt.Parameters.AddWithValue("@eA", edition);
                        if (cardNum != null) findAlt.Parameters.AddWithValue("@nA", cardNum);
                        findAlt.Parameters.AddWithValue("@nmA", alt);
                        using var ra = findAlt.ExecuteReader();
                        if (ra.Read())
                        {
                            try
                            {
                                foundId = ra.IsDBNull(0) ? null : ra.GetInt32(0);
                                foundMtgs = ra.IsDBNull(1) ? null : ra.GetValue(1);
                                if (hasVersionCol)
                                {
                                    try { int idx = ra.GetOrdinal("version"); if (idx >= 0) foundVersion = ra.IsDBNull(idx) ? null : ra.GetInt32(idx); } catch (System.Exception) { throw; }
                                }
                                if (hasModifierCol)
                                {
                                    try { int idxm = ra.GetOrdinal("modifier"); if (idxm >= 0) foundModifier = ra.IsDBNull(idxm) ? null : ra.GetString(idxm); } catch (System.Exception) { throw; }
                                }
                            }
                            catch (System.Exception) { throw; }
                        }
                    }
                    // Second attempt: if still not found (possibly collector number mismatch), try ignoring collector number but only accept if unique
                    if (!foundId.HasValue)
                    {
                        using var countCmd = con.CreateCommand();
                        var sbc = new StringBuilder();
                        sbc.Append($"SELECT {idCol}, {mtgsCol}");
                        if (hasVersionCol) sbc.Append(", version");
                        if (hasModifierCol) sbc.Append(", modifier");
                        sbc.Append($" FROM Cards WHERE {editionCol}=@eB COLLATE NOCASE AND {nameCol}=@nmB COLLATE NOCASE");
                        if (hasVersionCol) sbc.Append(" AND ((@verB IS NULL AND version IS NULL) OR version=@verB)");
                        countCmd.CommandText = sbc.ToString();
                        if (hasVersionCol) countCmd.Parameters.AddWithValue("@verB", (object?)csvVersion ?? DBNull.Value);
                        countCmd.Parameters.AddWithValue("@eB", edition);
                        countCmd.Parameters.AddWithValue("@nmB", alt);
                        var matches = new List<(int id, object? mtgs, int? ver, string? mod)>();
                        using (var rb2 = countCmd.ExecuteReader())
                        {
                            while (rb2.Read())
                            {
                                try
                                {
                                    int idv = rb2.IsDBNull(0) ? -1 : rb2.GetInt32(0);
                                    object? mtgsv = rb2.IsDBNull(1) ? null : rb2.GetValue(1);
                                    int? verv = null; string? modv = null;
                                    if (hasVersionCol) { try { int idx = rb2.GetOrdinal("version"); if (idx >= 0) verv = rb2.IsDBNull(idx) ? null : rb2.GetInt32(idx); } catch (System.Exception) { throw; } }
                                    if (hasModifierCol) { try { int idxm = rb2.GetOrdinal("modifier"); if (idxm >= 0) modv = rb2.IsDBNull(idxm) ? null : rb2.GetString(idxm); } catch (System.Exception) { throw; } }
                                    if (idv >= 0) matches.Add((idv, mtgsv, verv, modv));
                                }
                                catch (System.Exception) { throw; }
                            }
                        }
                        if (matches.Count == 1)
                        {
                            var m = matches[0];
                            foundId = m.id; foundMtgs = m.mtgs; foundVersion = m.ver; foundModifier = m.mod;
                        }
                        // If multiple, keep not found to avoid ambiguous incorrect mapping.
                    }
                }
            }

            if (foundId.HasValue)
            {
                // If MtgsId empty/null/0 -> update; else conflict unless equal
                string? existingVal = foundMtgs?.ToString();
                bool isEmpty = string.IsNullOrWhiteSpace(existingVal) || existingVal == "0";
                // If CSV provided a modifier and DB modifier differs or is null, update modifier on match (not considered a new entry)
                bool modifierNeedsUpdate = hasModifierCol && !string.IsNullOrWhiteSpace(csvModifier) && !string.Equals(foundModifier ?? string.Empty, csvModifier ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                // Capture original Qty for diagnostics (even if we won't move it)
                int? origQty = null;
                if (hasQtyCol)
                {
                    try
                    {
                        using var qcmd = con.CreateCommand();
                        qcmd.CommandText = $"SELECT Qty FROM Cards WHERE {idCol}=@id";
                        qcmd.Parameters.AddWithValue("@id", foundId.Value);
                        var qobj = qcmd.ExecuteScalar();
                        if (qobj != null && qobj != DBNull.Value && int.TryParse(qobj.ToString(), out var oq)) origQty = oq;
                    }
                    catch (System.Exception) { throw; }
                }
                if (isEmpty)
                {
                    if (!dryRun)
                    {
                        using var upd = con.CreateCommand();
                        if (modifierNeedsUpdate)
                            upd.CommandText = $"UPDATE Cards SET {mtgsCol}=@m, modifier=@mod WHERE {idCol}=@id";
                        else
                            upd.CommandText = $"UPDATE Cards SET {mtgsCol}=@m WHERE {idCol}=@id";
                        upd.Parameters.AddWithValue("@m", mtgsId);
                        upd.Parameters.AddWithValue("@id", foundId.Value);
                        if (modifierNeedsUpdate) upd.Parameters.AddWithValue("@mod", csvModifier);
                        try
                        {
                            upd.ExecuteNonQuery();
                            updatedMtgs++;
                            string qtyMoveNote = string.Empty;

                            // Pre-check whether Cards.id rekey will succeed (needed to decide CollectionCards CardId)
                            bool canRekeyEarly = false;
                            if (foundId.HasValue && foundId.Value != mtgsId)
                            {
                                try
                                {
                                    using var chkEarly = con.CreateCommand();
                                    chkEarly.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@nid LIMIT 1";
                                    chkEarly.Parameters.AddWithValue("@nid", mtgsId);
                                    var exEarly = chkEarly.ExecuteScalar();
                                    canRekeyEarly = (exEarly == null || exEarly == DBNull.Value);
                                }
                                catch { canRekeyEarly = false; }
                            }
                            // The CardId to use in CollectionCards: MtgsId only if rekey will succeed, otherwise keep internal id
                            int collectionCardId = canRekeyEarly ? mtgsId : foundId.Value;

                            // If the mainDb row has a Qty, move it to mtgstudio.collection and clear mainDb Qty (only after confirmed write)
                            if (hasQtyCol)
                            {
                                int? qtyVal = null;
                                try
                                {
                                    using var getQty = con.CreateCommand();
                                    getQty.CommandText = $"SELECT Qty FROM Cards WHERE {idCol}=@id";
                                    getQty.Parameters.AddWithValue("@id", foundId.Value);
                                    var obj = getQty.ExecuteScalar();
                                    if (obj != null && obj != DBNull.Value)
                                    {
                                        if (int.TryParse(obj.ToString(), out var q)) qtyVal = q;
                                    }
                                }
                                catch (System.Exception) { throw; }
                                if (qtyVal.HasValue)
                                {
                                    qtyMoveAttempted++;
                                    string collectionPath = Path.Combine(exeDir, "mtgstudio.collection");
                                    if (File.Exists(collectionPath))
                                    {
                                        bool success = false; string? failReason = null;
                                        int? collectionQtyAfter = null; int? mainDbQtyAfter = null; string status = "unknown"; string reason = string.Empty;
                                        try
                                        {
                                            using var conCol = new SqliteConnection($"Data Source={collectionPath}");
                                            conCol.Open();
                                            int affected = 0;
                                            // Rekey legacy CollectionCards rows only if Cards.id rekey will succeed
                                            if (canRekeyEarly)
                                            {
                                                try
                                                {
                                                    using var fix = conCol.CreateCommand();
                                                    fix.CommandText = @"UPDATE CollectionCards SET CardId=@mtgs WHERE CardId=@internal AND NOT EXISTS (SELECT 1 FROM CollectionCards WHERE CardId=@mtgs)";
                                                    fix.Parameters.AddWithValue("@mtgs", mtgsId);
                                                    fix.Parameters.AddWithValue("@internal", foundId.Value);
                                                    fix.ExecuteNonQuery();
                                                }
                                                catch (System.Exception) { throw; }
                                            }
                                            using (var u = conCol.CreateCommand())
                                            {
                                                u.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                                                u.Parameters.AddWithValue("@q", qtyVal.Value);
                                                u.Parameters.AddWithValue("@id", collectionCardId);
                                                try { affected = u.ExecuteNonQuery(); } catch (System.Exception) { throw; }
                                            }
                                            if (affected == 0)
                                            {
                                                using var ins = conCol.CreateCommand();
                                                ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                                                ins.Parameters.AddWithValue("@id", collectionCardId);
                                                ins.Parameters.AddWithValue("@q", qtyVal.Value);
                                                var added = DateTime.Now.ToString("s").Replace('T', ' ');
                                                ins.Parameters.AddWithValue("@added", added);
                                                ins.ExecuteNonQuery();
                                            }
                                            success = true;
                                        }
                                        catch (Exception ex) { success = false; failReason = ex.Message; }
                                        if (success)
                                        {
                                            status = "write-ok";
                                            try
                                            {
                                                using var clr = con.CreateCommand();
                                                clr.CommandText = $"UPDATE Cards SET Qty=NULL WHERE {idCol}=@id";
                                                clr.Parameters.AddWithValue("@id", foundId.Value);
                                                clr.ExecuteNonQuery();
                                                // initial verify before detailed post-state capture
                                                try
                                                {
                                                    using var verify = new SqliteConnection($"Data Source={collectionPath};Mode=ReadOnly");
                                                    verify.Open();
                                                    using var vc = verify.CreateCommand();
                                                    vc.CommandText = "SELECT Qty FROM CollectionCards WHERE CardId=@id";
                                                    vc.Parameters.AddWithValue("@id", collectionCardId);
                                                    var vObj = vc.ExecuteScalar();
                                                    if (vObj == null || vObj == DBNull.Value || !int.TryParse(vObj.ToString(), out var vq) || vq != qtyVal.Value)
                                                    {
                                                        qtyMoveNote += "; verify mismatch";
                                                        qtyMoveFail++;
                                                    }
                                                    else qtyMoveSuccess++;
                                                }
                                                catch { qtyMoveFail++; qtyMoveNote += "; verify failed"; }
                                                // capture post states
                                                try
                                                {
                                                    using var colVerify = new SqliteConnection($"Data Source={collectionPath};Mode=ReadOnly");
                                                    colVerify.Open();
                                                    using var vc2 = colVerify.CreateCommand();
                                                    vc2.CommandText = "SELECT Qty FROM CollectionCards WHERE CardId=@id";
                                                    vc2.Parameters.AddWithValue("@id", collectionCardId);
                                                    var cv = vc2.ExecuteScalar();
                                                    if (cv != null && cv != DBNull.Value && int.TryParse(cv.ToString(), out var cq)) collectionQtyAfter = cq;
                                                }
                                                catch (System.Exception) { throw; }
                                                try
                                                {
                                                    using var qmain = con.CreateCommand();
                                                    qmain.CommandText = $"SELECT Qty FROM Cards WHERE {idCol}=@id";
                                                    qmain.Parameters.AddWithValue("@id", foundId.Value);
                                                    var mv = qmain.ExecuteScalar();
                                                    if (mv != null && mv != DBNull.Value && int.TryParse(mv.ToString(), out var mq)) mainDbQtyAfter = mq; else mainDbQtyAfter = null;
                                                }
                                                catch (System.Exception) { throw; }
                                                status = "moved";
                                            }
                                            catch (Exception exClear)
                                            {
                                                qtyMoveNote = $"; collection write ok but clear failed: {Truncate(exClear.Message,60)}";
                                                qtyMoveFail++;
                                                status = "clear-fail"; reason = Truncate(exClear.Message,60);
                                            }
                                        }
                                        else
                                        {
                                            qtyMoveNote = $"; qty move FAILED (left in mainDb){(failReason!=null?": "+Truncate(failReason,60):string.Empty)}";
                                            qtyMoveFail++;
                                            reason = failReason ?? string.Empty;
                                        }

                                        // Add debug diagnostic line
                                        debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty={origQty?.ToString() ?? "NULL"}|attempt=1|status={status}|reason={reason}|colPath={(File.Exists(collectionPath)?collectionPath:"missing")}|colQtyAfter={(collectionQtyAfter?.ToString()??"NULL")}|mainDbQtyAfter={(mainDbQtyAfter?.ToString()??"NULL")}|rule=CardId-is-MtgsId");
                                    }
                                    else
                                    {
                                        qtyMoveNote = "; collection file missing (qty left in mainDb)";
                                        qtyMoveFail++;
                                        debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty={origQty?.ToString() ?? "NULL"}|attempt=1|status=no-collection|reason=collection-missing");
                                    }
                                }
                                else if (hasQtyCol)
                                {
                                    qtyMoveSkippedNoQty++;
                                    debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty={(origQty?.ToString()??"NULL")}|attempt=0|status=skip-noqty");
                                }
                            }
                string modNote = modifierNeedsUpdate ? $"; set modifier=[{csvModifier}]" : string.Empty;
                string rekeyNote = string.Empty;
                if (foundId.HasValue && foundId.Value != mtgsId)
                {
                    bool canRekey = true;
                    try
                    {
                        using var chk = con.CreateCommand();
                        chk.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@nid LIMIT 1";
                        chk.Parameters.AddWithValue("@nid", mtgsId);
                        var exists = chk.ExecuteScalar();
                        if (exists != null && exists != DBNull.Value) { canRekey = false; rekeyNote = "; rekey-skip target-exists"; idRekeyFail++; }
                    }
                    catch (Exception exChk) { canRekey = false; rekeyNote = "; rekey-check-fail:" + Truncate(exChk.Message,40); idRekeyFail++; }
                    if (canRekey)
                    {
                        try
                        {
                            using var rk = con.CreateCommand();
                            rk.CommandText = $"UPDATE Cards SET {idCol}=@newId WHERE {idCol}=@oldId";
                            rk.Parameters.AddWithValue("@newId", mtgsId);
                            rk.Parameters.AddWithValue("@oldId", foundId.Value);
                            int changed = rk.ExecuteNonQuery();
                            if (changed == 1) { rekeyNote = $"; rekeyed {foundId.Value}->{mtgsId}"; idRekeySuccess++; foundId = mtgsId; }
                            else { rekeyNote = "; rekey-nochange"; idRekeyFail++; }
                        }
                        catch (Exception exRk) { rekeyNote = "; rekey-fail:" + Truncate(exRk.Message,40); idRekeyFail++; }
                    }
                }
                updateLines.Add(line + $" | UPDATE id={foundId.Value} MtgsId={mtgsId}" + modNote + rekeyNote + $"; origQty={(qtyMoveNote.Contains("moved Qty=")?"moved":(qtyMoveNote.Contains("FAILED")?"failed":(qtyMoveNote.Contains("missing")?"missing":"unknown")))}" + qtyMoveNote);
                            if (!hasQtyCol) debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty=NA|attempt=0|status=no-qty-column");
                        }
                        catch { errors++; errorLines.Add(line + " | ERROR update failed"); }
                    }
            else { updatedMtgs++; string modNote = modifierNeedsUpdate ? $"; WOULD SET modifier=[{csvModifier}]" : string.Empty; updateLines.Add(line + $" | WOULD UPDATE id={foundId.Value} MtgsId={mtgsId}" + modNote); }
                }
                else if (existingVal != mtgsId.ToString())
                {
                    bool resolvedAsSkip = false;
                    try
                    {
                        using var probe = con.CreateCommand();
                        var psb = new StringBuilder();
                        psb.Append($"SELECT 1 FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                        if (cardNum == null)
                            psb.Append($" AND ( {numberCol} IS NULL OR TRIM({numberCol})='' )");
                        else
                            psb.Append($" AND {numberCol}=@n");
                        psb.Append($" AND {nameCol}=@nm COLLATE NOCASE");
                        // modifier differences do not constitute a different card for this probe
                        // Look for any other row (possibly with a different version) that already has this MtgsId
                        psb.Append($" AND {mtgsCol}=@m AND {idCol}<>@fid LIMIT 1");
                        probe.CommandText = psb.ToString();
                        probe.Parameters.AddWithValue("@e", edition);
                        if (cardNum != null) probe.Parameters.AddWithValue("@n", cardNum);
                        probe.Parameters.AddWithValue("@nm", name);
                        probe.Parameters.AddWithValue("@m", mtgsId);
                        probe.Parameters.AddWithValue("@fid", foundId!.Value);
                        var existsElsewhere = probe.ExecuteScalar();
                        if (existsElsewhere != null && existsElsewhere != DBNull.Value)
                        {
                            // Another matching row already has this mapping; treat as already mapped, not a conflict
                            skippedExisting++;
                            skippedLines.Add(line + $" | SKIPPED resolved: different version row already mapped MtgsId={mtgsId}");
                            resolvedAsSkip = true;
                        }
                    }
                    catch (System.Exception) { throw; }
                    // If the conflict row is a known ignorable promo/reprint modifier, skip this row entirely
                    if (!resolvedAsSkip && IsIgnorableModifier(csvModifier))
                    {
                        skippedExisting++;
                        skippedLines.Add(line + " | SKIPPED ignored modifier in brackets");
                        goto ProgressOnly;
                    }
                    // If DB row has no version but CSV has a version, treat as an insert of a new versioned row (version=1)
                    if (!resolvedAsSkip && hasVersionCol && foundVersion == null && csvVersion.HasValue)
                    {
                        if (!dryRun)
                        {
                            int newId = mtgsId;
                            using (var chkId = con.CreateCommand())
                            {
                                chkId.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@id LIMIT 1";
                                chkId.Parameters.AddWithValue("@id", newId);
                                var exists = chkId.ExecuteScalar();
                                if (exists != null && exists != DBNull.Value)
                                {
                                    using var getMax = con.CreateCommand();
                                    getMax.CommandText = $"SELECT COALESCE(MAX({idCol}), 0) + 1 FROM Cards";
                                    var maxObj = getMax.ExecuteScalar();
                                    newId = Convert.ToInt32(maxObj);
                                }
                            }
                            using var ins2 = con.CreateCommand();
                            var colsSb2 = new StringBuilder($"{idCol},{editionCol},{numberCol},{nameCol},{mtgsCol}");
                            var valsSb2 = new StringBuilder("@id,@e,@n,@nm,@m");
                            if (hasModifierCol) { colsSb2.Append(",modifier"); valsSb2.Append(",@mod"); }
                            // Set version explicitly to 1 regardless of CSV version number per instruction
                            colsSb2.Append(",version"); valsSb2.Append(",@ver");
                            ins2.CommandText = $"INSERT INTO Cards ({colsSb2}) VALUES ({valsSb2})";
                            ins2.Parameters.AddWithValue("@id", newId);
                            ins2.Parameters.AddWithValue("@e", edition);
                            ins2.Parameters.AddWithValue("@n", (object?)cardNum ?? DBNull.Value);
                            ins2.Parameters.AddWithValue("@nm", name);
                            ins2.Parameters.AddWithValue("@m", mtgsId);
                            if (hasModifierCol) ins2.Parameters.AddWithValue("@mod", (object?)csvModifier ?? DBNull.Value);
                            ins2.Parameters.AddWithValue("@ver", 1);
                            try { ins2.ExecuteNonQuery(); inserted++; updateLines.Add(line + $" | INSERT (resolve conflict) id={newId} version=1"); } catch { errors++; errorLines.Add(line + " | ERROR insert (resolve conflict) failed"); }
                        }
                        else
                        {
                            unmatchedInsertLines.Add(line + " | RESOLVE_CONFLICT would insert version=1");
                        }
                        goto ProgressOnly;
                    }
                    if (!resolvedAsSkip)
                    {
                        conflicts++;
                        conflictLines.Add(line + $" | CONFLICT existing MtgsId={existingVal} at id={foundId}");
                    }
                }
                else
                {
                    // Already mapped to same MtgsId; still update modifier if needed
                    if (modifierNeedsUpdate)
                    {
                        if (!dryRun)
                        {
                            try
                            {
                                using var um = con.CreateCommand();
                                um.CommandText = $"UPDATE Cards SET modifier=@mod WHERE {idCol}=@id";
                                um.Parameters.AddWithValue("@mod", csvModifier);
                                um.Parameters.AddWithValue("@id", foundId!.Value);
                                um.ExecuteNonQuery();
                                updateLines.Add(line + $" | UPDATE id={foundId} modifier=[{csvModifier}] (same MtgsId)");
                            }
                            catch { errors++; errorLines.Add(line + " | ERROR modifier update failed"); }
                            preExistingMtgs++;
                        }
                        else
                        {
                            updateLines.Add(line + $" | WOULD UPDATE id={foundId} modifier=[{csvModifier}] (same MtgsId)");
                            preExistingMtgs++;
                        }
                    }
                    else
                    {
                        skippedExisting++; skippedLines.Add(line + $" | SKIPPED same MtgsId={mtgsId} at id={foundId}"); preExistingMtgs++;
                        skippedExisting++; skippedLines.Add(line + $" | SKIPPED same MtgsId={mtgsId} at id={foundId}"); preExistingMtgs++; debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty={(origQty?.ToString()??"NULL")}|attempt=0|status=already-mapped");
                    }
                }
            }
            else
            {
                // No match; report unmatched, optionally insert
                unmatchedInsertLines.Add(line + " | UNMATCHED would insert");
                if (!dryRun && insertMissing)
                {
                    // Choose a new mainDb id: prefer mtgsId if unused else max+1
                    int newId = mtgsId;
                    using (var chkId = con.CreateCommand())
                    {
                        chkId.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@id LIMIT 1";
                        chkId.Parameters.AddWithValue("@id", newId);
                        var exists = chkId.ExecuteScalar();
                        if (exists != null && exists != DBNull.Value)
                        {
                            using var getMax = con.CreateCommand();
                            getMax.CommandText = $"SELECT COALESCE(MAX({idCol}), 0) + 1 FROM Cards";
                            var maxObj = getMax.ExecuteScalar();
                            newId = Convert.ToInt32(maxObj);
                        }
                    }
                    using var ins = con.CreateCommand();
                    var colsSb = new StringBuilder($"{idCol},{editionCol},{numberCol},{nameCol},{mtgsCol}");
                    var valsSb = new StringBuilder("@id,@e,@n,@nm,@m");
                    if (hasModifierCol) { colsSb.Append(",modifier"); valsSb.Append(",@mod"); }
                    if (hasVersionCol && csvVersion.HasValue) { colsSb.Append(",version"); valsSb.Append(",@ver"); }
                    ins.CommandText = $"INSERT INTO Cards ({colsSb}) VALUES ({valsSb})";
                    ins.Parameters.AddWithValue("@id", newId);
                    ins.Parameters.AddWithValue("@e", edition);
                    ins.Parameters.AddWithValue("@n", (object?)cardNum ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@nm", name);
                    ins.Parameters.AddWithValue("@m", mtgsId);
                    if (hasModifierCol) ins.Parameters.AddWithValue("@mod", (object?)csvModifier ?? DBNull.Value);
                    if (hasVersionCol && csvVersion.HasValue) ins.Parameters.AddWithValue("@ver", csvVersion.Value);
                    try { ins.ExecuteNonQuery(); inserted++; unmatchedInsertLines.Add(line + $" | INSERT id={newId}"); } catch { errors++; errorLines.Add(line + " | ERROR insert failed"); }
                }
            }
ProgressOnly:
            // progress update
            try { progress?.Invoke(processed, total); } catch (System.Exception) { throw; }
        }

        // Write categorized logs (suffix with dryrun/apply)
        // Append summary BEFORE writing so it appears in updates file
    updateLines.Add($"# QTY MOVE SUMMARY success={qtyMoveSuccess} fail={qtyMoveFail} skipped_no_qty={qtyMoveSkippedNoQty} attempted={qtyMoveAttempted} preExistingMtgs={preExistingMtgs} idRekeySuccess={idRekeySuccess} idRekeyFail={idRekeyFail}");

        // Write categorized logs (suffix with dryrun/apply)
        try
        {
            string dir = Path.GetDirectoryName(csvPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(csvPath) + ".mtgs";
            string stage = dryRun ? "dryrun" : "apply";
            string PathFor(string label) => Path.Combine(dir, $"{baseName}-{label}.{stage}.csv");
            void WriteIfAny(List<string> list, string label)
            {
                if (list.Count == 0) return;
                File.WriteAllLines(PathFor(label), list);
            }
            WriteIfAny(errorLines, "errors");
            WriteIfAny(conflictLines, "conflicts");
            WriteIfAny(skippedLines, "skipped");
            WriteIfAny(updateLines, "updates");
            WriteIfAny(unmatchedInsertLines, "unmatched-inserts");
            WriteIfAny(debugLines, "debug");
            // Backward-compatible unmatched path points to unmatched inserts log
            unmatchedLogPath = unmatchedInsertLines.Count > 0 ? PathFor("unmatched-inserts") : null;
        }
    catch (System.Exception) { throw; }

        return new CsvMtgsMapResult(updatedMtgs, inserted, skippedExisting, conflicts, errors, dbPath, unmatchedLogPath);
    }

    /// <summary>
    /// Returns true if the CSV file is in MTG Studio export format (comma-delimited with CardId header).
    /// </summary>
    public static bool IsMtgsStudioCsvFormat(string csvPath)
    {
        using var sr = new StreamReader(csvPath);
        var firstLine = sr.ReadLine();
        return firstLine != null && firstLine.StartsWith("CardId,", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prepares a plan by matching CSV rows to mainDb without writing anything.
    /// Returns a result summary and a plan that can be passed to ApplyStudioCsvPlan.
    /// </summary>
    public static (CsvMtgsMapResult Result, StudioCsvPlan Plan) PrepareStudioCsvPlan(string csvPath, Action<int, int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath required");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found", csvPath);
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dbPath = Path.Combine(exeDir, "mainDb.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found in executable directory", dbPath);

        var allLines = File.ReadAllLines(csvPath);
        var emptyPlan = new StudioCsvPlan { DbPath = dbPath, CsvPath = csvPath };
        if (allLines.Length < 2) return (new CsvMtgsMapResult(0, 0, 0, 0, 0, dbPath, null), emptyPlan);

        var header = SplitCommaCsv(allLines[0]);
        int colCardId = Array.FindIndex(header, h => h.Equals("CardId", StringComparison.OrdinalIgnoreCase));
        int colName = Array.FindIndex(header, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
        int colSet = Array.FindIndex(header, h => h.Equals("SetAbbreviation", StringComparison.OrdinalIgnoreCase));
        int colNumber = Array.FindIndex(header, h => h.Equals("CollectorNoSortable", StringComparison.OrdinalIgnoreCase));
        if (colCardId < 0 || colName < 0 || colSet < 0 || colNumber < 0)
            throw new InvalidOperationException("CSV header missing required columns: CardId, Name, SetAbbreviation, CollectorNoSortable");

        int updatedMtgs = 0, skippedExisting = 0, conflicts = 0, errors = 0;
        string? unmatchedLogPath = null;
        var updateLines = new List<string>();
        var unmatchedInsertLines = new List<string>();

        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();

        var dbCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Cards)";
            using var r = pragma.ExecuteReader();
            while (r.Read()) { try { dbCols.Add(r.GetString(1)); } catch { } }
        }
        string idCol = dbCols.Contains("id") ? "id" : (dbCols.Contains("cardId") ? "cardId" : "id");
        string editionCol = dbCols.Contains("edition") ? "edition" : (dbCols.Contains("set") ? "set" : "edition");
        string numberCol = dbCols.Contains("collectorNumberValue") ? "collectorNumberValue" : (dbCols.Contains("numberValue") ? "numberValue" : "collectorNumberValue");
        string nameCol = dbCols.Contains("name") ? "name" : "name";
        string mtgsCol = dbCols.Contains("MtgsId") ? "MtgsId" : (dbCols.Contains("mtgsid") ? "mtgsid" : (dbCols.Contains("MTGSID") ? "MTGSID" : ""));
        if (string.IsNullOrEmpty(mtgsCol)) throw new InvalidOperationException("Cards table has no MtgsId column.");
        bool hasQtyCol = dbCols.Contains("Qty");

        var plan = new StudioCsvPlan { DbPath = dbPath, CsvPath = csvPath, IdCol = idCol, MtgsCol = mtgsCol, HasQtyCol = hasQtyCol };
        int total = allLines.Length - 1;
        int processed = 0;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < allLines.Length; i++)
        {
            var line = allLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            processed++;
            var csv = SplitCommaCsv(line);
            int minCol = Math.Max(Math.Max(colCardId, colName), Math.Max(colSet, colNumber)) + 1;
            if (csv.Length < minCol) { errors++; plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.Error, 0, null, null, "", "", null, line)); continue; }

            string cardIdStr = csv[colCardId].Trim();
            string name = csv[colName].Trim();
            string edition = csv[colSet].Trim();
            string rawNum = csv[colNumber].Trim();
            if (!int.TryParse(cardIdStr, out int mtgsId) || mtgsId <= 0)
            { errors++; plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.Error, 0, null, null, edition, name, null, line)); continue; }

            string cardNumParsed = ParseCollectorNumber(rawNum);
            string? cardNum = string.IsNullOrWhiteSpace(cardNumParsed) ? null : cardNumParsed;

            string key = string.Join("|", edition.ToLowerInvariant(), name.ToLowerInvariant(), cardNum ?? "");
            if (!seenKeys.Add(key)) { skippedExisting++; plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.SkippedDuplicate, mtgsId, null, null, edition, name, cardNum, line)); goto PrepareNext; }

            // Skip if MtgsId already mapped
            using (var chk = con.CreateCommand())
            {
                chk.CommandText = $"SELECT 1 FROM Cards WHERE {mtgsCol}=@m LIMIT 1";
                chk.Parameters.AddWithValue("@m", mtgsId);
                var exists = chk.ExecuteScalar();
                if (exists != null && exists != DBNull.Value) { skippedExisting++; plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.SkippedAlreadyMapped, mtgsId, null, null, edition, name, cardNum, line)); continue; }
            }

            // Find by edition + name + number
            int? foundId = null; object? foundMtgs = null;
            using (var find = con.CreateCommand())
            {
                var sb = new StringBuilder();
                sb.Append($"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                if (cardNum == null)
                    sb.Append($" AND ({numberCol} IS NULL OR TRIM({numberCol})='')");
                else
                    sb.Append($" AND {numberCol}=@n");
                sb.Append($" AND {nameCol}=@nm COLLATE NOCASE LIMIT 1");
                find.CommandText = sb.ToString();
                find.Parameters.AddWithValue("@e", edition);
                if (cardNum != null) find.Parameters.AddWithValue("@n", cardNum);
                find.Parameters.AddWithValue("@nm", name);
                using var rr = find.ExecuteReader();
                if (rr.Read())
                {
                    foundId = rr.IsDBNull(0) ? null : rr.GetInt32(0);
                    foundMtgs = rr.IsDBNull(1) ? null : rr.GetValue(1);
                }
            }

            // Fallback: name with "/" → try " // " form
            if (!foundId.HasValue && name.Contains('/') && !name.Contains(" // "))
            {
                string alt = Regex.Replace(name, @"\s*/\s*", " // ").Trim();
                if (!string.Equals(alt, name, StringComparison.OrdinalIgnoreCase))
                {
                    using var findAlt = con.CreateCommand();
                    var sb = new StringBuilder();
                    sb.Append($"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                    if (cardNum == null)
                        sb.Append($" AND ({numberCol} IS NULL OR TRIM({numberCol})='')");
                    else
                        sb.Append($" AND {numberCol}=@n");
                    sb.Append($" AND {nameCol}=@nm COLLATE NOCASE LIMIT 1");
                    findAlt.CommandText = sb.ToString();
                    findAlt.Parameters.AddWithValue("@e", edition);
                    if (cardNum != null) findAlt.Parameters.AddWithValue("@n", cardNum);
                    findAlt.Parameters.AddWithValue("@nm", alt);
                    using var ra = findAlt.ExecuteReader();
                    if (ra.Read())
                    {
                        foundId = ra.IsDBNull(0) ? null : ra.GetInt32(0);
                        foundMtgs = ra.IsDBNull(1) ? null : ra.GetValue(1);
                    }
                }
            }

            // Second fallback: ignore collector number if unique by edition + name
            if (!foundId.HasValue)
            {
                using var countCmd = con.CreateCommand();
                countCmd.CommandText = $"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE AND {nameCol}=@nm COLLATE NOCASE";
                countCmd.Parameters.AddWithValue("@e", edition);
                countCmd.Parameters.AddWithValue("@nm", name);
                var matches = new List<(int id, object? mtgs)>();
                using (var rb = countCmd.ExecuteReader())
                {
                    while (rb.Read())
                    {
                        int idv = rb.IsDBNull(0) ? -1 : rb.GetInt32(0);
                        object? mtgsv = rb.IsDBNull(1) ? null : rb.GetValue(1);
                        if (idv >= 0) matches.Add((idv, mtgsv));
                    }
                }
                if (matches.Count == 1) { foundId = matches[0].id; foundMtgs = matches[0].mtgs; }
            }

            if (foundId.HasValue)
            {
                string? existingVal = foundMtgs?.ToString();
                bool isEmpty = string.IsNullOrWhiteSpace(existingVal) || existingVal == "0";

                int? origQty = null;
                if (hasQtyCol)
                {
                    using var qcmd = con.CreateCommand();
                    qcmd.CommandText = $"SELECT Qty FROM Cards WHERE {idCol}=@id";
                    qcmd.Parameters.AddWithValue("@id", foundId.Value);
                    var qobj = qcmd.ExecuteScalar();
                    if (qobj != null && qobj != DBNull.Value && int.TryParse(qobj.ToString(), out var oq)) origQty = oq;
                }

                if (isEmpty)
                {
                    updatedMtgs++;
                    updateLines.Add($"{edition}|{name}|{cardNum} | WOULD UPDATE id={foundId.Value} MtgsId={mtgsId}");
                    plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.Update, mtgsId, foundId.Value, origQty, edition, name, cardNum, line));
                }
                else if (existingVal != mtgsId.ToString())
                {
                    conflicts++;
                    plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.Conflict, mtgsId, foundId.Value, origQty, edition, name, cardNum, line, existingVal));
                }
                else
                {
                    skippedExisting++;
                    plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.SkippedAlreadyMapped, mtgsId, foundId.Value, origQty, edition, name, cardNum, line, existingVal));
                }
            }
            else
            {
                unmatchedInsertLines.Add(line + " | UNMATCHED");
                plan.Entries.Add(new StudioCsvPlanEntry(StudioMatchKind.Unmatched, mtgsId, null, null, edition, name, cardNum, line));
            }

PrepareNext:
            try { progress?.Invoke(processed, total); } catch { }
        }

        // Write plan-stage logs
        try
        {
            string dir = Path.GetDirectoryName(csvPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(csvPath) + ".studio";
            void WriteIfAny(List<string> list, string label)
            {
                if (list.Count == 0) return;
                File.WriteAllLines(Path.Combine(dir, $"{baseName}-{label}.dryrun.csv"), list);
            }
            WriteIfAny(updateLines, "updates");
            WriteIfAny(unmatchedInsertLines, "unmatched");
            unmatchedLogPath = unmatchedInsertLines.Count > 0 ? Path.Combine(dir, $"{baseName}-unmatched.dryrun.csv") : null;
        }
        catch { }

        int unmatchedCount = plan.Entries.Count(e => e.Kind == StudioMatchKind.Unmatched);
        var result = new CsvMtgsMapResult(updatedMtgs, 0, skippedExisting, conflicts, errors, dbPath, unmatchedLogPath);
        return (result, plan);
    }

    /// <summary>
    /// Applies a previously prepared plan: writes MtgsId, transfers qty, rekeys ids, and optionally inserts unmatched rows.
    /// </summary>
    public static CsvMtgsMapResult ApplyStudioCsvPlan(StudioCsvPlan plan, bool insertMissing = false, Action<int, int>? progress = null)
    {
        if (!File.Exists(plan.DbPath)) throw new FileNotFoundException("mainDb.db not found", plan.DbPath);
        string exeDir = Path.GetDirectoryName(plan.DbPath)!;

        int updatedMtgs = 0, inserted = 0, skippedExisting = 0, conflicts = 0, errors = 0;
        int qtyMoveSuccess = 0, qtyMoveFail = 0, qtyMoveSkippedNoQty = 0, qtyMoveAttempted = 0;
        int idRekeySuccess = 0, idRekeyFail = 0;
        string? unmatchedLogPath = null;
        var updateLogLines = new List<string>();
        var unmatchedLogLines = new List<string>();
        var errorLogLines = new List<string>();
        var debugLogLines = new List<string>();

        string idCol = plan.IdCol;
        string mtgsCol = plan.MtgsCol;
        bool hasQtyCol = plan.HasQtyCol;

        using var con = new SqliteConnection($"Data Source={plan.DbPath}");
        con.Open();

        var updates = plan.Entries.Where(e => e.Kind == StudioMatchKind.Update).ToList();
        var unmatched = plan.Entries.Where(e => e.Kind == StudioMatchKind.Unmatched).ToList();
        skippedExisting = plan.Entries.Count(e => e.Kind == StudioMatchKind.SkippedAlreadyMapped || e.Kind == StudioMatchKind.SkippedDuplicate);
        conflicts = plan.Entries.Count(e => e.Kind == StudioMatchKind.Conflict);
        errors = plan.Entries.Count(e => e.Kind == StudioMatchKind.Error);

        int total = updates.Count + (insertMissing ? unmatched.Count : 0);
        int processed = 0;

        foreach (var entry in updates)
        {
            processed++;
            try
            {
                using var upd = con.CreateCommand();
                upd.CommandText = $"UPDATE Cards SET {mtgsCol}=@m WHERE {idCol}=@id";
                upd.Parameters.AddWithValue("@m", entry.MtgsId);
                upd.Parameters.AddWithValue("@id", entry.FoundId!.Value);
                upd.ExecuteNonQuery();
                updatedMtgs++;
                string qtyMoveNote = string.Empty;

                // Determine rekey feasibility FIRST so CollectionCards and Cards.id stay consistent
                bool canRekey = false;
                bool wasRekeyed = false;
                string rekeyNote = string.Empty;
                if (entry.FoundId!.Value != entry.MtgsId)
                {
                    canRekey = true;
                    using (var chk2 = con.CreateCommand())
                    {
                        chk2.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@nid LIMIT 1";
                        chk2.Parameters.AddWithValue("@nid", entry.MtgsId);
                        var exists = chk2.ExecuteScalar();
                        if (exists != null && exists != DBNull.Value) canRekey = false;
                    }
                }

                // Rekey Cards.id before touching CollectionCards so both stay in sync
                if (canRekey)
                {
                    using var rk = con.CreateCommand();
                    rk.CommandText = $"UPDATE Cards SET {idCol}=@newId WHERE {idCol}=@oldId";
                    rk.Parameters.AddWithValue("@newId", entry.MtgsId);
                    rk.Parameters.AddWithValue("@oldId", entry.FoundId!.Value);
                    int changed = rk.ExecuteNonQuery();
                    if (changed == 1) { wasRekeyed = true; rekeyNote = $"; rekeyed {entry.FoundId.Value}->{entry.MtgsId}"; idRekeySuccess++; }
                    else { rekeyNote = "; rekey-nochange"; idRekeyFail++; }
                }
                else if (entry.FoundId!.Value != entry.MtgsId)
                {
                    rekeyNote = "; rekey-skip target-exists"; idRekeyFail++;
                }

                // The collection CardId to use: MtgsId if rekey succeeded (Cards.id is now MtgsId),
                // otherwise keep using the internal id so CollectionCards stays consistent with Cards.id.
                int collectionCardId = wasRekeyed ? entry.MtgsId : entry.FoundId!.Value;

                // Rekey existing CollectionCards rows only if Cards.id was actually rekeyed
                string collectionPath = Path.Combine(exeDir, "mtgstudio.collection");
                if (wasRekeyed && File.Exists(collectionPath))
                {
                    try
                    {
                        using var conFix = new SqliteConnection($"Data Source={collectionPath}");
                        conFix.Open();
                        using var fix = conFix.CreateCommand();
                        fix.CommandText = "UPDATE CollectionCards SET CardId=@mtgs WHERE CardId=@internal AND NOT EXISTS (SELECT 1 FROM CollectionCards WHERE CardId=@mtgs)";
                        fix.Parameters.AddWithValue("@mtgs", entry.MtgsId);
                        fix.Parameters.AddWithValue("@internal", entry.FoundId!.Value);
                        fix.ExecuteNonQuery();
                    }
                    catch { /* best effort; reverse map fix in Load() provides safety net */ }
                }

                // Qty transfer from mainDb to collection
                if (hasQtyCol && entry.OrigQty.HasValue)
                {
                    qtyMoveAttempted++;
                    if (File.Exists(collectionPath))
                    {
                        bool success = false; string? failReason = null;
                        try
                        {
                            using var conCol = new SqliteConnection($"Data Source={collectionPath}");
                            conCol.Open();
                            int affected = 0;
                            using (var u = conCol.CreateCommand())
                            {
                                u.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                                u.Parameters.AddWithValue("@q", entry.OrigQty.Value);
                                u.Parameters.AddWithValue("@id", collectionCardId);
                                affected = u.ExecuteNonQuery();
                            }
                            if (affected == 0)
                            {
                                using var ins = conCol.CreateCommand();
                                ins.CommandText = @"INSERT INTO CollectionCards 
                        (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                        VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                                ins.Parameters.AddWithValue("@id", collectionCardId);
                                ins.Parameters.AddWithValue("@q", entry.OrigQty.Value);
                                ins.Parameters.AddWithValue("@added", DateTime.Now.ToString("s").Replace('T', ' '));
                                ins.ExecuteNonQuery();
                            }
                            success = true;
                        }
                        catch (Exception ex) { success = false; failReason = ex.Message; }
                        if (success)
                        {
                            // Clear mainDb Qty now that collection has the value
                            int clearId = wasRekeyed ? entry.MtgsId : entry.FoundId!.Value;
                            using var clr = con.CreateCommand();
                            clr.CommandText = $"UPDATE Cards SET Qty=NULL WHERE {idCol}=@id";
                            clr.Parameters.AddWithValue("@id", clearId);
                            clr.ExecuteNonQuery();
                            qtyMoveSuccess++;
                            qtyMoveNote = "; qty moved";
                        }
                        else { qtyMoveFail++; qtyMoveNote = $"; qty move FAILED: {Truncate(failReason, 60)}"; }
                    }
                    else { qtyMoveFail++; qtyMoveNote = "; collection file missing"; }
                }
                else if (hasQtyCol) { qtyMoveSkippedNoQty++; }

                updateLogLines.Add($"{entry.Edition}|{entry.Name}|{entry.CardNum} | UPDATE id={entry.FoundId.Value} MtgsId={entry.MtgsId}" + qtyMoveNote + rekeyNote);
                debugLogLines.Add($"ID={entry.FoundId.Value}|mtgs={entry.MtgsId}|origQty={entry.OrigQty?.ToString() ?? "NULL"}|status=updated" + qtyMoveNote + rekeyNote);
            }
            catch
            {
                errors++;
                errorLogLines.Add(entry.RawLine + " | ERROR update failed");
            }
            try { progress?.Invoke(processed, total); } catch { }
        }

        // Insert unmatched if requested
        if (insertMissing)
        {
            // Detect edition/number column names (needed for INSERT)
            var dbCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = con.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Cards)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) { try { dbCols.Add(r.GetString(1)); } catch { } }
            }
            string editionCol = dbCols.Contains("edition") ? "edition" : (dbCols.Contains("set") ? "set" : "edition");
            string numberCol = dbCols.Contains("collectorNumberValue") ? "collectorNumberValue" : (dbCols.Contains("numberValue") ? "numberValue" : "collectorNumberValue");
            string nameCol = dbCols.Contains("name") ? "name" : "name";

            foreach (var entry in unmatched)
            {
                processed++;
                int newId = entry.MtgsId;
                using (var chkId = con.CreateCommand())
                {
                    chkId.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@id LIMIT 1";
                    chkId.Parameters.AddWithValue("@id", newId);
                    var exists = chkId.ExecuteScalar();
                    if (exists != null && exists != DBNull.Value)
                    {
                        using var getMax = con.CreateCommand();
                        getMax.CommandText = $"SELECT COALESCE(MAX({idCol}), 0) + 1 FROM Cards";
                        var maxObj = getMax.ExecuteScalar();
                        newId = Convert.ToInt32(maxObj);
                    }
                }
                using var ins = con.CreateCommand();
                ins.CommandText = $"INSERT INTO Cards ({idCol},{editionCol},{numberCol},{nameCol},{mtgsCol}) VALUES (@id,@e,@n,@nm,@m)";
                ins.Parameters.AddWithValue("@id", newId);
                ins.Parameters.AddWithValue("@e", entry.Edition);
                ins.Parameters.AddWithValue("@n", (object?)entry.CardNum ?? DBNull.Value);
                ins.Parameters.AddWithValue("@nm", entry.Name);
                ins.Parameters.AddWithValue("@m", entry.MtgsId);
                try { ins.ExecuteNonQuery(); inserted++; }
                catch { errors++; errorLogLines.Add(entry.RawLine + " | ERROR insert failed"); }
                unmatchedLogLines.Add(entry.RawLine + " | INSERTED");
                try { progress?.Invoke(processed, total); } catch { }
            }
        }

        // Write apply-stage logs
        updateLogLines.Add($"# SUMMARY updated={updatedMtgs} inserted={inserted} skipped={skippedExisting} conflicts={conflicts} errors={errors} qtyMoved={qtyMoveSuccess} qtyFail={qtyMoveFail} rekeyOk={idRekeySuccess} rekeyFail={idRekeyFail}");
        try
        {
            string dir = Path.GetDirectoryName(plan.CsvPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(plan.CsvPath) + ".studio";
            void WriteIfAny(List<string> list, string label)
            {
                if (list.Count == 0) return;
                File.WriteAllLines(Path.Combine(dir, $"{baseName}-{label}.apply.csv"), list);
            }
            WriteIfAny(errorLogLines, "errors");
            WriteIfAny(updateLogLines, "updates");
            WriteIfAny(unmatchedLogLines, "unmatched");
            WriteIfAny(debugLogLines, "debug");
            unmatchedLogPath = unmatchedLogLines.Count > 0 ? Path.Combine(dir, $"{baseName}-unmatched.apply.csv") : null;
        }
        catch { }

        return new CsvMtgsMapResult(updatedMtgs, inserted, skippedExisting, conflicts, errors, plan.DbPath, unmatchedLogPath);
    }

    /// <summary>
    /// Processes an MTG Studio Collection CSV export (comma-delimited with header).
    /// Maps CardId (= MtgsId) onto matching mainDb rows by edition + name + collector number,
    /// transfers quantities to mtgstudio.collection, and rekeys internal ids.
    /// </summary>
    public static CsvMtgsMapResult ProcessMtgsStudioCsv(string csvPath, bool dryRun = true, bool insertMissing = false, Action<int, int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath required");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found", csvPath);
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dbPath = Path.Combine(exeDir, "mainDb.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found in executable directory", dbPath);

        var allLines = File.ReadAllLines(csvPath);
        if (allLines.Length < 2) return new CsvMtgsMapResult(0, 0, 0, 0, 0, dbPath, null);

        // Parse header to find column indices
        var header = SplitCommaCsv(allLines[0]);
        int colCardId = Array.FindIndex(header, h => h.Equals("CardId", StringComparison.OrdinalIgnoreCase));
        int colName = Array.FindIndex(header, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
        int colSet = Array.FindIndex(header, h => h.Equals("SetAbbreviation", StringComparison.OrdinalIgnoreCase));
        int colNumber = Array.FindIndex(header, h => h.Equals("CollectorNoSortable", StringComparison.OrdinalIgnoreCase));
        if (colCardId < 0 || colName < 0 || colSet < 0 || colNumber < 0)
            throw new InvalidOperationException("CSV header missing required columns: CardId, Name, SetAbbreviation, CollectorNoSortable");

        int updatedMtgs = 0, inserted = 0, skippedExisting = 0, conflicts = 0, errors = 0;
        string? unmatchedLogPath = null;
        var errorLines = new List<string>();
        var conflictLines = new List<string>();
        var skippedLines = new List<string>();
        var updateLines = new List<string>();
        var unmatchedInsertLines = new List<string>();
        var debugLines = new List<string>();
        int qtyMoveSuccess = 0, qtyMoveFail = 0, qtyMoveSkippedNoQty = 0, qtyMoveAttempted = 0;
        int idRekeySuccess = 0, idRekeyFail = 0;

        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();

        // Detect DB column names
        var dbCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Cards)";
            using var r = pragma.ExecuteReader();
            while (r.Read()) { try { dbCols.Add(r.GetString(1)); } catch { } }
        }
        string idCol = dbCols.Contains("id") ? "id" : (dbCols.Contains("cardId") ? "cardId" : "id");
        string editionCol = dbCols.Contains("edition") ? "edition" : (dbCols.Contains("set") ? "set" : "edition");
        string numberCol = dbCols.Contains("collectorNumberValue") ? "collectorNumberValue" : (dbCols.Contains("numberValue") ? "numberValue" : "collectorNumberValue");
        string nameCol = dbCols.Contains("name") ? "name" : "name";
        string mtgsCol = dbCols.Contains("MtgsId") ? "MtgsId" : (dbCols.Contains("mtgsid") ? "mtgsid" : (dbCols.Contains("MTGSID") ? "MTGSID" : ""));
        if (string.IsNullOrEmpty(mtgsCol)) throw new InvalidOperationException("Cards table has no MtgsId column.");
        bool hasQtyCol = dbCols.Contains("Qty");

        int total = allLines.Length - 1;
        int processed = 0;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < allLines.Length; i++)
        {
            var line = allLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            processed++;
            var csv = SplitCommaCsv(line);
            int minCol = Math.Max(Math.Max(colCardId, colName), Math.Max(colSet, colNumber)) + 1;
            if (csv.Length < minCol) { errors++; errorLines.Add(line + " | ERROR too few columns"); continue; }

            string cardIdStr = csv[colCardId].Trim();
            string name = csv[colName].Trim();
            string edition = csv[colSet].Trim();
            string rawNum = csv[colNumber].Trim();
            if (!int.TryParse(cardIdStr, out int mtgsId) || mtgsId <= 0)
            { errors++; errorLines.Add(line + " | ERROR invalid CardId"); continue; }

            string cardNumParsed = ParseCollectorNumber(rawNum);
            string? cardNum = string.IsNullOrWhiteSpace(cardNumParsed) ? null : cardNumParsed;

            // Deduplicate
            string key = string.Join("|", edition.ToLowerInvariant(), name.ToLowerInvariant(), cardNum ?? "");
            if (!seenKeys.Add(key)) { skippedExisting++; skippedLines.Add(line + " | SKIPPED duplicate CSV row"); goto StudioNextRow; }

            // Skip if MtgsId already mapped
            using (var chk = con.CreateCommand())
            {
                chk.CommandText = $"SELECT 1 FROM Cards WHERE {mtgsCol}=@m LIMIT 1";
                chk.Parameters.AddWithValue("@m", mtgsId);
                var exists = chk.ExecuteScalar();
                if (exists != null && exists != DBNull.Value) { skippedExisting++; skippedLines.Add(line + " | SKIPPED MtgsId already mapped"); continue; }
            }

            // Find by edition + name + number
            int? foundId = null; object? foundMtgs = null;
            using (var find = con.CreateCommand())
            {
                var sb = new StringBuilder();
                sb.Append($"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                if (cardNum == null)
                    sb.Append($" AND ({numberCol} IS NULL OR TRIM({numberCol})='')");
                else
                    sb.Append($" AND {numberCol}=@n");
                sb.Append($" AND {nameCol}=@nm COLLATE NOCASE LIMIT 1");
                find.CommandText = sb.ToString();
                find.Parameters.AddWithValue("@e", edition);
                if (cardNum != null) find.Parameters.AddWithValue("@n", cardNum);
                find.Parameters.AddWithValue("@nm", name);
                using var rr = find.ExecuteReader();
                if (rr.Read())
                {
                    foundId = rr.IsDBNull(0) ? null : rr.GetInt32(0);
                    foundMtgs = rr.IsDBNull(1) ? null : rr.GetValue(1);
                }
            }

            // Fallback: name with "/" → try " // " form
            if (!foundId.HasValue && name.Contains('/') && !name.Contains(" // "))
            {
                string alt = Regex.Replace(name, @"\s*/\s*", " // ").Trim();
                if (!string.Equals(alt, name, StringComparison.OrdinalIgnoreCase))
                {
                    using var findAlt = con.CreateCommand();
                    var sb = new StringBuilder();
                    sb.Append($"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE");
                    if (cardNum == null)
                        sb.Append($" AND ({numberCol} IS NULL OR TRIM({numberCol})='')");
                    else
                        sb.Append($" AND {numberCol}=@n");
                    sb.Append($" AND {nameCol}=@nm COLLATE NOCASE LIMIT 1");
                    findAlt.CommandText = sb.ToString();
                    findAlt.Parameters.AddWithValue("@e", edition);
                    if (cardNum != null) findAlt.Parameters.AddWithValue("@n", cardNum);
                    findAlt.Parameters.AddWithValue("@nm", alt);
                    using var ra = findAlt.ExecuteReader();
                    if (ra.Read())
                    {
                        foundId = ra.IsDBNull(0) ? null : ra.GetInt32(0);
                        foundMtgs = ra.IsDBNull(1) ? null : ra.GetValue(1);
                    }
                }
            }

            // Second fallback: ignore collector number if unique by edition + name
            if (!foundId.HasValue)
            {
                using var countCmd = con.CreateCommand();
                countCmd.CommandText = $"SELECT {idCol}, {mtgsCol} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE AND {nameCol}=@nm COLLATE NOCASE";
                countCmd.Parameters.AddWithValue("@e", edition);
                countCmd.Parameters.AddWithValue("@nm", name);
                var matches = new List<(int id, object? mtgs)>();
                using (var rb = countCmd.ExecuteReader())
                {
                    while (rb.Read())
                    {
                        int idv = rb.IsDBNull(0) ? -1 : rb.GetInt32(0);
                        object? mtgsv = rb.IsDBNull(1) ? null : rb.GetValue(1);
                        if (idv >= 0) matches.Add((idv, mtgsv));
                    }
                }
                if (matches.Count == 1)
                {
                    foundId = matches[0].id;
                    foundMtgs = matches[0].mtgs;
                }
            }

            if (foundId.HasValue)
            {
                string? existingVal = foundMtgs?.ToString();
                bool isEmpty = string.IsNullOrWhiteSpace(existingVal) || existingVal == "0";

                int? origQty = null;
                if (hasQtyCol)
                {
                    using var qcmd = con.CreateCommand();
                    qcmd.CommandText = $"SELECT Qty FROM Cards WHERE {idCol}=@id";
                    qcmd.Parameters.AddWithValue("@id", foundId.Value);
                    var qobj = qcmd.ExecuteScalar();
                    if (qobj != null && qobj != DBNull.Value && int.TryParse(qobj.ToString(), out var oq)) origQty = oq;
                }

                if (isEmpty)
                {
                    if (!dryRun)
                    {
                        using var upd = con.CreateCommand();
                        upd.CommandText = $"UPDATE Cards SET {mtgsCol}=@m WHERE {idCol}=@id";
                        upd.Parameters.AddWithValue("@m", mtgsId);
                        upd.Parameters.AddWithValue("@id", foundId.Value);
                        try
                        {
                            upd.ExecuteNonQuery();
                            updatedMtgs++;
                            string qtyMoveNote = string.Empty;

                            // Pre-check whether Cards.id rekey will succeed
                            bool canRekey3 = false;
                            bool wasRekeyed3 = false;
                            string rekeyNote = string.Empty;
                            if (foundId.Value != mtgsId)
                            {
                                canRekey3 = true;
                                using (var chk2 = con.CreateCommand())
                                {
                                    chk2.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@nid LIMIT 1";
                                    chk2.Parameters.AddWithValue("@nid", mtgsId);
                                    var exists = chk2.ExecuteScalar();
                                    if (exists != null && exists != DBNull.Value) canRekey3 = false;
                                }
                            }

                            // Rekey Cards.id first so CollectionCards stays consistent
                            if (canRekey3)
                            {
                                using var rk = con.CreateCommand();
                                rk.CommandText = $"UPDATE Cards SET {idCol}=@newId WHERE {idCol}=@oldId";
                                rk.Parameters.AddWithValue("@newId", mtgsId);
                                rk.Parameters.AddWithValue("@oldId", foundId.Value);
                                int changed = rk.ExecuteNonQuery();
                                if (changed == 1) { wasRekeyed3 = true; rekeyNote = $"; rekeyed {foundId.Value}->{mtgsId}"; idRekeySuccess++; }
                                else { rekeyNote = "; rekey-nochange"; idRekeyFail++; }
                            }
                            else if (foundId.Value != mtgsId)
                            {
                                rekeyNote = "; rekey-skip target-exists"; idRekeyFail++;
                            }

                            int collectionCardId3 = wasRekeyed3 ? mtgsId : foundId.Value;

                            // Rekey CollectionCards only if Cards.id was rekeyed
                            string collectionPath = Path.Combine(exeDir, "mtgstudio.collection");
                            if (wasRekeyed3 && File.Exists(collectionPath))
                            {
                                try
                                {
                                    using var conFix = new SqliteConnection($"Data Source={collectionPath}");
                                    conFix.Open();
                                    using var fix = conFix.CreateCommand();
                                    fix.CommandText = "UPDATE CollectionCards SET CardId=@mtgs WHERE CardId=@internal AND NOT EXISTS (SELECT 1 FROM CollectionCards WHERE CardId=@mtgs)";
                                    fix.Parameters.AddWithValue("@mtgs", mtgsId);
                                    fix.Parameters.AddWithValue("@internal", foundId.Value);
                                    fix.ExecuteNonQuery();
                                }
                                catch { /* best effort; reverse map fix in Load() provides safety net */ }
                            }

                            // Qty transfer: move qty from mainDb to collection
                            if (hasQtyCol && origQty.HasValue)
                            {
                                qtyMoveAttempted++;
                                if (File.Exists(collectionPath))
                                {
                                    bool success = false; string? failReason = null;
                                    try
                                    {
                                        using var conCol = new SqliteConnection($"Data Source={collectionPath}");
                                        conCol.Open();
                                        int affected = 0;
                                        using (var u = conCol.CreateCommand())
                                        {
                                            u.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                                            u.Parameters.AddWithValue("@q", origQty.Value);
                                            u.Parameters.AddWithValue("@id", collectionCardId3);
                                            affected = u.ExecuteNonQuery();
                                        }
                                        if (affected == 0)
                                        {
                                            using var ins = conCol.CreateCommand();
                                            ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                                            ins.Parameters.AddWithValue("@id", collectionCardId3);
                                            ins.Parameters.AddWithValue("@q", origQty.Value);
                                            ins.Parameters.AddWithValue("@added", DateTime.Now.ToString("s").Replace('T', ' '));
                                            ins.ExecuteNonQuery();
                                        }
                                        success = true;
                                    }
                                    catch (Exception ex) { success = false; failReason = ex.Message; }
                                    if (success)
                                    {
                                        int clearId = wasRekeyed3 ? mtgsId : foundId.Value;
                                        using var clr = con.CreateCommand();
                                        clr.CommandText = $"UPDATE Cards SET Qty=NULL WHERE {idCol}=@id";
                                        clr.Parameters.AddWithValue("@id", clearId);
                                        clr.ExecuteNonQuery();
                                        qtyMoveSuccess++;
                                        qtyMoveNote = "; qty moved";
                                    }
                                    else
                                    {
                                        qtyMoveFail++;
                                        qtyMoveNote = $"; qty move FAILED: {Truncate(failReason, 60)}";
                                    }
                                }
                                else { qtyMoveFail++; qtyMoveNote = "; collection file missing"; }
                            }
                            else if (hasQtyCol) { qtyMoveSkippedNoQty++; }

                            updateLines.Add($"{edition}|{name}|{cardNum} | UPDATE id={foundId.Value} MtgsId={mtgsId}" + qtyMoveNote + rekeyNote);
                            debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|origQty={origQty?.ToString() ?? "NULL"}|status=updated" + qtyMoveNote + rekeyNote);
                        }
                        catch { errors++; errorLines.Add(line + " | ERROR update failed"); }
                    }
                    else
                    {
                        updatedMtgs++;
                        updateLines.Add($"{edition}|{name}|{cardNum} | WOULD UPDATE id={foundId.Value} MtgsId={mtgsId}");
                    }
                }
                else if (existingVal != mtgsId.ToString())
                {
                    conflicts++;
                    conflictLines.Add($"{edition}|{name}|{cardNum} | CONFLICT existing MtgsId={existingVal} at id={foundId}");
                }
                else
                {
                    skippedExisting++;
                    skippedLines.Add($"{edition}|{name}|{cardNum} | SKIPPED same MtgsId={mtgsId}");
                    debugLines.Add($"ID={foundId.Value}|mtgs={mtgsId}|status=already-mapped");
                }
            }
            else
            {
                unmatchedInsertLines.Add(line + " | UNMATCHED");
                if (!dryRun && insertMissing)
                {
                    int newId = mtgsId;
                    using (var chkId = con.CreateCommand())
                    {
                        chkId.CommandText = $"SELECT 1 FROM Cards WHERE {idCol}=@id LIMIT 1";
                        chkId.Parameters.AddWithValue("@id", newId);
                        var exists = chkId.ExecuteScalar();
                        if (exists != null && exists != DBNull.Value)
                        {
                            using var getMax = con.CreateCommand();
                            getMax.CommandText = $"SELECT COALESCE(MAX({idCol}), 0) + 1 FROM Cards";
                            var maxObj = getMax.ExecuteScalar();
                            newId = Convert.ToInt32(maxObj);
                        }
                    }
                    using var ins = con.CreateCommand();
                    ins.CommandText = $"INSERT INTO Cards ({idCol},{editionCol},{numberCol},{nameCol},{mtgsCol}) VALUES (@id,@e,@n,@nm,@m)";
                    ins.Parameters.AddWithValue("@id", newId);
                    ins.Parameters.AddWithValue("@e", edition);
                    ins.Parameters.AddWithValue("@n", (object?)cardNum ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@nm", name);
                    ins.Parameters.AddWithValue("@m", mtgsId);
                    try { ins.ExecuteNonQuery(); inserted++; } catch { errors++; errorLines.Add(line + " | ERROR insert failed"); }
                }
            }

StudioNextRow:
            try { progress?.Invoke(processed, total); } catch { }
        }

        // Write categorized logs
        updateLines.Add($"# SUMMARY updated={updatedMtgs} inserted={inserted} skipped={skippedExisting} conflicts={conflicts} errors={errors} qtyMoved={qtyMoveSuccess} qtyFail={qtyMoveFail} rekeyOk={idRekeySuccess} rekeyFail={idRekeyFail}");
        try
        {
            string dir = Path.GetDirectoryName(csvPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(csvPath) + ".studio";
            string stage = dryRun ? "dryrun" : "apply";
            string PathFor(string label) => Path.Combine(dir, $"{baseName}-{label}.{stage}.csv");
            void WriteIfAny(List<string> list, string label)
            {
                if (list.Count == 0) return;
                File.WriteAllLines(PathFor(label), list);
            }
            WriteIfAny(errorLines, "errors");
            WriteIfAny(conflictLines, "conflicts");
            WriteIfAny(skippedLines, "skipped");
            WriteIfAny(updateLines, "updates");
            WriteIfAny(unmatchedInsertLines, "unmatched");
            WriteIfAny(debugLines, "debug");
            unmatchedLogPath = unmatchedInsertLines.Count > 0 ? PathFor("unmatched") : null;
        }
        catch { }

        return new CsvMtgsMapResult(updatedMtgs, inserted, skippedExisting, conflicts, errors, dbPath, unmatchedLogPath);
    }

    private static string[] SplitCommaCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        result.Add(sb.ToString());
        for (int i = 0; i < result.Count; i++)
        {
            result[i] = result[i].Trim();
        }
        return result.ToArray();
    }

    public static CsvUpdateResult Process(string csvPath, string? explicitDbPath = null, Action<int,int>? progress = null)
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
                catch (System.Exception) { throw; }
            }
        }
    catch (System.Exception) { throw; }

        int total = 0, processed = 0; for (int i = 0; i < lines.Count; i++) if (!string.IsNullOrWhiteSpace(lines[i])) total++;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            processed++;
            var cols = SplitSemicolonCsv(line);
            if (cols.Length < 6) continue; // silently skip insufficient columns per prior behavior

            string nameRaw = cols[0].Trim();
            string? modifier = null; int? versionNumber = null;
            int lb = nameRaw.IndexOf('['); int rb = lb >= 0 ? nameRaw.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb)
            {
                modifier = nameRaw.Substring(lb + 1, rb - lb - 1).Trim();
            }
            var verMatch = Regex.Match(nameRaw, "\\((\\d+)\\)");
            if (verMatch.Success && int.TryParse(verMatch.Groups[1].Value, out var vParsed)) versionNumber = vParsed;
            string name = nameRaw;
            name = Regex.Replace(name, "\\s*\\[[^\\]]*\\]\\s*", " ");
            name = Regex.Replace(name, "\\(\\d+\\)", "");
            name = Regex.Replace(name, "\\s{2,}", " ").Trim();
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
            try { rows = updateCmd.ExecuteNonQuery(); } catch (System.Exception) { throw; }
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
            try { progress?.Invoke(processed, total); } catch (System.Exception) { throw; }
        }
        return new CsvUpdateResult(updated, inserted, errors, dbPath);
    }

    // Split a semicolon-delimited CSV line honoring quotes. Removes surrounding quotes and unescapes doubled quotes.
    private static string[] SplitSemicolonCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped double quote inside quoted field
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip next
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ';')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        result.Add(sb.ToString());
        // Trim whitespace and normalize fields
        for (int i = 0; i < result.Count; i++)
        {
            result[i] = result[i].Trim();
        }
        return result.ToArray();
    }

    // Returns true if the modifier indicates a promo/reprint that should be ignored when it would cause a conflict
    private static bool IsIgnorableModifier(string? modifier)
    {
        if (string.IsNullOrWhiteSpace(modifier)) return false;
        var m = modifier.Trim();
        if (m.StartsWith("Reprint", StringComparison.OrdinalIgnoreCase)) return true;
        switch (m.ToLowerInvariant())
        {
            case "datestamped promo":
            case "stamped promo":
            case "date stamped":
            case "planeswalker":
            case "serialized":
            case "booster":
            case "booster foil":
            case "surge foil":
            case "double rainbow":
                return true;
        }
        return false;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Importing;

public record CsvUpdateResult(int Updated, int Inserted, int Errors, string DatabasePath);
public record CsvMtgsMapResult(int UpdatedMtgsIds, int InsertedNew, int SkippedExisting, int Conflicts, int Errors, string DatabasePath, string? UnmatchedLogPath);

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
                                            // Fix any legacy rows that incorrectly stored internal Cards.Id instead of MtgsId
                                            try
                                            {
                                                using var fix = conCol.CreateCommand();
                                                fix.CommandText = @"UPDATE CollectionCards SET CardId=@mtgs WHERE CardId=@internal AND NOT EXISTS (SELECT 1 FROM CollectionCards WHERE CardId=@mtgs)";
                                                fix.Parameters.AddWithValue("@mtgs", mtgsId);
                                                fix.Parameters.AddWithValue("@internal", foundId.Value);
                                                fix.ExecuteNonQuery();
                                            }
                                            catch (System.Exception) { throw; }
                                            using (var u = conCol.CreateCommand())
                                            {
                                                u.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id"; // CardId is MtgsId
                                                u.Parameters.AddWithValue("@q", qtyVal.Value);
                                                u.Parameters.AddWithValue("@id", mtgsId);
                                                try { affected = u.ExecuteNonQuery(); } catch (System.Exception) { throw; }
                                            }
                                            if (affected == 0)
                                            {
                                                using var ins = conCol.CreateCommand();
                                                ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                                                ins.Parameters.AddWithValue("@id", mtgsId);
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
                                                    vc.Parameters.AddWithValue("@id", mtgsId);
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
                                                    vc2.Parameters.AddWithValue("@id", mtgsId);
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
                updateLines.Add(line + $" | UPDATE id={foundId.Value} MtgsId={mtgsId}" + modNote + $"; origQty={(qtyMoveNote.Contains("moved Qty=")?"moved":(qtyMoveNote.Contains("FAILED")?"failed":(qtyMoveNote.Contains("missing")?"missing":"unknown")))}" + qtyMoveNote);
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
        updateLines.Add($"# QTY MOVE SUMMARY success={qtyMoveSuccess} fail={qtyMoveFail} skipped_no_qty={qtyMoveSkippedNoQty} attempted={qtyMoveAttempted} preExistingMtgs={preExistingMtgs}");

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

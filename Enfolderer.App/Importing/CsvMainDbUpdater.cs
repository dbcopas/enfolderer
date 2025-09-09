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
    public static CsvMtgsMapResult ProcessMtgsMapping(string csvPath, string? explicitDbPath = null, bool dryRun = true, bool insertMissing = false, Action<int,int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath required");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found", csvPath);
        string dbPath = explicitDbPath ?? Path.Combine(Path.GetDirectoryName(csvPath) ?? string.Empty, "mainDb.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found in CSV folder", dbPath);

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

        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();

        // Detect column names
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Cards)";
            using var r = pragma.ExecuteReader();
            while (r.Read()) { try { cols.Add(r.GetString(1)); } catch { } }
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
                            try { int idx = rr.GetOrdinal("version"); if (idx >= 0) foundVersion = rr.IsDBNull(idx) ? null : rr.GetInt32(idx); } catch { }
                        }
                        if (hasModifierCol)
                        {
                            try { int idxm = rr.GetOrdinal("modifier"); if (idxm >= 0) foundModifier = rr.IsDBNull(idxm) ? null : rr.GetString(idxm); } catch { }
                        }
                    }
                    catch { }
                }
            }

            if (foundId.HasValue)
            {
                // If MtgsId empty/null/0 -> update; else conflict unless equal
                string? existingVal = foundMtgs?.ToString();
                bool isEmpty = string.IsNullOrWhiteSpace(existingVal) || existingVal == "0";
                // If CSV provided a modifier and DB modifier differs or is null, update modifier on match (not considered a new entry)
                bool modifierNeedsUpdate = hasModifierCol && !string.IsNullOrWhiteSpace(csvModifier) && !string.Equals(foundModifier ?? string.Empty, csvModifier ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
                            // If the mainDb row has a Qty, move it to mtgstudio.collection and clear mainDb Qty
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
                                catch { }
                                if (qtyVal.HasValue)
                                {
                                    try
                                    {
                                        // Determine collection path: prefer alongside mainDb, else exe directory
                                        string dbDir = Path.GetDirectoryName(dbPath) ?? string.Empty;
                                        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        string[] candidates = new[] { Path.Combine(dbDir, "mtgstudio.collection"), Path.Combine(exeDir, "mtgstudio.collection") };
                                        string? collectionPath = candidates.FirstOrDefault(File.Exists);
                                        if (!string.IsNullOrEmpty(collectionPath))
                                        {
                                            using var conCol = new SqliteConnection($"Data Source={collectionPath}");
                                            conCol.Open();
                                            int affected = 0;
                                            using (var u = conCol.CreateCommand())
                                            {
                                                u.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                                                u.Parameters.AddWithValue("@q", qtyVal.Value);
                                                u.Parameters.AddWithValue("@id", foundId.Value);
                                                try { affected = u.ExecuteNonQuery(); } catch { affected = 0; }
                                            }
                                            if (affected == 0)
                                            {
                                                using var ins = conCol.CreateCommand();
                                                ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                                                ins.Parameters.AddWithValue("@id", foundId.Value);
                                                ins.Parameters.AddWithValue("@q", qtyVal.Value);
                                                var added = DateTime.Now.ToString("s").Replace('T', ' ');
                                                ins.Parameters.AddWithValue("@added", added);
                                                try { ins.ExecuteNonQuery(); } catch { }
                                            }
                                            // Clear mainDb Qty
                                            try
                                            {
                                                using var clr = con.CreateCommand();
                                                clr.CommandText = $"UPDATE Cards SET Qty=NULL WHERE {idCol}=@id";
                                                clr.Parameters.AddWithValue("@id", foundId.Value);
                                                clr.ExecuteNonQuery();
                                            }
                                            catch { }
                                            qtyMoveNote = $"; moved Qty={qtyVal.Value} to collection and cleared mainDb";
                                        }
                                    }
                                    catch { }
                                }
                            }
                string modNote = modifierNeedsUpdate ? $"; set modifier=[{csvModifier}]" : string.Empty;
                updateLines.Add(line + $" | UPDATE id={foundId.Value} MtgsId={mtgsId}" + modNote + qtyMoveNote);
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
                    catch { }
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
                        }
                        else
                        {
                            updateLines.Add(line + $" | WOULD UPDATE id={foundId} modifier=[{csvModifier}] (same MtgsId)");
                        }
                    }
                    else
                    {
                        skippedExisting++; skippedLines.Add(line + $" | SKIPPED same MtgsId={mtgsId} at id={foundId}");
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
            try { progress?.Invoke(processed, total); } catch { }
        }

        // Write categorized logs (suffix with dryrun/apply)
        try
        {
            string dir = Path.GetDirectoryName(csvPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(csvPath) + ".mtgs";
            string stage = dryRun ? "dryrun" : "apply";
            string PathFor(string label) => Path.Combine(dir, $"{baseName}-{label}.{stage}.txt");
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
            // Backward-compatible unmatched path points to unmatched inserts log
            unmatchedLogPath = unmatchedInsertLines.Count > 0 ? PathFor("unmatched-inserts") : null;
        }
        catch { }

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
                catch { }
            }
        }
        catch { }

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
            try { progress?.Invoke(processed, total); } catch { }
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

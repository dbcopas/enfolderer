using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App;

public sealed class AutoImportMissingSetsService
{
    public sealed record AutoImportResult(int TotalMissing, int ImportedWithNewInserts, IReadOnlyList<string> MissingSets);

    public async Task<AutoImportResult> AutoImportAsync(HashSet<string> binderSetCodes, string dbPath, Func<string,string>? status, bool confirm, Func<string, bool>? confirmPrompt, IStatusSink sink)
    {
        if (binderSetCodes == null || binderSetCodes.Count == 0) { sink.SetStatus("No set codes in binder."); return new AutoImportResult(0,0,Array.Empty<string>()); }
        if (!File.Exists(dbPath)) { sink.SetStatus("mainDb.db not found in collection folder."); return new AutoImportResult(0,0,Array.Empty<string>()); }
        var missing = new List<string>();
        try
        {
            using var conCheck = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conCheck.Open();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = conCheck.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Cards)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) { try { cols.Add(r.GetString(1)); } catch { } }
            }
            string editionCol = cols.Contains("edition") ? "edition" : (cols.Contains("set") ? "set" : "edition");
            foreach (var sc in binderSetCodes.OrderBy(s => s))
            {
                using var cmd = conCheck.CreateCommand();
                cmd.CommandText = $"SELECT 1 FROM Cards WHERE {editionCol}=@e COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@e", sc);
                var val = cmd.ExecuteScalar();
                if (val == null || val == DBNull.Value) missing.Add(sc);
            }
        }
        catch (Exception ex)
        {
            sink.SetStatus("Scan failed: " + ex.Message);
            return new AutoImportResult(0,0,Array.Empty<string>());
        }
        if (missing.Count == 0)
        {
            sink.SetStatus("All binder sets already present in mainDb.");
            return new AutoImportResult(0,0,Array.Empty<string>());
        }
        if (confirm && confirmPrompt != null)
        {
            if (!confirmPrompt(string.Join(", ", missing))) return new AutoImportResult(missing.Count,0,missing);
        }
        int imported = 0;
        foreach (var setCode in missing)
        {
            try
            {
                var result = await ScryfallImportService.ImportSetAsync(setCode, dbPath, forceReimport:false, sink);
                if (result.Inserted > 0) imported++;
            }
            catch (Exception exSet)
            {
                Debug.WriteLine($"[AutoImport] Failed {setCode}: {exSet.Message}");
            }
        }
        sink.SetStatus($"Auto import complete: {imported}/{missing.Count} sets with new inserts.");
        return new AutoImportResult(missing.Count, imported, missing);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Importing; // self
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Importing;

public sealed class AutoImportMissingSetsService
{
    public sealed record AutoImportResult(int TotalMissing, int ImportedWithNewInserts, IReadOnlyList<string> MissingSets);

    public async Task<AutoImportResult> AutoImportAsync(HashSet<string> binderSetCodes, string dbPath, Func<string,string>? status, bool confirm, Func<string, bool>? confirmPrompt, IStatusSink sink)
    {
        if (binderSetCodes == null || binderSetCodes.Count == 0) { sink.SetStatus("No set codes in binder."); return new AutoImportResult(0,0,Array.Empty<string>()); }
        if (!File.Exists(dbPath)) { sink.SetStatus("mainDb.db not found in collection folder."); return new AutoImportResult(0,0,Array.Empty<string>()); }

        // We still identify which sets are totally absent (zero rows) just for reporting, but
        // we now ALWAYS run an import pass for every binder set to supplement partially present sets.
        var totallyMissing = new List<string>();
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
            foreach (var sc in binderSetCodes)
            {
                using var cmd = conCheck.CreateCommand();
                cmd.CommandText = $"SELECT 1 FROM Cards WHERE {editionCol}=@e COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@e", sc);
                var val = cmd.ExecuteScalar();
                if (val == null || val == DBNull.Value) totallyMissing.Add(sc);
            }
        }
        catch (Exception ex)
        {
            sink.SetStatus("Scan failed: " + ex.Message);
            return new AutoImportResult(0,0,Array.Empty<string>());
        }

        if (confirm && confirmPrompt != null)
        {
            // Inform the user all listed sets (not only missing) will be contacted via Scryfall.
            var promptList = string.Join(", ", binderSetCodes.OrderBy(s => s));
            if (!confirmPrompt(promptList)) return new AutoImportResult(totallyMissing.Count,0,totallyMissing);
        }

        int setsWithNewInserts = 0;
        var importer = new ScryfallSetImporter();
        int processed = 0;
        foreach (var setCode in binderSetCodes.OrderBy(s => s))
        {
            try
            {
                sink.SetStatus($"[{++processed}/{binderSetCodes.Count}] {setCode}: importing (supplement mode)...");
                var result = await importer.ImportAsync(setCode, forceReimport:false, dbPath, msg => sink.SetStatus($"{setCode}: {msg}"));
                if (result.Inserted > 0) setsWithNewInserts++;
            }
            catch (Exception exSet)
            {
                Debug.WriteLine($"[AutoImport] Failed {setCode}: {exSet.Message}");
            }
        }
        sink.SetStatus($"Auto import complete: new cards inserted in {setsWithNewInserts}/{binderSetCodes.Count} sets.");
        return new AutoImportResult(totallyMissing.Count, setsWithNewInserts, totallyMissing);
    }
}

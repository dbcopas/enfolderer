using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Importing;

public interface IStatusSink { void SetStatus(string message); }

public record ImportResult(string SetCode, int Inserted, int UpdatedExisting, int Skipped, int Fetched, int? DeclaredCount);

public static class ScryfallImportService
{
    public static async Task<ImportResult> ImportSetAsync(string setCode, string dbPath, bool forceReimport, IStatusSink sink)
    {
        if (string.IsNullOrWhiteSpace(setCode)) throw new ArgumentException("setCode required");
        if (!File.Exists(dbPath)) throw new FileNotFoundException("mainDb.db not found", dbPath);
        using var con = new SqliteConnection($"Data Source={dbPath}");
        await con.OpenAsync();
        long maxId = 0;
        using (var cmd = con.CreateCommand()) { cmd.CommandText = "SELECT IFNULL(MAX(id),0) FROM Cards"; var o = await cmd.ExecuteScalarAsync(); if (o != null && long.TryParse(o.ToString(), out var v)) maxId = v; }
        long nextId = Math.Max(1000000, maxId + 1);
        var cardCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = con.CreateCommand()) { pragma.CommandText = "PRAGMA table_info(Cards)"; using var pr = await pragma.ExecuteReaderAsync(); while (await pr.ReadAsync()) { try { cardCols.Add(pr.GetString(1)); } catch { } } }
        string editionCol = cardCols.Contains("edition") ? "edition" : (cardCols.Contains("set") ? "set" : "edition");
        string collectorCol = cardCols.Contains("collectorNumberValue") ? "collectorNumberValue" : (cardCols.Contains("collectorNumber") ? "collectorNumber" : "collectorNumberValue");
        string? rarityCol = cardCols.Contains("rarity") ? "rarity" : null;
        string? gathererCol = cardCols.Contains("gathererId") ? "gathererId" : (cardCols.Contains("gatherer_id") ? "gatherer_id" : null);
        string? nameCol = cardCols.Contains("name") ? "name" : null;
        if (forceReimport)
        {
            using var del = con.CreateCommand();
            del.CommandText = $"DELETE FROM Cards WHERE {editionCol}=@e COLLATE NOCASE";
            del.Parameters.AddWithValue("@e", setCode);
            await del.ExecuteNonQueryAsync();
        }
        using var http = BinderViewModelHttpFactory.Create();
        sink.SetStatus($"Validating set '{setCode}'...");
        var validateUrl = $"https://api.scryfall.com/sets/{setCode}";
        JsonElement? setJson = null;
        using (var setResp = await http.GetAsync(validateUrl))
        {
            if (setResp.IsSuccessStatusCode)
            {
                try { var bytes = await setResp.Content.ReadAsByteArrayAsync(); using var doc = JsonDocument.Parse(bytes); setJson = doc.RootElement.Clone(); } catch { }
            }
            else { sink.SetStatus($"Set '{setCode}' not found"); return new ImportResult(setCode,0,0,0,0,null); }
        }
        string api = $"https://api.scryfall.com/cards/search?order=set&q=e:{Uri.EscapeDataString(setCode)}&unique=prints";
        int? declaredCount = null;
        if (setJson.HasValue)
        {
            var root = setJson.Value;
            if (root.TryGetProperty("search_uri", out var su) && su.ValueKind == JsonValueKind.String) { var val = su.GetString(); if (!string.IsNullOrWhiteSpace(val)) api = val!; }
            if (root.TryGetProperty("card_count", out var cc) && cc.ValueKind == JsonValueKind.Number && cc.TryGetInt32(out var countVal)) declaredCount = countVal;
        }
        var all = new List<JsonElement>();
        string? page = api;
        while (page != null)
        {
            var resp = await http.GetAsync(page);
            if (!resp.IsSuccessStatusCode) { sink.SetStatus($"Error {(int)resp.StatusCode} fetching {setCode}"); return new ImportResult(setCode,0,0,0,all.Count,declaredCount); }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var c in data.EnumerateArray()) all.Add(c.Clone());
            page = null;
            if (root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True && root.TryGetProperty("next_page", out var np) && np.ValueKind == JsonValueKind.String)
            {
                page = np.GetString();
                sink.SetStatus($"{setCode}: {all.Count}{(declaredCount.HasValue?"/"+declaredCount.Value:"")} fetched...");
            }
        }
        int inserted = 0, updatedExisting = 0, skipped = 0;
        foreach (var card in all)
        {
            try
            {
                string name = card.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                string rarity = card.TryGetProperty("rarity", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;
                string cardSetCode = card.TryGetProperty("set", out var sEl) ? sEl.GetString() ?? setCode : setCode;
                string collector = card.TryGetProperty("collector_number", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
                int? gatherer = null;
                if (card.TryGetProperty("multiverse_ids", out var mIds) && mIds.ValueKind == JsonValueKind.Array)
                    foreach (var mv in mIds.EnumerateArray()) if (mv.ValueKind == JsonValueKind.Number && mv.TryGetInt32(out var mvId)) { gatherer = mvId; break; }
                bool existsRow = false;
                using (var exists = con.CreateCommand())
                {
                    exists.CommandText = $"SELECT {(nameCol??"1")} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE AND {collectorCol}=@n LIMIT 1";
                    exists.Parameters.AddWithValue("@e", cardSetCode);
                    exists.Parameters.AddWithValue("@n", collector);
                    using var er = await exists.ExecuteReaderAsync();
                    if (await er.ReadAsync())
                    {
                        existsRow = true;
                        bool needsUpdate = false;
                        if (nameCol != null)
                        {
                            try { if (!er.IsDBNull(0) && string.IsNullOrWhiteSpace(er.GetString(0)) && !string.IsNullOrWhiteSpace(name)) needsUpdate = true; } catch { }
                        }
                        if (!needsUpdate && (rarityCol != null || gathererCol != null))
                        {
                            using var chk = con.CreateCommand();
                            chk.CommandText = $"SELECT {(rarityCol ?? "NULL")},{(gathererCol ?? "NULL")} FROM Cards WHERE {editionCol}=@e COLLATE NOCASE AND {collectorCol}=@n LIMIT 1";
                            chk.Parameters.AddWithValue("@e", cardSetCode);
                            chk.Parameters.AddWithValue("@n", collector);
                            using var cr = await chk.ExecuteReaderAsync();
                            if (await cr.ReadAsync())
                            {
                                if (rarityCol != null) { try { if (cr.IsDBNull(0) && !string.IsNullOrWhiteSpace(rarity)) needsUpdate = true; } catch { } }
                                if (gathererCol != null && gatherer.HasValue) { int idx = rarityCol != null ? 1 : 0; try { if (cr.IsDBNull(idx)) needsUpdate = true; } catch { } }
                            }
                        }
                        if (needsUpdate)
                        {
                            using var upd = con.CreateCommand();
                            var parts = new List<string>();
                            if (nameCol != null && !string.IsNullOrWhiteSpace(name)) { parts.Add(nameCol + "=@name"); upd.Parameters.AddWithValue("@name", name); }
                            if (rarityCol != null && !string.IsNullOrWhiteSpace(rarity)) { parts.Add(rarityCol + "=@rarity"); upd.Parameters.AddWithValue("@rarity", rarity); }
                            if (gathererCol != null && gatherer.HasValue) { parts.Add(gathererCol + "=@gath"); upd.Parameters.AddWithValue("@gath", gatherer.Value); }
                            if (parts.Count > 0)
                            {
                                upd.CommandText = $"UPDATE Cards SET {string.Join(",", parts)} WHERE {editionCol}=@e COLLATE NOCASE AND {collectorCol}=@n";
                                upd.Parameters.AddWithValue("@e", cardSetCode);
                                upd.Parameters.AddWithValue("@n", collector);
                                try { if (await upd.ExecuteNonQueryAsync() > 0) updatedExisting++; } catch { }
                            }
                        }
                    }
                }
                if (existsRow) { skipped++; continue; }
                using var ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO Cards (id, name, edition, collectorNumberValue, rarity, gathererId, MtgsId) VALUES (@id,@name,@ed,@num,@rar,@gath,NULL)";
                ins.Parameters.AddWithValue("@id", nextId++);
                ins.Parameters.AddWithValue("@name", name);
                ins.Parameters.AddWithValue("@ed", cardSetCode);
                ins.Parameters.AddWithValue("@num", collector);
                ins.Parameters.AddWithValue("@rar", rarity);
                if (gatherer.HasValue) ins.Parameters.AddWithValue("@gath", gatherer.Value); else ins.Parameters.AddWithValue("@gath", DBNull.Value);
                await ins.ExecuteNonQueryAsync();
                inserted++;
            }
            catch { skipped++; }
        }
        return new ImportResult(setCode, inserted, updatedExisting, skipped, all.Count, declaredCount);
    }
}

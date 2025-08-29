using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App;

/// <summary>
/// Loads and indexes local MTG collection databases located beside the binder text file.
/// Expects two files (if present):
///   mainDb.db            (schema has Cards table with cardId, edition, number, modifier, gathererId)
///   mtgstudio.collection (schema has CollectionCards table with CardId, Qty)
/// Provides:
///   MainIndex: (set, collector) -> (cardId, gathererId?)
///   Quantities: (set, collector) -> quantity (only entries with Qty > 0)
/// Safe to call Load multiple times; it will no-op if folder hasn't changed.
/// </summary>
public sealed class CardCollectionData
{
    public Dictionary<(string set,string collector),(int cardId,int? gatherer)> MainIndex { get; } = new(StringTupleComparer.OrdinalIgnoreCase);
    public Dictionary<(string set,string collector), int> Quantities { get; } = new(StringTupleComparer.OrdinalIgnoreCase);

    private string? _loadedFolder;

    public bool IsLoaded => _loadedFolder != null;

    public void Load(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        if (string.Equals(_loadedFolder, folder, StringComparison.OrdinalIgnoreCase)) return; // already loaded

        string mainDbPath = Path.Combine(folder, "mainDb.db");
        string collectionPath = Path.Combine(folder, "mtgstudio.collection");
        if (!File.Exists(mainDbPath) || !File.Exists(collectionPath)) return; // silently ignore if either missing

        MainIndex.Clear();
        Quantities.Clear();

        // 1. Load main card index
        var reverse = new Dictionary<int, List<(string set,string collector)>>();
        try
        {
            using var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly");
            con.Open();

            // Discover actual column names (schema variations between exports)
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = con.CreateCommand())
            {
                // First try canonical 'Cards' then fallback to lowercase 'cards'
                pragma.CommandText = "PRAGMA table_info(Cards)";
                using (var pr = pragma.ExecuteReader())
                {
                    while (pr.Read()) { try { columns.Add(pr.GetString(1)); } catch { }
                    }
                }
                if (columns.Count == 0)
                {
                    pragma.CommandText = "PRAGMA table_info(cards)"; // fallback (provided schema uses lowercase)
                    using var pr2 = pragma.ExecuteReader();
                    while (pr2.Read()) { try { columns.Add(pr2.GetString(1)); } catch { }
                    }
                }
            }

            // Prefer 'id' per provided schema; include legacy/cardId variants.
            string? cardIdCol = FirstExisting(columns, "id", "ID", "cardId", "CardId", "card_id");
            string? editionCol = FirstExisting(columns, "edition", "Edition", "set", "Set", "setCode", "SetCode", "set_code");
            string? numberCol = FirstExisting(columns, "number", "Number", "collectorNumber", "CollectorNumber", "collector_number", "collector_no", "cardNumber"); // raw printed number (may include suffix like 001/284)
            string? numberValueCol = FirstExisting(columns, "collectorNumberValue", "CollectorNumberValue", "collector_number_value", "numberValue", "NumberValue"); // base numeric value for matching
            string? modifierCol = FirstExisting(columns, "modifier", "Modifier", "mod", "Mod", "variant", "Variant", "variation"); // optional
            string? gathererCol = FirstExisting(columns, "gathererId", "GathererId", "gatherer_id", "gatherer", "Gatherer"); // optional

            if (cardIdCol == null || editionCol == null || numberCol == null)
            {
                System.Diagnostics.Debug.WriteLine("[Collection] Main DB missing required columns (id/edition/collectorNumber). Aborting load.");
                return;
            }

            // Build SELECT with aliases so downstream code is stable
            string select = $"SELECT {cardIdCol} AS cardId, {editionCol} AS edition, {numberCol} AS number, " +
                             (modifierCol != null ? $"{modifierCol} AS modifier, " : "NULL AS modifier, ") +
                             (gathererCol != null ? $"{gathererCol} AS gathererId, " : "NULL AS gathererId, ") +
                             (numberValueCol != null ? $"{numberValueCol} AS numberValue " : "NULL AS numberValue ") +
                             "FROM Cards";

            using var cmd = con.CreateCommand();
            cmd.CommandText = select;
            using var r = cmd.ExecuteReader();
            int ordCardId = r.GetOrdinal("cardId");
            int ordEdition = r.GetOrdinal("edition");
            int ordNumber = r.GetOrdinal("number");
            int ordModifier = r.GetOrdinal("modifier");
            int ordGatherer = r.GetOrdinal("gathererId");
            int ordNumberValue = r.GetOrdinal("numberValue");
            while (r.Read())
            {
                int cardId = SafeGetInt(r, ordCardId) ?? -1;
                if (cardId < 0) continue;
                string set = (SafeGetString(r, ordEdition) ?? string.Empty).Trim();
                string number = (SafeGetString(r, ordNumber) ?? string.Empty).Trim(); // printed collector number (may have slash)
                string numberValue = (SafeGetString(r, ordNumberValue) ?? string.Empty).Trim(); // base numeric value
                string modifier = (SafeGetString(r, ordModifier) ?? string.Empty).Trim();
                int? gatherer = SafeGetInt(r, ordGatherer);
                if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(number)) continue;
                // Determine base key: prefer numberValue, fallback to pre-split number
                string baseKey = !string.IsNullOrEmpty(numberValue) ? numberValue : number.Split('/')[0];
                baseKey = baseKey.Trim();
                if (baseKey.Length == 0) baseKey = number; // last resort
                // Only keep modifier in key if it's a token; otherwise drop it to unify variants.
                bool keepModifier = IsTokenModifier(modifier);
                string collectorPrimary = (!keepModifier || string.IsNullOrEmpty(modifier)) ? baseKey : baseKey + modifier;
                // Add primary key
                AddIndexEntry(set, collectorPrimary, cardId, gatherer, reverse);
                // If baseKey has leading zeros, also add trimmed variant to maximize match robustness
                string trimmed = baseKey.TrimStart('0');
                if (trimmed.Length == 0) trimmed = "0"; // handle all-zero
                if (!string.Equals(trimmed, baseKey, StringComparison.Ordinal))
                {
                    string collectorTrim = (!keepModifier || string.IsNullOrEmpty(modifier)) ? trimmed : trimmed + modifier;
                    AddIndexEntry(set, collectorTrim, cardId, gatherer, reverse, allowOverwrite:false);
                }
                if (cardId == 71099)
                {
                    System.Diagnostics.Debug.WriteLine($"[Collection][DEBUG] CardId 71099 set={set} baseKey={baseKey} modifier={modifier} keepModifier={keepModifier} insertedKeys: {collectorPrimary}{(trimmed!=baseKey?", "+collectorTrimIfDifferent(baseKey, trimmed, keepModifier, modifier):string.Empty)}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Collection] Main DB load failed: {ex.Message}");
            return; // abort silently so UI continues
        }

        // 2. Load quantities and map through reverse index
        try
        {
            using var con = new SqliteConnection($"Data Source={collectionPath};Mode=ReadOnly");
            con.Open();
            // Discover schema for CollectionCards
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = con.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(CollectionCards)";
                using var pr = pragma.ExecuteReader();
                while (pr.Read())
                {
                    try { columns.Add(pr.GetString(1)); } catch { }
                }
            }
            string? qtyCardIdCol = FirstExisting(columns, "CardId", "cardId", "card_id", "id", "ID");
            string? qtyCol = FirstExisting(columns, "Qty", "qty", "quantity", "Quantity", "count", "Count");
            if (qtyCardIdCol == null || qtyCol == null)
            {
                System.Diagnostics.Debug.WriteLine("[Collection] CollectionCards missing CardId/Qty columns.");
            }
            else
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT {qtyCardIdCol} AS CardId, {qtyCol} AS Qty FROM CollectionCards WHERE {qtyCol} > 0";
                using var r = cmd.ExecuteReader();
                int ordCardId = r.GetOrdinal("CardId");
                int ordQty = r.GetOrdinal("Qty");
                while (r.Read())
                {
                    int? cardId = SafeGetInt(r, ordCardId);
                    int? qty = SafeGetInt(r, ordQty);
                    if (cardId == null || qty == null || qty <= 0) continue;
                    if (!reverse.TryGetValue(cardId.Value, out var keys)) continue;
                    foreach (var key in keys)
                    {
                        Quantities[key] = qty.Value;
                        // Add base-number alias when collector has numeric prefix + letter suffix (e.g., 270a -> 270)
                        var (setLower, collector) = key;
                        if (!string.IsNullOrEmpty(collector))
                        {
                            int pos = 0;
                            while (pos < collector.Length && char.IsDigit(collector[pos])) pos++;
                            if (pos > 0 && pos < collector.Length) // has digits then suffix
                            {
                                string numericPart = collector.Substring(0, pos);
                                if (!Quantities.ContainsKey((setLower, numericPart)))
                                {
                                    Quantities[(setLower, numericPart)] = qty.Value;
                                    if (cardId == 71099)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Collection][DEBUG] Alias added cardId=71099 {setLower}:{numericPart} -> qty={qty}");
                                    }
                                }
                            }
                        }
                        if (cardId == 71099)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Collection][DEBUG] Qty map cardId=71099 key={key.set}:{key.collector} qty={qty}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Collection] Quantity DB load failed: {ex.Message}");
            // leave already built MainIndex; quantities may remain empty
        }

        _loadedFolder = folder;
        System.Diagnostics.Debug.WriteLine($"[Collection] Loaded: MainIndex={MainIndex.Count} Quantities={Quantities.Count}");
    }

    private void AddIndexEntry(string set, string collector, int cardId, int? gatherer, Dictionary<int, List<(string set,string collector)>> reverse, bool allowOverwrite = true)
    {
        var key = (set.ToLowerInvariant(), collector);
        if (allowOverwrite || !MainIndex.ContainsKey(key))
        {
            MainIndex[key] = (cardId, gatherer);
        }
        if (!reverse.TryGetValue(cardId, out var list))
        {
            list = new();
            reverse[cardId] = list;
        }
        if (!list.Contains(key)) list.Add(key);
    }

    // Case-insensitive tuple comparer for (set, collector) keys
    private sealed class StringTupleComparer : IEqualityComparer<(string a,string b)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string a,string b) x, (string a,string b) y) => 
            string.Equals(x.a, y.a, StringComparison.OrdinalIgnoreCase) && string.Equals(x.b, y.b, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string a,string b) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.a), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.b));
    }

    private static string? FirstExisting(HashSet<string> cols, params string[] candidates)
    {
        foreach (var c in candidates)
            if (cols.Contains(c)) return c;
        return null;
    }

    private static string? SafeGetString(SqliteDataReader r, int ordinal)
    {
        try { if (ordinal >=0 && !r.IsDBNull(ordinal)) return r.GetString(ordinal); } catch { }
        return null;
    }

    private static int? SafeGetInt(SqliteDataReader r, int ordinal)
    {
        try { if (ordinal >=0 && !r.IsDBNull(ordinal)) return r.GetInt32(ordinal); } catch { }
        return null;
    }

    private static bool IsTokenModifier(string modifier)
    {
        if (string.IsNullOrWhiteSpace(modifier)) return false;
        // Treat any modifier containing 'token' (case-insensitive) as token indicator.
        return modifier.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string collectorTrimIfDifferent(string baseKey, string trimmed, bool keepModifier, string modifier)
    {
        if (string.Equals(baseKey, trimmed, StringComparison.Ordinal)) return trimmed;
        if (!keepModifier || string.IsNullOrEmpty(modifier)) return trimmed;
        return trimmed + modifier;
    }
}

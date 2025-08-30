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
    // Variant quantities keyed by (set, baseCollectorNumber, modifier) retaining non-token modifiers
    public Dictionary<(string set,string collector,string modifier), int> VariantQuantities { get; } = new(StringVariantTupleComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string,string[]> ModifierSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        { "JP", new[]{ "jp","jpn","ja","japanese" } },
    { "JPN", new[]{ "jpn","jp","ja","japanese" } },
    { "ART JP", new[]{ "art jp","artjp","alt jp","jp art","jp alt","jp-art","art-jp" } }
    };

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
    VariantQuantities.Clear();
    _cardRows.Clear();

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
            string? numberValueCol = FirstExisting(columns, "collectorNumberValue", "CollectorNumberValue", "collector_number_value", "numberValue", "NumberValue"); // base numeric value for matching
            string? modifierCol = FirstExisting(columns, "modifier", "Modifier", "mod", "Mod", "variant", "Variant", "variation"); // optional
            

            if (cardIdCol == null || editionCol == null)
            {
                System.Diagnostics.Debug.WriteLine("[Collection] Main DB missing required columns (id/edition/collectorNumber). Aborting load.");
                return;
            }

            // Build SELECT with aliases so downstream code is stable
            string select = $"SELECT {cardIdCol} AS cardId, {editionCol} AS edition, " +
                             (modifierCol != null ? $"{modifierCol} AS modifier, " : "NULL AS modifier, ") +
                             (numberValueCol != null ? $"{numberValueCol} AS numberValue " : "NULL AS numberValue ") +
                             "FROM Cards";

            using var cmd = con.CreateCommand();
            cmd.CommandText = select;
            using var r = cmd.ExecuteReader();
            int ordCardId = r.GetOrdinal("cardId");
            int ordEdition = r.GetOrdinal("edition");
            int ordModifier = r.GetOrdinal("modifier");
            int ordNumberValue = r.GetOrdinal("numberValue");
            while (r.Read())
            {
                int cardId = SafeGetInt(r, ordCardId) ?? -1;
                if (cardId < 0) continue;
                string set = (SafeGetString(r, ordEdition) ?? string.Empty).Trim();
                string numberValue = (SafeGetString(r, ordNumberValue) ?? string.Empty).Trim(); // base numeric value
                string modifier = (SafeGetString(r, ordModifier) ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(set)) continue;
                // Determine base key: prefer numberValue, fallback to pre-split number
                string baseKey =  numberValue;
                baseKey = baseKey.Trim();
                // Only keep modifier in key if it's a token; otherwise drop it to unify variants.
                bool keepModifier = IsTokenModifier(modifier);
                string collectorPrimary = (!keepModifier || string.IsNullOrEmpty(modifier)) ? baseKey : baseKey + modifier;
                // Add primary key
                AddIndexEntry(set, collectorPrimary, cardId, reverse);
                // If baseKey has leading zeros, also add trimmed variant to maximize match robustness
                string trimmed = baseKey.TrimStart('0');
                if (trimmed.Length == 0) trimmed = "0"; // handle all-zero
                if (!string.Equals(trimmed, baseKey, StringComparison.Ordinal))
                {
                    string collectorTrim = (!keepModifier || string.IsNullOrEmpty(modifier)) ? trimmed : trimmed + modifier;
                    AddIndexEntry(set, collectorTrim, cardId, reverse, allowOverwrite:false);
                }
                // Record full row including non-token modifier for later variant quantity lookup
                if (!_cardRows.ContainsKey(cardId))
                {
                    _cardRows[cardId] = (set.ToLowerInvariant(), baseKey, modifier); // store baseKey (normalized) + raw modifier
                }
                if (Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1" && string.Equals(set, "WAR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(modifier))
                {
                    System.Diagnostics.Debug.WriteLine($"[Collection][ROW] WAR card row cardId={cardId} baseKey={baseKey} modifier={modifier}");
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
                // Pull all rows (including Qty=0) so variant entries with zero can be recognized explicitly.
                cmd.CommandText = $"SELECT {qtyCardIdCol} AS CardId, {qtyCol} AS Qty FROM CollectionCards";
                using var r = cmd.ExecuteReader();
                int ordCardId = r.GetOrdinal("CardId");
                int ordQty = r.GetOrdinal("Qty");
                while (r.Read())
                {
                    int? cardId = SafeGetInt(r, ordCardId);
                    int? qty = SafeGetInt(r, ordQty);
                    if (cardId == null || qty == null) continue;
                    bool positive = qty > 0;
                    if (!reverse.TryGetValue(cardId.Value, out var keys)) continue;
                    foreach (var key in keys)
                    {
                        if (positive)
                            Quantities[key] = qty.Value; // only store positives in base map
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
                        if (_cardRows.TryGetValue(cardId.Value, out var row))
                        {
                            var (rSet, rCollector, rModifier) = row;
                            if (!string.IsNullOrEmpty(rModifier) && !IsTokenModifier(rModifier))
                            {
                                var modLower = rModifier.ToLowerInvariant();
                                VariantQuantities[(rSet, rCollector, modLower)] = qty.Value; // include zero
                                var trimmedCollector = rCollector.TrimStart('0');
                                if (trimmedCollector.Length == 0) trimmedCollector = "0";
                                if (!string.Equals(trimmedCollector, rCollector, StringComparison.Ordinal))
                                    VariantQuantities[(rSet, trimmedCollector, modLower)] = qty.Value;
                                if (Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1" && rSet == "war")
                                    System.Diagnostics.Debug.WriteLine($"[Collection][VARIANT-ADD] WAR variant collector={rCollector} modifier={modLower} qty={qty.Value}");
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

    private void AddIndexEntry(string set, string collector, int cardId, Dictionary<int, List<(string set,string collector)>> reverse, bool allowOverwrite = true)
    {
        var key = (set.ToLowerInvariant(), collector);
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

    // Internal row cache for variant lookups
    private readonly Dictionary<int,(string set,string collector,string modifier)> _cardRows = new();

    // Variant key comparer
    private sealed class StringVariantTupleComparer : IEqualityComparer<(string a,string b,string c)>
    {
        public static readonly StringVariantTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string a,string b,string c) x, (string a,string b,string c) y) =>
            string.Equals(x.a, y.a, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.b, y.b, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.c, y.c, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string a,string b,string c) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.a),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.b),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.c));
    }

    public bool TryGetVariantQuantity(string set, string collector, string modifier, out int qty)
    {
        qty = 0;
        if (string.IsNullOrWhiteSpace(set) || string.IsNullOrWhiteSpace(collector) || string.IsNullOrWhiteSpace(modifier)) return false;
        return VariantQuantities.TryGetValue((set.ToLowerInvariant(), collector, modifier.ToLowerInvariant()), out qty);
    }

    public bool TryGetVariantQuantityFlexible(string set, string collector, string modifier, out int qty)
    {
        if (TryGetVariantQuantity(set, collector, modifier, out qty)) return true;
        // Try synonym groups
        foreach (var kvp in ModifierSynonyms)
        {
            if (kvp.Value.Any(v => string.Equals(v, modifier, StringComparison.OrdinalIgnoreCase)) || string.Equals(kvp.Key, modifier, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var alt in kvp.Value.Append(kvp.Key).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (TryGetVariantQuantity(set, collector, alt, out qty)) return true;
                }
            }
        }
        return false;
    }
}

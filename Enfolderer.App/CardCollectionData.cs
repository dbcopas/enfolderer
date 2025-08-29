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
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT cardId, edition, number, modifier, gathererId FROM Cards";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int cardId = r.GetInt32(0);
                string set = (r.IsDBNull(1)?"":r.GetString(1)).Trim();
                string number = (r.IsDBNull(2)?"":r.GetString(2)).Trim();
                string modifier = r.IsDBNull(3)?string.Empty:r.GetString(3).Trim();
                int? gatherer = r.IsDBNull(4)? null : r.GetInt32(4);
                if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(number)) continue;
                string collector = string.IsNullOrEmpty(modifier) ? number : number + modifier; // concatenation per observed schema
                var key = (set.ToLowerInvariant(), collector);
                MainIndex[key] = (cardId, gatherer);
                if (!reverse.TryGetValue(cardId, out var list))
                {
                    list = new();
                    reverse[cardId] = list;
                }
                list.Add(key);
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
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT CardId, Qty FROM CollectionCards WHERE Qty > 0";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int cardId = r.GetInt32(0);
                int qty = r.GetInt32(1);
                if (qty <= 0) continue;
                if (!reverse.TryGetValue(cardId, out var keys)) continue;
                foreach (var key in keys)
                {
                    Quantities[key] = qty;
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

    // Case-insensitive tuple comparer for (set, collector) keys
    private sealed class StringTupleComparer : IEqualityComparer<(string a,string b)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string a,string b) x, (string a,string b) y) => 
            string.Equals(x.a, y.a, StringComparison.OrdinalIgnoreCase) && string.Equals(x.b, y.b, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string a,string b) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.a), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.b));
    }
}

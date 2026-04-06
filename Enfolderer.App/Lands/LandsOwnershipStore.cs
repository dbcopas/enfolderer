using System;
using System.Collections.Generic;
using System.IO;

namespace Enfolderer.App.Lands;

/// <summary>
/// Persists land ownership as lines of "SET|COLLECTOR_NUMBER" in a plain text file.
/// </summary>
internal class LandsOwnershipStore
{
    private readonly string _filePath;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);

    public LandsOwnershipStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private static string Key(string set, string collectorNumber) => $"{set}|{collectorNumber}";

    public bool IsOwned(string set, string collectorNumber) => _owned.Contains(Key(set, collectorNumber));

    public bool Toggle(string set, string collectorNumber)
    {
        var key = Key(set, collectorNumber);
        bool nowOwned;
        if (_owned.Contains(key))
        {
            _owned.Remove(key);
            nowOwned = false;
        }
        else
        {
            _owned.Add(key);
            nowOwned = true;
        }
        Save();
        return nowOwned;
    }

    public int OwnedCount => _owned.Count;

    private void Load()
    {
        _owned.Clear();
        if (!File.Exists(_filePath)) return;
        foreach (var line in File.ReadAllLines(_filePath))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                _owned.Add(trimmed);
        }
    }

    private void Save()
    {
        var sorted = new List<string>(_owned);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(_filePath, sorted);
    }
}

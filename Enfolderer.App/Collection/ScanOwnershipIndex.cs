using System.IO;
using Enfolderer.Ai.Contracts;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Collection;

public sealed record ScanOwnership(int PrintingQuantity, int NameQuantity, int RequiredQuantity)
{
    public bool IsMissing => PrintingQuantity < RequiredQuantity;
    public string OverlayText => new CardOwnership(PrintingQuantity, NameQuantity, RequiredQuantity).OverlayText;
}

/// <summary>A read-only inventory snapshot; mapped collection rows take precedence over mainDb Qty.</summary>
public sealed class ScanOwnershipIndex
{
    private sealed record Printing(string Set, string Number, string Name, string Source, int Quantity, int GathererId);
    private readonly List<Printing> _printings = [];
    private IReadOnlyDictionary<string, CollectionPrinting>? _snapshotIndex;

    public ScanOwnershipIndex(string directory)
    {
        using var collection = Open(Path.Combine(directory, "mtgstudio.collection"));
        var quantities = new Dictionary<long, int>();
        using (var command = collection.CreateCommand())
        {
            command.CommandText = "SELECT CardId, SUM(Qty) FROM CollectionCards GROUP BY CardId";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                quantities.Add(reader.GetInt64(0), Math.Max(0, reader.GetInt32(1)));
        }
        using var main = Open(Path.Combine(directory, "mainDb.db"));
        using var cards = main.CreateCommand();
        cards.CommandText = "SELECT id, name, edition, collectorNumberValue, MtgsId, Qty, gathererId FROM Cards";
        using var rows = cards.ExecuteReader();
        while (rows.Read())
        {
            var set = rows.IsDBNull(2) ? "" : rows.GetString(2).Trim();
            var number = rows.IsDBNull(3) ? "" : rows.GetValue(3).ToString()!.Trim();
            var name = rows.IsDBNull(1) ? "" : rows.GetString(1).Trim();
            if (name.Length == 0) continue;
            var id = rows.GetInt64(0);
            var mapped = rows.IsDBNull(4) ? 0 : rows.GetInt64(4);
            var hasCollectionQuantity = mapped > 0 && quantities.TryGetValue(mapped, out _);
            var quantity = hasCollectionQuantity ? quantities[mapped]
                : rows.IsDBNull(5) ? 0 : Math.Max(0, rows.GetInt32(5));
            _printings.Add(new Printing(
                set, number.Length == 0 ? "" : NormalizeNumber(number), NormalizeName(name),
                hasCollectionQuantity ? $"collection:{mapped}" : $"main:{id}", quantity,
                rows.IsDBNull(6) ? 0 : rows.GetInt32(6)));
        }
    }

    public ScanOwnership Resolve(IdentifiedCard card)
    {
        if (card.Game != CardGames.Magic || string.IsNullOrWhiteSpace(card.Set)
            || string.IsNullOrWhiteSpace(card.CollectorNumber))
            throw new InvalidOperationException("Ownership requires an identified Magic printing.");
        var required = RequiredQuantity(card.Prices?.Layout);
        var matches = _printings.Where(p => string.Equals(p.Set, card.Set, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Number, NormalizeNumber(card.CollectorNumber!), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            var legacy = CollectionSnapshot.Resolve(card, _snapshotIndex ??= ExportSnapshot().BuildIndex());
            return new ScanOwnership(legacy.Quantity, legacy.AllQuantity, legacy.RequiredQuantity);
        }
        var names = matches.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = SumSources(matches);
        var all = SumSources(_printings.Where(p => names.Contains(p.Name)));
        return new ScanOwnership(exact, all, required);
    }

    public CollectionSnapshot ExportSnapshot()
    {
        var byName = _printings.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var nameTotals = byName.ToDictionary(g => g.Key, g => SumSources(g.Value), StringComparer.OrdinalIgnoreCase);
        var snapshot = new CollectionSnapshot
        {
            Printings = _printings.Where(p => p.Set.Length > 0 && p.Number.Length > 0)
                .GroupBy(p => CollectionSnapshot.Key(p.Set, p.Number), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var names = group.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var total = names.Length == 1 ? nameTotals[names[0]]
                        : SumSources(names.SelectMany(name => byName[name]));
                    return new CollectionPrinting(group.First().Set, group.First().Number, SumSources(group), total);
                })
                .ToArray(),
            GathererPrintings = _printings.Where(p => p.GathererId > 0)
                .GroupBy(p => p.GathererId)
                .Select(group =>
                {
                    var names = group.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase);
                    return new CollectionGathererPrinting(group.Key, SumSources(group),
                        SumSources(names.SelectMany(name => byName[name])));
                }).ToArray()
        };
        snapshot.Validate();
        return snapshot;
    }

    public static int RequiredQuantity(string? layout) => CollectionSnapshot.RequiredQuantity(layout);

    private static int SumSources(IEnumerable<Printing> rows) =>
        rows.GroupBy(p => p.Source).Sum(group => group.First().Quantity);

    private static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    private static string NormalizeNumber(string number)
    {
        var trimmed = number.Trim().TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static SqliteConnection Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Local ownership database is missing.", path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}

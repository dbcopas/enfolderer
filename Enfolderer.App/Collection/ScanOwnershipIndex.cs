using System.IO;
using Enfolderer.Ai.Contracts;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Collection;

public sealed record ScanOwnership(int PrintingQuantity, int NameQuantity, int RequiredQuantity)
{
    public bool IsMissing => PrintingQuantity < RequiredQuantity;
    public string OverlayText => IsMissing ? "" : $"Owned: {PrintingQuantity} | All: {NameQuantity}";
}

/// <summary>A read-only inventory snapshot; mapped collection rows take precedence over mainDb Qty.</summary>
public sealed class ScanOwnershipIndex
{
    private sealed record Printing(string Set, string Number, string Name, string Source, int Quantity);
    private readonly List<Printing> _printings = [];

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
        cards.CommandText = "SELECT id, name, edition, collectorNumberValue, MtgsId, Qty FROM Cards";
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
                hasCollectionQuantity ? $"collection:{mapped}" : $"main:{id}", quantity));
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
            throw new InvalidOperationException($"Printing {card.Set} {card.CollectorNumber} is absent from the local card database.");
        var names = matches.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = SumSources(matches);
        var all = SumSources(_printings.Where(p => names.Contains(p.Name)));
        return new ScanOwnership(exact, all, required);
    }

    public static int RequiredQuantity(string? layout) => layout?.ToLowerInvariant() switch
    {
        "transform" or "modal_dfc" or "battle" or "double_faced_token" or "double_faced_card" or "reversible_card" => 2,
        "normal" or "split" or "aftermath" or "adventure" or "meld" or "flip" or "leveler"
            or "saga" or "class" or "plane" or "planar" or "scheme" or "vanguard" or "token"
            or "emblem" or "art_series" or "prototype" or "augment" or "host" or "mutate" or "prepare" => 1,
        _ => throw new InvalidOperationException(
            $"Card layout '{layout ?? "(missing)"}' is unavailable or unsupported; ownership requirement cannot be established.")
    };

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

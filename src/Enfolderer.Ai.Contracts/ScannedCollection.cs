using System.Text.Json.Serialization;

namespace Enfolderer.Ai.Contracts;

public sealed record ScannedCollectionPrinting(
    string Set, string Number, string Language, string Name, int Quantity)
{
    [JsonIgnore]
    public string Key => CollectionSnapshot.Key(Set, Number) + ":" + Language.Trim().ToLowerInvariant();
}

/// <summary>One immutable addition per scan, separate from imported ownership snapshots.</summary>
public sealed record ScannedCollectionAddition
{
    [JsonRequired] public string JobId { get; init; } = "";
    [JsonRequired] public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonRequired] public IReadOnlyList<ScannedCollectionPrinting> Printings { get; init; } = [];

    public static bool CanAdd(IdentifiedCard card) =>
        card.Game == CardGames.Magic && card.HasPrinting && string.IsNullOrWhiteSpace(card.Error);

    public static ScannedCollectionAddition FromResult(string jobId, ScanResultDocument result) => new()
    {
        JobId = jobId,
        Printings = Combine(result.Cards.Where(CanAdd).Select(card => new ScannedCollectionPrinting(
            card.Set!.Trim().ToUpperInvariant(), CollectionSnapshot.NormalizeNumber(card.CollectorNumber!),
            card.Language?.Trim().ToLowerInvariant() ?? "", card.Name!.Trim(), 1)))
    };

    public void Validate()
    {
        if (!Guid.TryParseExact(JobId, "N", out _) || AddedAt == default || Printings is null || Printings.Count == 0)
            throw new InvalidDataException("A scanned collection addition requires a scan and identified printings.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var printing in Printings)
        {
            if (printing is null || string.IsNullOrWhiteSpace(printing.Set) || printing.Set.Length > 12
                || !printing.Set.All(char.IsAsciiLetterOrDigit)
                || string.IsNullOrWhiteSpace(printing.Number) || printing.Number.Length > 32
                || printing.Number.Any(char.IsControl) || printing.Name is null
                || string.IsNullOrWhiteSpace(printing.Name) || printing.Name.Length > 500
                || printing.Language is null || printing.Language.Length > 12
                || !printing.Language.All(char.IsAsciiLetter) || printing.Quantity <= 0 || !keys.Add(printing.Key))
                throw new InvalidDataException("Invalid scanned collection printing or quantity.");
        }
    }

    public static IReadOnlyList<ScannedCollectionPrinting> Combine(IEnumerable<ScannedCollectionPrinting> printings)
    {
        var combined = new Dictionary<string, ScannedCollectionPrinting>(StringComparer.OrdinalIgnoreCase);
        foreach (var printing in printings)
            combined[printing.Key] = combined.TryGetValue(printing.Key, out var previous)
                ? previous with { Quantity = checked(previous.Quantity + printing.Quantity) }
                : printing;
        return combined.Values.OrderBy(p => p.Set, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Number, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Language, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

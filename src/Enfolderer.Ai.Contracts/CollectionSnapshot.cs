using System.Text.Json.Serialization;

namespace Enfolderer.Ai.Contracts;

public sealed record CollectionPrinting(
    [property: JsonRequired] string Set,
    [property: JsonRequired] string Number,
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] int AllQuantity);

public sealed record CollectionGathererPrinting(
    [property: JsonRequired] int GathererId,
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] int AllQuantity);

public sealed record CardPrintingReference(string Set, string Number, IReadOnlyList<int> GathererIds);

public sealed record CardOwnership(int Quantity, int AllQuantity, int RequiredQuantity, string? Error = null)
{
    [JsonIgnore] public bool IsMissing => Error is null && Quantity < RequiredQuantity;
    [JsonIgnore] public string OverlayText => Error is not null
        ? "Owned: unavailable\nAll sets: unavailable"
        : $"Owned: {Quantity}\nAll sets: {AllQuantity}";
}

/// <summary>Portable quantities only: no database files, personal notes or card names.</summary>
public sealed record CollectionSnapshot
{
    public const int MaxPrintings = 300_000;
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonRequired] public IReadOnlyList<CollectionPrinting> Printings { get; init; } = [];
    public IReadOnlyList<CollectionGathererPrinting> GathererPrintings { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || ExportedAt == default || Printings is null
            || GathererPrintings is null || Printings.Count + GathererPrintings.Count == 0
            || Printings.Count + GathererPrintings.Count > MaxPrintings)
            throw new InvalidDataException("Invalid or unsupported collection snapshot.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var printing in Printings)
        {
            if (printing is null || string.IsNullOrWhiteSpace(printing.Set) || printing.Set.Length > 12
                || !printing.Set.All(char.IsAsciiLetterOrDigit)
                || string.IsNullOrWhiteSpace(printing.Number) || printing.Number.Length > 32
                || printing.Number.Any(char.IsControl)
                || printing.Quantity < 0 || printing.AllQuantity < printing.Quantity
                || !keys.Add(Key(printing.Set, printing.Number)))
                throw new InvalidDataException("Snapshot has invalid quantities, printing identifiers or duplicate printings.");
        }
        var gathererIds = new HashSet<int>();
        foreach (var printing in GathererPrintings)
        {
            if (printing is null || printing.GathererId <= 0 || printing.Quantity < 0
                || printing.AllQuantity < printing.Quantity || !gathererIds.Add(printing.GathererId))
                throw new InvalidDataException("Snapshot has invalid quantities or duplicate Gatherer printing identifiers.");
        }
    }

    public static string Key(string set, string number) =>
        set.Trim().ToUpperInvariant() + ":" + NormalizeNumber(number).ToUpperInvariant();

    public static string NormalizeNumber(string number)
    {
        var trimmed = number.Trim().TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    public IReadOnlyDictionary<string, CollectionPrinting> BuildIndex()
    {
        var index = Printings.ToDictionary(p => Key(p.Set, p.Number), StringComparer.OrdinalIgnoreCase);
        foreach (var printing in GathererPrintings)
            index.Add(GathererKey(printing.GathererId), new("", "", printing.Quantity, printing.AllQuantity));
        return index;
    }

    public static string GathererKey(int id) => "@gatherer:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static CardOwnership Resolve(IdentifiedCard card, IReadOnlyDictionary<string, CollectionPrinting> index)
    {
        if (card.Game != CardGames.Magic || string.IsNullOrWhiteSpace(card.Set)
            || string.IsNullOrWhiteSpace(card.CollectorNumber))
            throw new InvalidOperationException("Ownership requires an identified Magic printing.");
        var required = RequiredQuantity(card.Prices?.Layout);
        if (!index.TryGetValue(Key(card.Set, card.CollectorNumber), out var printing))
        {
            var matches = (card.Prices?.GathererIds ?? []).Select(GathererKey)
                .Where(index.ContainsKey).Select(key => index[key]).Distinct().ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException("Conflicting quantities for this printing's Gatherer identifiers.");
            printing = matches.SingleOrDefault();
        }
        var all = printing?.AllQuantity ?? 0;
        foreach (var related in card.Prices?.RelatedPrintings ?? [])
        {
            if (index.TryGetValue(Key(related.Set, related.Number), out var sibling))
                all = Math.Max(all, sibling.AllQuantity);
            else
                foreach (var id in related.GathererIds)
                    if (index.TryGetValue(GathererKey(id), out sibling))
                        all = Math.Max(all, sibling.AllQuantity);
        }
        return new CardOwnership(printing?.Quantity ?? 0, all, required);
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
}

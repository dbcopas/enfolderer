namespace Enfolderer.Ai.Contracts;

public sealed class CombinedCollectionOwnership
{
    private readonly IReadOnlyDictionary<string, CollectionPrinting> _uploaded;
    private readonly IReadOnlyDictionary<string, int> _scanned;

    public CombinedCollectionOwnership(CollectionSnapshot? snapshot, IReadOnlyList<ScannedCollectionPrinting>? scanned)
    {
        _uploaded = snapshot?.BuildIndex() ?? new Dictionary<string, CollectionPrinting>();
        _scanned = (scanned ?? []).GroupBy(p => CollectionSnapshot.Key(p.Set, p.Number))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity), StringComparer.OrdinalIgnoreCase);
    }

    public bool HasScanned => _scanned.Count > 0;

    public CardOwnership Resolve(IdentifiedCard card)
    {
        var uploaded = CollectionSnapshot.Resolve(card, _uploaded);
        var key = CollectionSnapshot.Key(card.Set!, card.CollectorNumber!);
        var exact = _scanned.GetValueOrDefault(key);
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        foreach (var printing in card.Prices?.RelatedPrintings ?? [])
            related.Add(CollectionSnapshot.Key(printing.Set, printing.Number));
        var all = related.Sum(k => _scanned.GetValueOrDefault(k));
        return uploaded with
        {
            Quantity = checked(uploaded.Quantity + exact),
            AllQuantity = checked(uploaded.AllQuantity + all)
        };
    }
}

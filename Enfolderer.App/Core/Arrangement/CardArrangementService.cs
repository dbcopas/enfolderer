using System.Collections.Generic;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Core.Arrangement;

/// <summary>
/// Adapter over existing CardListBuilder/VariantPairingService for interface consumption.
/// </summary>
public sealed class CardArrangementService : ICardArrangementService
{
    private readonly CardListBuilder _builder;
    public CardArrangementService(VariantPairingService variantPairing) => _builder = new CardListBuilder(variantPairing);

    public (List<CardEntry> cards, Dictionary<string, string> explicitVariantPairKeys) Build(
        IList<CardSpec> specs,
        IReadOnlyDictionary<int, CardEntry> mfcBacks,
        IList<(string set, string baseNum, string variantNum)> pendingExplicitVariantPairs)
        => _builder.Build(specs, mfcBacks, pendingExplicitVariantPairs);
}

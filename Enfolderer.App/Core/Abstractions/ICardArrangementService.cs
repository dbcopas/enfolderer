using System.Collections.Generic;
using Enfolderer.App.Core;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Arranges raw specs + synthesized backs into card entries and variant pairing map.
/// </summary>
public interface ICardArrangementService
{
    (List<CardEntry> cards, Dictionary<string,string> explicitVariantPairKeys) Build(
        IList<CardSpec> specs,
        IReadOnlyDictionary<int, CardEntry> mfcBacks,
        IList<(string set,string baseNum,string variantNum)> pendingExplicitVariantPairs);
}

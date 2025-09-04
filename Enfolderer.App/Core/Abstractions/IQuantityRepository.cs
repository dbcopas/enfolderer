namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction for quantity persistence (writes to mainDb.db or mtgstudio.collection) separated from UI/service logic.
/// Narrow interface: update custom card quantity (Cards table) or standard collection quantity (CollectionCards table).
/// Implementations should be resilient: swallow exceptions and return null on failure.
/// </summary>
public interface IQuantityRepository
{
    /// <summary>
    /// Update quantity for a custom card stored in mainDb.db (Cards table). Returns persisted quantity or null on failure.
    /// </summary>
    int? UpdateCustomCardQuantity(string mainDbPath, int cardId, int newQty, bool qtyDebug);

    /// <summary>
    /// Upsert quantity for a standard (non-custom) card in mtgstudio.collection (CollectionCards table). Returns persisted quantity or null on failure.
    /// </summary>
    int? UpsertStandardCardQuantity(string collectionFilePath, int cardId, int newQty, bool qtyDebug);
}

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction for collection DB access (load + direct cardId resolve).
/// </summary>
public interface ICollectionRepository
{
    void EnsureLoaded(string? folder);
    int? ResolveCardId(string? folder, string setOriginal, string baseNum, string trimmed);
}

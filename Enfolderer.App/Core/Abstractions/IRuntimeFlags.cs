namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction for runtime / diagnostic flags. Allows services to be tested without mutating environment variables.
/// </summary>
public interface IRuntimeFlags
{
    bool QtyDebug { get; }
    bool CacheDebug { get; }
}

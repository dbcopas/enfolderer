using System;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Core;

/// <summary>
/// Default runtime flag provider. Reads env vars lazily and caches values for the process lifetime.
/// </summary>
public sealed class RuntimeFlags : IRuntimeFlags
{
    private readonly Lazy<bool> _qtyDebug = new(() => Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1");
    private readonly Lazy<bool> _cacheDebug = new(() => Environment.GetEnvironmentVariable("ENFOLDERER_CACHE_DEBUG") == "1");
    public bool QtyDebug => _qtyDebug.Value;
    public bool CacheDebug => _cacheDebug.Value;
    public static IRuntimeFlags Default { get; } = new RuntimeFlags();
}

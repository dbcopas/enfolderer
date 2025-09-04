namespace Enfolderer.App.Core.Logging;

/// <summary>
/// Central string constants for log categories (keeps typos out of call sites).
/// </summary>
public static class LogCategories
{
    public const string QtyEnrich = "QtyEnrich";
    public const string QtyVariant = "QtyVariant";
    public const string QtyCoordinator = "QtyCoordinator";
    public const string QuantityRepoStd = "QuantityRepo.Std";
    public const string QuantityRepoCustom = "QuantityRepo.Custom";
    public const string Collection = "Collection";
    public const string CollectionDebug = "Collection.Debug";
    public const string CollectionWarn = "Collection.Warn";
    public const string Cache = "Cache";
    public const string CacheDiag = "Cache.Diag";
    public const string ResolveSpecs = "ResolveSpecs";
    public const string SpecFetch = "SpecFetch";
    public const string Binder = "Binder";
    public const string UI = "UI";
    public const string CardSlot = "CardSlot";
    public const string Mfc = "MFC";
    public const string Import = "Import";
    public const string Layout = "Layout";
}

/// <summary>
/// Global log host for static/legacy contexts (e.g., CardSlot) without DI path.
/// </summary>
public static class LogHost
{
    public static Enfolderer.App.Core.Abstractions.ILogSink? Sink { get; set; }
}

/// <summary>
/// No-op log sink (used in tests to suppress output).
/// </summary>
public sealed class NullLogSink : Enfolderer.App.Core.Abstractions.ILogSink
{
    public void Log(string message, string? category = null) { /* intentionally blank */ }
}
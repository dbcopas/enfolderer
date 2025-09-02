namespace Enfolderer.App.Core;

// Extracted from MainWindow.xaml.cs (Phase 1 refactor).
// Retains original lowercase primary constructor parameter names to avoid touching existing usages.
public record CardSpec(string setCode, string number, string? overrideName, bool explicitEntry, string? numberDisplayOverride = null)
{
    public CardEntry? Resolved { get; set; }
}

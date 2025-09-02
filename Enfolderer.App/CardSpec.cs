using Enfolderer.App;

namespace Enfolderer.App;

// Extracted from MainWindow.xaml.cs (Phase 1 refactor).
// Retains original lowercase primary constructor parameter names to avoid touching existing usages.
internal record CardSpec(string setCode, string number, string? overrideName, bool explicitEntry, string? numberDisplayOverride = null)
{
    public CardEntry? Resolved { get; set; }
}

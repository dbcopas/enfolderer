using System;

namespace Enfolderer.App.Infrastructure;

public class StatusPanelService
{
    private readonly Action<string>? _onUpdate;
    public StatusPanelService(Action<string>? onUpdate) { _onUpdate = onUpdate; }
    public void Update(string? latest, Action<string> setApiStatus)
    {
        if (!string.IsNullOrEmpty(latest)) setApiStatus(latest!);
        _onUpdate?.Invoke(latest ?? string.Empty);
    }
}

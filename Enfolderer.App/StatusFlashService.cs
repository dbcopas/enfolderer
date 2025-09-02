using System;
using System.Threading;
using System.Threading.Tasks;

namespace Enfolderer.App;

/// <summary>
/// Encapsulates transient status flash messages with automatic timeout & cancellation of prior flashes.
/// </summary>
public class StatusFlashService
{
    private CancellationTokenSource? _cts;

    public void Flash(string message, TimeSpan duration, Action<string> setStatus)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        setStatus(message);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(duration, cts.Token);
                if (!cts.IsCancellationRequested)
                    setStatus(string.Empty);
            } catch (TaskCanceledException) { }
        });
    }
}

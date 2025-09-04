using System.Diagnostics;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Infrastructure;

/// <summary>
/// Simple log sink forwarding to Debug.WriteLine. Future: replace with structured logging.
/// </summary>
public sealed class DebugLogSink : ILogSink
{
    public void Log(string message, string? category = null)
    {
        var stamp = System.DateTime.UtcNow.ToString("HH:mm:ss.fff");
        var tid = System.Environment.CurrentManagedThreadId;
        if (string.IsNullOrEmpty(category)) Debug.WriteLine($"[{stamp} t{tid}] {message}");
        else Debug.WriteLine($"[{stamp} t{tid}] [{category}] {message}");
    }
}

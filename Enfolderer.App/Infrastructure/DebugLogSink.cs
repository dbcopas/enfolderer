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
        if (string.IsNullOrEmpty(category)) Debug.WriteLine(message);
        else Debug.WriteLine("["+category+"] " + message);
    }
}

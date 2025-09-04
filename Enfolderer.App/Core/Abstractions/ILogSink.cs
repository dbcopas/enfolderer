namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Minimal logging abstraction to decouple direct Debug.WriteLine calls.
/// Category is optional; implementation may prepend it.
/// </summary>
public interface ILogSink
{
    void Log(string message, string? category = null);
}

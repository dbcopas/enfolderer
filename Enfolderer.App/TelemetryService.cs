using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Enfolderer.App;

/// <summary>
/// Centralizes lightweight HTTP/metadata telemetry previously embedded in BinderViewModel.
/// Provides counters and a transient status line (ApiStatus) via callback.
/// </summary>
public class TelemetryService
{
    private readonly Action<string> _statusSetter;
    private readonly bool _debugEnabled;

    public TelemetryService(Action<string> statusSetter, bool debugEnabled)
    { _statusSetter = statusSetter; _debugEnabled = debugEnabled; }

    private static readonly object _httpLogLock = new();
    private static int _httpInFlight = 0; private static int _http404 = 0; private static int _http500 = 0;
    private static readonly ConcurrentDictionary<string,string> _imageUrlNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static string HttpLogPath => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "http-log.txt");

    public void Start(string url)
    {
        Interlocked.Increment(ref _httpInFlight);
        Log($"[{DateTime.UtcNow:O}] REQ {url}");
        _statusSetter(ShortLabel(url));
    }
    public void Done(string url, int status, long ms)
    {
        Interlocked.Decrement(ref _httpInFlight);
        if (status==404) Interlocked.Increment(ref _http404); else if (status==500) Interlocked.Increment(ref _http500);
        Log($"[{DateTime.UtcNow:O}] RESP {status} {ms}ms {url}");
        _statusSetter(ShortLabel(url));
    }
    public void SetImageUrlName(string url, string name)
    { if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(name)) _imageUrlNameMap[url] = name; }

    private void Log(string line)
    { if (!_debugEnabled) return; try { lock(_httpLogLock) { Directory.CreateDirectory(ImageCacheStore.CacheRoot); File.AppendAllText(HttpLogPath, line+Environment.NewLine); } } catch { } }

    private string ShortLabel(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            if (url.Contains("/cards/", StringComparison.OrdinalIgnoreCase)) return url;
            if (_imageUrlNameMap.TryGetValue(url, out var name)) return $"img: {name}";
            var u = new Uri(url);
            var last = u.Segments.Length>0 ? u.Segments[^1].Trim('/') : url;
            if (last.Length>40) last = last[..40];
            return last;
        }
        catch { return url; }
    }
}

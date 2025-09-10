using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Imaging;

namespace Enfolderer.App.Infrastructure;

/// <summary>
/// Provides a factory to create HttpClient instances consistent with BinderViewModel's internal client.
/// Uses reflection to reuse the private CreateClient method when available.
/// </summary>
internal static class BinderViewModelHttpFactory
{
    public static HttpClient Create()
    {
        try
        {
            var mi = typeof(BinderViewModel).GetMethod("CreateClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi != null && mi.Invoke(null, null) is HttpClient existing) return existing;
        }
    catch (System.Exception) { throw; }
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer/0.1 (+https://github.com/dbcopas/enfolderer)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }
}

/// <summary>
/// Simple token bucket style limiter to keep API calls below provider thresholds.
/// </summary>
internal static class ApiRateLimiter
{
    private const int Limit = 9; // strictly less than 10 per second
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly System.Collections.Generic.Queue<DateTime> Timestamps = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task WaitAsync()
    {
        while (true)
        {
            await Gate.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                while (Timestamps.Count > 0 && now - Timestamps.Peek() > Window)
                    Timestamps.Dequeue();
                if (Timestamps.Count < Limit)
                {
                    Timestamps.Enqueue(now);
                    return;
                }
                var waitMs = (int)Math.Ceiling((Window - (now - Timestamps.Peek())).TotalMilliseconds);
                if (waitMs < 1) waitMs = 1;
                _ = Task.Run(async () => { await Task.Delay(waitMs); });
            }
            finally
            {
                Gate.Release();
            }
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Central HTTP external logging helper writing simple timestamped lines to cache folder.
/// </summary>
internal static class HttpHelper
{
    private static readonly object _extHttpLogLock = new();
    public static void LogHttpExternal(string phase, string url, int? status = null, long? ms = null)
    {
        try
        {
            var path = Path.Combine(ImageCacheStore.CacheRoot, "http-log.txt");
            Directory.CreateDirectory(ImageCacheStore.CacheRoot);
            var ts = DateTime.UtcNow.ToString("O");
            string line = status.HasValue
                ? $"[{ts}] {phase} {status.Value} {(ms ?? 0)}ms {url}"
                : $"[{ts}] {phase} {url}";
            lock (_extHttpLogLock)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
    catch (System.Exception) { throw; }
    }
}

/// <summary>
/// Runtime feature / diagnostic flags. Central so multiple classes can coordinate behavior without new dependencies.
/// </summary>
internal static class AppRuntimeFlags
{
    /// <summary>
    /// When true, CardSlot image fetching is skipped (used by SELF_TESTS to avoid early HTTP calls at startup).
    /// </summary>
    public static volatile bool DisableImageFetching;
}

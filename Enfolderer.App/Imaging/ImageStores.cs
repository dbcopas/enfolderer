using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Enfolderer.App.Imaging;

/// <summary>
/// Disk-backed bitmap cache keyed by a logical image key (usually "url|face").
/// Files are stored under %LOCALAPPDATA%/Enfolderer/cache with a SHA256 filename.
/// </summary>
public static class ImageCacheStore
{
    public static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string CacheRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Enfolderer", "cache");

    static ImageCacheStore()
    {
        try { Directory.CreateDirectory(CacheRoot); } catch { }
    }

    public static string ImagePathForKey(string key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheRoot, hash + ".img");
    }

    public static bool TryLoadFromDisk(string key, out BitmapImage bmp)
    {
        bmp = null!;
        try
        {
            var path = ImagePathForKey(key);
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                bmp = CreateBitmap(bytes);
                Cache[key] = bmp;
                return true;
            }
        }
        catch { }
        return false;
    }

    public static void PersistImage(string key, byte[] bytes)
    {
        try
        {
            var path = ImagePathForKey(key);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        }
        catch { }
    }

    private static BitmapImage CreateBitmap(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.StreamSource = ms;
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }
}

/// <summary>
/// Stores resolved front/back image URLs per card (setCode + collector number) to avoid
/// refetching metadata when switching faces.
/// </summary>
public static class CardImageUrlStore
{
    private static readonly ConcurrentDictionary<string, (string? front, string? back)> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string setCode, string number) => $"{setCode.ToLowerInvariant()}/{number}";

    public static void Set(string setCode, string number, string? front, string? back)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
        _map[Key(setCode, number)] = (front, back);
    }

    public static (string? front, string? back) Get(string setCode, string number)
    {
        if (_map.TryGetValue(Key(setCode, number), out var v)) return v; return (null, null);
    }
}

/// <summary>
/// In-memory negative cache: tracks (set, number) pairs that returned 404 from Scryfall.
/// Used to avoid hammering the API when a binder entry is invalid. Not persisted across runs.
/// </summary>
public static class NotFoundCardStore
{
    private static readonly ConcurrentDictionary<string, byte> _notFound = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string setCode, string number) => $"{setCode.ToLowerInvariant()}/{number}";

    public static void MarkNotFound(string setCode, string number)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
        _notFound[Key(setCode, number)] = 1;
    }

    public static bool IsNotFound(string setCode, string number)
    {
        return _notFound.ContainsKey(Key(setCode, number));
    }
}

/// <summary>
/// Persists per-card layout type (e.g., transform, split) so UI logic can distinguish
/// true two-sided cards from other multi-face styles.
/// </summary>
public static class CardLayoutStore
{
    private static readonly ConcurrentDictionary<string, string?> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string setCode, string number) => $"{setCode.ToLowerInvariant()}/{number}";

    public static void Set(string setCode, string number, string? layout)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
        _map[Key(setCode, number)] = layout;
    }

    public static string? Get(string setCode, string number)
    {
        _map.TryGetValue(Key(setCode, number), out var v); return v;
    }
}

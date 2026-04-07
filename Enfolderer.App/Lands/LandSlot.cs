using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Enfolderer.App.Imaging;
using Enfolderer.App.Importing;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Core;

namespace Enfolderer.App.Lands;

public class LandSlot : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim FetchGate = new(4);

    public string Name { get; }
    public string CollectorNumber { get; }
    public string Set { get; }
    public string Tooltip { get; }

    private ImageSource? _imageSource;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        private set { _imageSource = value; OnPropertyChanged(); }
    }

    private bool _isOwned;
    public bool IsOwned
    {
        get => _isOwned;
        set
        {
            if (_isOwned != value)
            {
                _isOwned = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OwnershipDisplay));
            }
        }
    }

    public string OwnershipDisplay => _isOwned ? "\u2713" : "";

    public LandSlot(LandEntry entry, bool isOwned)
    {
        Name = entry.Name;
        CollectorNumber = entry.CollectorNumber;
        Set = entry.Set;
        Tooltip = $"{entry.Name} ({entry.Set} #{entry.CollectorNumber})";
        _isOwned = isOwned;
    }

    public async Task TryLoadImageAsync(HttpClient client)
    {
        if (AppRuntimeFlags.DisableImageFetching) return;
        if (string.IsNullOrWhiteSpace(Set) || string.IsNullOrWhiteSpace(CollectorNumber)) return;

        try
        {
            string? imgUrl = null;

            // Check if we already have a cached URL
            var (frontUrl, _) = CardImageUrlStore.Get(Set, CollectorNumber);
            imgUrl = frontUrl;

            if (string.IsNullOrEmpty(imgUrl))
            {
                var apiUrl = ScryfallUrlHelper.BuildCardApiUrl(Set, CollectorNumber);
                await ApiRateLimiter.WaitAsync();
                await FetchGate.WaitAsync();
                HttpResponseMessage resp;
                try { resp = await client.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead); }
                finally { FetchGate.Release(); }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode) return;
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("image_uris", out var imgs) && imgs.TryGetProperty("normal", out var norm))
                        imgUrl = norm.GetString();
                    else if (root.TryGetProperty("image_uris", out imgs) && imgs.TryGetProperty("large", out var large))
                        imgUrl = large.GetString();

                    if (!string.IsNullOrEmpty(imgUrl))
                        CardImageUrlStore.Set(Set, CollectorNumber, imgUrl, null);
                }
            }

            if (string.IsNullOrWhiteSpace(imgUrl)) return;

            // Check memory cache
            var cacheKey = imgUrl + "|front";
            if (ImageCacheStore.Cache.TryGetValue(cacheKey, out var cached)) { ImageSource = cached; return; }
            if (ImageCacheStore.TryLoadFromDisk(cacheKey, out var diskBmp)) { ImageSource = diskBmp; return; }

            // Download
            await ApiRateLimiter.WaitAsync();
            var bytes = await client.GetByteArrayAsync(imgUrl);
            var bmp = CreateFrozenBitmap(bytes);
            ImageSource = bmp;
            ImageCacheStore.Cache[cacheKey] = bmp;
            ImageCacheStore.PersistImage(cacheKey, bytes);
        }
        catch { /* image fetch failures are non-fatal */ }
    }

    private static BitmapImage CreateFrozenBitmap(byte[] data)
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

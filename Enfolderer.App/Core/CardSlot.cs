using System;
using Enfolderer.App.Core; // still needed for CardEntry
using Enfolderer.App.Imaging;
using Enfolderer.App.Infrastructure;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Enfolderer.App.Core.Logging;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Enfolderer.App.Binder;
using Enfolderer.App.Importing;

namespace Enfolderer.App.Core;

public static class CardSlotTheme
{
    private static readonly object _lock = new();
    private static SolidColorBrush _slotBrush = new SolidColorBrush(Color.FromRgb(240,232,210));
    public static SolidColorBrush SlotBrush { get { lock(_lock) return _slotBrush; } }
    public static Color BaseColor { get { lock(_lock) return _slotBrush.Color; } }
    public static void Recalculate(string? seed)
    {
        try
        {
            int hash = seed == null ? Environment.TickCount : seed.GetHashCode(StringComparison.OrdinalIgnoreCase);
            var rnd = new Random(hash ^ 0x5f3759df);
            int baseR = 240, baseG = 232, baseB = 210;
            int r = Clamp(baseR + rnd.Next(-10, 11), 215, 248);
            int g = Clamp(baseG + rnd.Next(-10, 11), 210, 242);
            int b = Clamp(baseB + rnd.Next(-14, 9), 195, 235);
            var c = Color.FromRgb((byte)r,(byte)g,(byte)b);
            var brush = new SolidColorBrush(c);
            if (brush.CanFreeze) brush.Freeze();
            lock(_lock) _slotBrush = brush;
        }
        catch { }
    }
    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}

public class CardSlot : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim FetchGate = new(4);
    public string Name { get; }
    public string Number { get; }
    // Provide EffectiveNumber for bindings (mirrors CardEntry.EffectiveNumber; here Number already stores the effective form)
    public string EffectiveNumber => Number;
    public string Set { get; }
    public string Tooltip { get; }
    public Brush Background { get; }
    public bool IsBackFace { get; }
    public bool IsPlaceholderBack { get; }
    // Global face index (position within _orderedFaces) assigned at construction for search highlighting correlation
    public int GlobalIndex { get; }
    private ImageSource? _imageSource;
    public ImageSource? ImageSource { get => _imageSource; private set { _imageSource = value; OnPropertyChanged(); } }
    private int _quantity;
    public int Quantity { get => _quantity; set { if (_quantity != value) { _quantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(QuantityDisplay)); } } }

    private int? _primaryPairedQuantity;
    private int? _secondaryPairedQuantity;
    public string QuantityDisplay
    {
        get
        {
            if (IsPlaceholderBack || _quantity < 0) return string.Empty;
            // When paired, always display the explicit primary/secondary component quantities even if merged Quantity differs.
            if (_primaryPairedQuantity.HasValue && _secondaryPairedQuantity.HasValue)
            {
                int left = _primaryPairedQuantity.Value;
                int right = _secondaryPairedQuantity.Value;
                return $"{left}({right})";
            }
            return _quantity.ToString();
        }
    }
    private bool _isSearchHighlight;
    public bool IsSearchHighlight { get => _isSearchHighlight; set { if (_isSearchHighlight != value) { _isSearchHighlight = value; OnPropertyChanged(); } } }

    public CardSlot(CardEntry entry, int index)
    {
        Name = entry.Name;
        Number = entry.EffectiveNumber;
        Set = entry.Set ?? string.Empty;
        Tooltip = entry.Display + (string.IsNullOrEmpty(Set) ? string.Empty : $" ({Set})");
        Background = Brushes.Black;
    IsBackFace = entry.IsBackFace;
        IsPlaceholderBack = string.Equals(Set, "__BACK__", StringComparison.OrdinalIgnoreCase) && string.Equals(Name, "Backface", StringComparison.OrdinalIgnoreCase);
        GlobalIndex = index;
        if (IsPlaceholderBack)
            _quantity = -1;
        else
            _quantity = entry.Quantity < 0 ? 0 : entry.Quantity;
        // Capture paired quantities (so UI can display x(y)). We do NOT overwrite _quantity here; merged quantity is only for non-paired display.
        _primaryPairedQuantity = entry.PrimaryPairedQuantity;
        _secondaryPairedQuantity = entry.SecondaryPairedQuantity;
    }

    public CardSlot(string placeholder, int index)
    {
        Name = placeholder;
        Number = string.Empty;
        Set = string.Empty;
        Tooltip = placeholder;
        Background = Brushes.Black;
        _quantity = 0;
        GlobalIndex = index;
    }

    private static Color GenerateColor(int index) => CardSlotTheme.BaseColor;

    public async Task TryLoadImageAsync(HttpClient client, string setCode, string number, bool isBackFace)
    {
        if (AppRuntimeFlags.DisableImageFetching) { return; }
        if (string.Equals(setCode, "__BACK__", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (frontUrl, backUrl) = CardImageUrlStore.Get(setCode, number);
                // System.Diagnostics.Debug.WriteLine($"[CardSlot] Placeholder back fetch mapping front={frontUrl ?? "<null>"} back={backUrl ?? "<null>"}");
                var chosen = isBackFace ? backUrl : frontUrl;
                if (string.IsNullOrWhiteSpace(chosen))
                {
                    chosen = Enfolderer.App.Imaging.CardBackImageService.GetEmbeddedFallback() ?? "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg";
                    // Debug.WriteLine("[CardSlot] Backface mapping missing; using dynamic embedded fallback.");
                }
                else
                {
                    // Debug.WriteLine($"[CardSlot] Using placeholder back image path: {chosen}");
                }
                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    if (chosen.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(chosen, UriKind.Absolute);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            if (bmp.CanFreeze) bmp.Freeze();
                            ImageSource = bmp;
                            return;
                        }
                        catch (Exception exPack)
                        { LogHost.Sink?.Log($"Embedded back image load failed {chosen}: {exPack.Message}", LogCategories.CardSlot); }
                    }
                    else if (File.Exists(chosen))
                    {
                        try
                        {
                            var bytesLocal = File.ReadAllBytes(chosen);
                            var bmpLocal = CreateFrozenBitmap(bytesLocal);
                            ImageSource = bmpLocal;
                        }
                        catch (Exception exLocal)
                        { LogHost.Sink?.Log($"Placeholder local back image load failed {chosen}: {exLocal.Message}", LogCategories.CardSlot); }
                    }
                    else if (Uri.IsWellFormedUriString(chosen, UriKind.Absolute))
                    {
                        try
                        {
                            var bytes = await client.GetByteArrayAsync(chosen);
                            var bmp = CreateFrozenBitmap(bytes);
                            ImageSource = bmp;
                            return;
                        }
                        catch (Exception exRemote)
                        { LogHost.Sink?.Log($"Placeholder remote back fetch failed {chosen}: {exRemote.Message}", LogCategories.CardSlot); }
                    }
                }
                else
                { LogHost.Sink?.Log("Placeholder back has no cached URL mapping.", LogCategories.CardSlot); }
                if (ImageSource == null)
                { LogHost.Sink?.Log("Placeholder back image NOT set (post-attempt).", LogCategories.CardSlot); }
            }
            catch { }
            return;
        }
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
        if (Enfolderer.App.Imaging.NotFoundCardStore.IsNotFound(setCode, number))
        {
            LogHost.Sink?.Log($"Skip image fetch (cached 404) set={setCode} number={number}", LogCategories.CardSlot);
            return;
        }
        if (string.Equals(setCode, "TOKEN", StringComparison.OrdinalIgnoreCase) || string.Equals(number, "TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            LogHost.Sink?.Log($"Skip image fetch for token: set={setCode} number={number}", LogCategories.CardSlot);
            return;
        }
        try
        {
            int faceIndex = isBackFace ? 1 : 0;
            var (frontUrl, backUrl) = CardImageUrlStore.Get(setCode, number);
            string? imgUrl = faceIndex == 0 ? frontUrl : backUrl;
            if (string.IsNullOrEmpty(imgUrl))
            {
                var apiUrl = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
                BinderViewModel.WithVm(vm => vm.FlashMetaUrl(apiUrl));
                LogHost.Sink?.Log($"API fetch {apiUrl} face={faceIndex} (metadata for image URL)", LogCategories.CardSlot);
                await ApiRateLimiter.WaitAsync();
                await FetchGate.WaitAsync();
                HttpResponseMessage resp = null!;
                try { resp = await client.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead); }
                finally { FetchGate.Release(); }
        using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = string.Empty; try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                        LogHost.Sink?.Log($"API status {(int)resp.StatusCode} {resp.ReasonPhrase} GET {apiUrl} Body: {body}", LogCategories.CardSlot);
            if ((int)resp.StatusCode == 404) { try { Enfolderer.App.Imaging.NotFoundCardStore.MarkNotFound(setCode, number); } catch { } }
                        return;
                    }
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var root = doc.RootElement;
                    string? front = null; string? back = null;
                    if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array && faces.GetArrayLength() >= 2)
                    {
                        var f0 = faces[0]; var f1 = faces[1];
                        if (f0.TryGetProperty("image_uris", out var f0Imgs) && f0Imgs.TryGetProperty("normal", out var f0Norm)) front = f0Norm.GetString(); else if (f0.TryGetProperty("image_uris", out f0Imgs) && f0Imgs.TryGetProperty("large", out var f0Large)) front = f0Large.GetString();
                        if (f1.TryGetProperty("image_uris", out var f1Imgs) && f1Imgs.TryGetProperty("normal", out var f1Norm)) back = f1Norm.GetString(); else if (f1.TryGetProperty("image_uris", out f1Imgs) && f1Imgs.TryGetProperty("large", out var f1Large)) back = f1Large.GetString();
                    }
                    if (front == null && root.TryGetProperty("image_uris", out var singleImgs) && singleImgs.TryGetProperty("normal", out var singleNorm)) front = singleNorm.GetString();
                    if (front == null && root.TryGetProperty("image_uris", out singleImgs) && singleImgs.TryGetProperty("large", out var singleLarge)) front = singleLarge.GetString();
                    CardImageUrlStore.Set(setCode, number, front, back);
                    imgUrl = faceIndex == 0 ? front : back;
                }
            }
            if (string.IsNullOrWhiteSpace(imgUrl)) { LogHost.Sink?.Log("No cached or fetched image URL.", LogCategories.CardSlot); return; }
            if (File.Exists(imgUrl))
            {
                try
                {
                    var bytesLocal = File.ReadAllBytes(imgUrl);
                    var bmpLocal = CreateFrozenBitmap(bytesLocal);
                    ImageSource = bmpLocal;
                    return;
                }
                catch (Exception exLocal)
                { LogHost.Sink?.Log($"Local image load failed {imgUrl}: {exLocal.Message}", LogCategories.CardSlot); }
            }
            var cacheKey = imgUrl + (isBackFace ? "|back" : "|front");
            if (ImageCacheStore.Cache.TryGetValue(cacheKey, out var cachedBmp)) { ImageSource = cachedBmp; return; }
            if (ImageCacheStore.TryLoadFromDisk(cacheKey, out var diskBmp)) { ImageSource = diskBmp; return; }
            await ApiRateLimiter.WaitAsync();
            try { BinderViewModel.WithVm(vm => BinderViewModel.SetImageUrlName(imgUrl, Name)); } catch { }
            BinderViewModel.WithVm(vm => vm.FlashImageFetch(Name));
            var bytes = await client.GetByteArrayAsync(imgUrl);
            try
            {
                var bmp2 = CreateFrozenBitmap(bytes);
                ImageSource = bmp2;
                ImageCacheStore.Cache[cacheKey] = bmp2;
                ImageCacheStore.PersistImage(cacheKey, bytes);
            }
            catch (Exception exBmp)
            { LogHost.Sink?.Log($"Bitmap create failed: {exBmp.Message}", LogCategories.CardSlot); }
        }
        catch (Exception ex)
    { LogHost.Sink?.Log($"Image fetch failed {setCode} {number}: {ex.Message}", LogCategories.CardSlot); }
    }

    private static BitmapImage CreateFrozenBitmap(byte[] data)
    {
        using var ms = new MemoryStream(data, writable:false);
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
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

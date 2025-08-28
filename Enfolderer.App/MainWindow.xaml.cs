using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Enfolderer.App;

public partial class MainWindow : Window
{
    private readonly BinderViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new BinderViewModel();
        DataContext = _vm;
    }

    private async void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Collection Text File",
            Filter = "All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                await _vm.LoadFromFileAsync(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm == null) { base.OnPreviewMouseWheel(e); return; }
        try
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            int delta = e.Delta; // >0 wheel up, <0 wheel down
            if (shift)
            {
                if (delta > 0)
                {
                    if (_vm.PrevBinderCommand.CanExecute(null)) _vm.PrevBinderCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextBinderCommand.CanExecute(null)) _vm.NextBinderCommand.Execute(null);
                }
            }
            else
            {
                if (delta > 0)
                {
                    if (_vm.PrevCommand.CanExecute(null)) _vm.PrevCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextCommand.CanExecute(null)) _vm.NextCommand.Execute(null);
                }
            }
            e.Handled = true; // prevent default scroll (there's no scroll viewer anyway)
        }
        finally
        {
            base.OnPreviewMouseWheel(e);
        }
    }
}

public record CardEntry(string Name, string Number, string? Set, bool IsModalDoubleFaced, bool IsBackFace = false, string? FrontRaw = null, string? BackRaw = null, string? DisplayNumber = null)
{
    public string EffectiveNumber => DisplayNumber ?? Number;
    public string Display => string.IsNullOrWhiteSpace(EffectiveNumber) ? Name : $"{EffectiveNumber} {Name}";
    public static CardEntry FromCsv(string line)
    {
        // Format: name;number;set(optional);flags(optional)  (Only first 3 considered now). MFC indicated by name suffix "|MFC" or a trailing ;MFC field.
        if (string.IsNullOrWhiteSpace(line)) throw new ArgumentException("Empty line"); 
        var raw = line.Split(';');
        if (raw.Length < 2) throw new ArgumentException("Must have at least name;number");
        string name = raw[0].Trim();
        string number = raw[1].Trim();
        string? set = raw.Length >= 3 ? raw[2].Trim() : null;
        bool mfc = false;
        string? front = null;
        string? back = null;
        // New required MFC syntax: FRONT/BACK|MFC in name field
    if (name.Contains("|MFC", StringComparison.OrdinalIgnoreCase) || name.Contains("|DFC", StringComparison.OrdinalIgnoreCase))
        {
            mfc = true;
            var markerIndex = name.LastIndexOf('|');
            var pairPart = name.Substring(0, markerIndex).Trim();
            var splitNames = pairPart.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (splitNames.Length == 2)
            {
                front = splitNames[0];
                back = splitNames[1];
        // Display rule (updated): show front name then back in parentheses
        name = $"{front} ({back})";
            }
            else
            {
                // Fallback keep original prior to marker
                name = pairPart;
            }
        }
        else
        {
            // Also allow trailing fields specifying MFC (legacy) but not primary here
            for (int i = 3; i < raw.Length; i++)
            {
                var f = raw[i].Trim();
                if (string.Equals(f, "MFC", StringComparison.OrdinalIgnoreCase) || string.Equals(f, "DFC", StringComparison.OrdinalIgnoreCase))
                    mfc = true;
            }
        }
        return new CardEntry(name, number, string.IsNullOrWhiteSpace(set) ? null : set, mfc, false, front, back);
    }
}

public static class ImageCacheStore
{
    public static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string CacheRoot { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Enfolderer", "cache");
    static ImageCacheStore()
    {
        try { Directory.CreateDirectory(CacheRoot); } catch { }
    }
    public static string ImagePathForKey(string key)
    {
        // key is url|face; hash it for filename safety
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return System.IO.Path.Combine(CacheRoot, hash + ".img");
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
}

// Stores image URLs (front/back) per card so CardSlot image loader can avoid redundant metadata fetches.
public static class CardImageUrlStore
{
    private static readonly ConcurrentDictionary<string,(string? front,string? back)> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string setCode, string number) => $"{setCode.ToLowerInvariant()}/{number}";
    public static void Set(string setCode, string number, string? front, string? back)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
    _map[Key(setCode, number)] = (front, back);
    }
    public static (string? front,string? back) Get(string setCode, string number)
    {
        if (_map.TryGetValue(Key(setCode, number), out var v)) return v; return (null,null);
    }
}

// Stores layout per card for persistence (used to distinguish true double-sided vs split/aftermath cards)
public static class CardLayoutStore
{
    private static readonly ConcurrentDictionary<string,string?> _map = new(StringComparer.OrdinalIgnoreCase);
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

internal static class ApiRateLimiter
{
    private const int Limit = 9; // strictly less than 10 per second
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly Queue<DateTime> Timestamps = new();
    private static readonly SemaphoreSlim Gate = new(1,1);
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
                // Need to wait until earliest timestamp exits window
                var waitMs = (int)Math.Ceiling((Window - (now - Timestamps.Peek())).TotalMilliseconds);
                if (waitMs < 1) waitMs = 1;
                // Release lock before delay to let others observe queue after time passes
                // Use Task.Delay outside lock
                _ = Task.Run(async () => { await Task.Delay(waitMs); });
            }
            finally
            {
                Gate.Release();
            }
            // Small delay before retrying to avoid busy-spin
            await Task.Delay(10);
        }
    }
}

// Computes a single beige tone variation per binder load
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
    private static readonly SemaphoreSlim FetchGate = new(4); // limit concurrent API calls
    public string Name { get; }
    public string Number { get; }
    public string Set { get; }
    public string Tooltip { get; }
    public Brush Background { get; }
    private ImageSource? _imageSource;
    public ImageSource? ImageSource { get => _imageSource; private set { _imageSource = value; OnPropertyChanged(); } }
    public CardSlot(CardEntry entry, int index)
    {
    Name = entry.Name;
    Number = entry.EffectiveNumber;
        Set = entry.Set ?? string.Empty;
    Tooltip = entry.Display + (string.IsNullOrEmpty(Set) ? string.Empty : $" ({Set})");
    Background = Brushes.Black;
    }
    public CardSlot(string placeholder, int index)
    {
        Name = placeholder;
        Number = string.Empty;
        Set = string.Empty;
        Tooltip = placeholder;
    Background = Brushes.Black;
    }
    // Retained for potential future per-slot variation (unused now)
    private static Color GenerateColor(int index) => CardSlotTheme.BaseColor;

    public async Task TryLoadImageAsync(HttpClient client, string setCode, string number, bool isBackFace)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return;
        if (string.Equals(setCode, "TOKEN", StringComparison.OrdinalIgnoreCase) || string.Equals(number, "TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"[CardSlot] Skip image fetch for token: set={setCode} number={number}");
            return;
        }
        try
        {
            int faceIndex = isBackFace ? 1 : 0;
            // Try existing cached URLs first
            var (frontUrl, backUrl) = CardImageUrlStore.Get(setCode, number);
            string? imgUrl = faceIndex == 0 ? frontUrl : backUrl;
            if (string.IsNullOrEmpty(imgUrl))
            {
                // Fetch metadata once to populate image URLs
                var apiUrl = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
                BinderViewModel.WithVm(vm => vm.FlashMetaUrl(apiUrl));
                Debug.WriteLine($"[CardSlot] API fetch {apiUrl} face={faceIndex} (metadata for image URL)");
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
                        Debug.WriteLine($"[CardSlot] API status {(int)resp.StatusCode} {resp.ReasonPhrase} Body: {body}");
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
            if (string.IsNullOrWhiteSpace(imgUrl)) { Debug.WriteLine("[CardSlot] No cached or fetched image URL."); return; }
            // Support local file path for placeholder backs
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
                {
                    Debug.WriteLine($"[CardSlot] Local image load failed {imgUrl}: {exLocal.Message}");
                }
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
            {
                Debug.WriteLine($"[CardSlot] Bitmap create failed: {exBmp.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CardSlot] Image fetch failed {setCode} {number}: {ex.Message}");
        }
    }

    private static BitmapImage CreateFrozenBitmap(byte[] data)
    {
        using var ms = new MemoryStream(data, writable:false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; // faster & avoids some metadata issues
        bmp.StreamSource = ms;
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BinderViewModel : INotifyPropertyChanged
{
    // Dynamic layout configuration (default 4x3, 40 sides per binder)
    private int _rowsPerPage = 3;
    private int _columnsPerPage = 4;
    public int RowsPerPage { get => _rowsPerPage; set { if (value>0 && value!=_rowsPerPage) { _rowsPerPage = value; OnPropertyChanged(); RecomputeAfterLayoutChange(); } } }
    public int ColumnsPerPage { get => _columnsPerPage; set { if (value>0 && value!=_columnsPerPage) { _columnsPerPage = value; OnPropertyChanged(); RecomputeAfterLayoutChange(); } } }
    public int SlotsPerPage => RowsPerPage * ColumnsPerPage;
    private int _pagesPerBinder = 40; // displayed sides per binder (not physical sheets)
    public int PagesPerBinder { get => _pagesPerBinder; set { if (value>0 && value!=_pagesPerBinder) { _pagesPerBinder = value; OnPropertyChanged(); RebuildViews(); Refresh(); } } }
    private string _layoutMode = "4x3"; // UI selection token
    public string LayoutMode { get => _layoutMode; set { if (!string.Equals(_layoutMode, value, StringComparison.OrdinalIgnoreCase)) { _layoutMode = value; OnPropertyChanged(); ApplyLayoutModeToken(); } } }
    private void ApplyLayoutModeToken()
    {
        switch (_layoutMode.ToLowerInvariant())
        {
            case "3x3": RowsPerPage = 3; ColumnsPerPage = 3; break;
            case "2x2": RowsPerPage = 2; ColumnsPerPage = 2; break;
            default: RowsPerPage = 3; ColumnsPerPage = 4; _layoutMode = "4x3"; OnPropertyChanged(nameof(LayoutMode)); break;
        }
    }
    private void RecomputeAfterLayoutChange()
    {
        BuildOrderedFaces();
        RebuildViews();
        Refresh();
    }
    private readonly List<CardEntry> _cards = new();
    private readonly List<CardEntry> _orderedFaces = new(); // reordered faces honoring placement constraints
    private readonly List<CardSpec> _specs = new(); // raw specs in file order
    private readonly ConcurrentDictionary<int, CardEntry> _mfcBacks = new(); // synthetic back faces keyed by spec index
    private readonly List<PageView> _views = new(); // sequence of display views across all binders
    // Explicit pair keys (e.g. base number + language variant) -> enforced pair placement regardless of name differences
    private readonly Dictionary<CardEntry,string> _explicitPairKeys = new();
    // Pending variant pairs captured during parse before resolution (set, baseNumber, variantNumber)
    private readonly List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs = new();
    private int _currentViewIndex = 0;
    private string? _currentCollectionDir; // directory of currently loaded collection file
    private string? _localBackImagePath; // cached resolved local back image path (or null if not found)
    private static readonly HttpClient Http = CreateClient();
    private class HttpLoggingHandler : DelegatingHandler
    {
        public HttpLoggingHandler(HttpMessageHandler inner) : base(inner) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var sw = Stopwatch.StartNew();
            HttpStart(url);
            try
            {
                var resp = await base.SendAsync(request, cancellationToken);
                sw.Stop();
                HttpDone(url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
                return resp;
            }
            catch (Exception)
            {
                sw.Stop();
                HttpDone(url, -1, sw.ElapsedMilliseconds);
                throw;
            }
        }
    }
    private static HttpClient CreateClient()
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var c = new HttpClient(new HttpLoggingHandler(sockets));
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer/0.1");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/yourrepo)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }
    private string? ResolveLocalBackImagePath(bool logIfMissing)
    {
        // Candidate file names (allow some common variations)
        var names = new[]
        {
            "Magic_card_back.jpg",
            "magic_card_back.jpg",
            "card_back.jpg",
            "back.jpg",
            "Magic_card_back.jpeg",
            "Magic_card_back.png"
        };
        // Candidate directories to search (in order)
        var dirs = new List<string?>
        {
            _currentCollectionDir,
            AppContext.BaseDirectory,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Enfolderer"),
            Directory.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "images")) ? System.IO.Path.Combine(AppContext.BaseDirectory, "images") : null
        };
        foreach (var d in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                foreach (var n in names)
                {
                    var candidate = System.IO.Path.Combine(d!, n);
                    if (File.Exists(candidate))
                    {
                        Debug.WriteLine($"[BackImage] Using local back image: {candidate}");
                        return candidate;
                    }
                }
            }
            catch { }
        }
        if (logIfMissing)
        {
            Debug.WriteLine("[BackImage] Local back image not found. Searched directories: " + string.Join(";", dirs.Where(d=>!string.IsNullOrWhiteSpace(d))) + " names: " + string.Join(',', names));
        }
        return null;
    }
    // Removed per refactor (ImageCacheStore used instead)

    public ObservableCollection<CardSlot> LeftSlots { get; } = new();
    public ObservableCollection<CardSlot> RightSlots { get; } = new();

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }
    private string _status = "Ready";

    public string PageDisplay
    {
        get => _pageDisplay;
        private set { _pageDisplay = value; OnPropertyChanged(); }
    }
    private string _pageDisplay = "Page 1";

    private Brush _binderBackground = Brushes.Black;
    public Brush BinderBackground { get => _binderBackground; private set { _binderBackground = value; OnPropertyChanged(); } }
    private readonly List<Brush> _customBinderBrushes = new();
    private readonly List<Brush> _generatedRandomBinderBrushes = new();
    private readonly Random _rand = new();

    // HTTP instrumentation fields
    private static int _httpInFlight;
    private static int _http404;
    private static int _http500;
    private static bool _debugHttpLogging = false;
    private static WeakReference<BinderViewModel>? _instanceRef;
    private static CancellationTokenSource? _apiFlashCts;
    private static readonly ConcurrentDictionary<string,string> _imageUrlNameMap = new(StringComparer.OrdinalIgnoreCase);
    private string _apiStatus = string.Empty; // transient left side text for image/metadata fetch (short form)
    public string ApiStatus { get => _apiStatus; private set { _apiStatus = value; OnPropertyChanged(); } }
    private string _httpPanel = string.Empty; // right side combined panel (latest call + counters)
    public string HttpPanel { get => _httpPanel; private set { _httpPanel = value; OnPropertyChanged(); } }
    private static readonly object _httpLogLock = new();
    private static string HttpLogPath => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "http.log");
    private static void RegisterInstance(BinderViewModel vm) => _instanceRef = new WeakReference<BinderViewModel>(vm);
    public static void WithVm(Action<BinderViewModel> act) { if (_instanceRef!=null && _instanceRef.TryGetTarget(out var vm)) { try { act(vm); } catch { } } }
    private static string CountersString() { int error = _http404 + _http500; return $"InFlight={_httpInFlight} Err={error}"; }
    private string? _currentHttpLabel;
    private void UpdatePanel(string? latest = null)
    {
        var counters = CountersString();
        if (!string.IsNullOrEmpty(latest))
        {
            _currentHttpLabel = latest;
            HttpPanel = $"{latest}  |  {counters}";
            // schedule expiration after 2s if no new label arrives
            var captured = latest;
            _ = Task.Run(async () => {
                try { await Task.Delay(2000); } catch { return; }
                if (captured == _currentHttpLabel) // still the latest
                {
                    Application.Current?.Dispatcher?.Invoke(() => {
                        if (captured == _currentHttpLabel)
                        {
                            _currentHttpLabel = null;
                            HttpPanel = counters = CountersString(); // refresh counters only
                        }
                    });
                }
            });
        }
        else
        {
            _currentHttpLabel = null;
            HttpPanel = counters;
        }
    }
    public void FlashImageFetch(string cardName)
    {
        try {
            _apiFlashCts?.Cancel();
            var cts = new CancellationTokenSource();
            _apiFlashCts = cts;
            Application.Current?.Dispatcher?.Invoke(() => ApiStatus = $"fetching image for {cardName}");
            _ = Task.Run(async () => { try { await Task.Delay(2000, cts.Token); } catch { return; } if (!cts.IsCancellationRequested) Application.Current?.Dispatcher?.Invoke(() => { if (ReferenceEquals(cts, _apiFlashCts)) ApiStatus = string.Empty; }); });
        } catch { }
    }
    public void FlashMetaUrl(string url)
    {
        try {
            if (string.IsNullOrWhiteSpace(url)) return;
            _apiFlashCts?.Cancel();
            var cts = new CancellationTokenSource();
            _apiFlashCts = cts;
            Application.Current?.Dispatcher?.Invoke(() => ApiStatus = url);
            _ = Task.Run(async () => { try { await Task.Delay(2000, cts.Token); } catch { return; } if (!cts.IsCancellationRequested) Application.Current?.Dispatcher?.Invoke(() => { if (ReferenceEquals(cts, _apiFlashCts)) ApiStatus = string.Empty; }); });
        } catch { }
    }
    private void RefreshSummaryIfIdle() { /* no-op now; counters always separate */ }
    private static void LogHttp(string line)
    { if (!_debugHttpLogging) return; try { lock(_httpLogLock) { Directory.CreateDirectory(ImageCacheStore.CacheRoot); File.AppendAllText(HttpLogPath, line + Environment.NewLine); } } catch { } }
    private static void HttpStart(string url)
    {
        Interlocked.Increment(ref _httpInFlight);
        LogHttp($"[{DateTime.UtcNow:O}] REQ {url}");
        var label = BuildDisplayLabel(url);
        WithVm(vm => vm.UpdatePanel(latest:label));
    }
    private static void HttpDone(string url, int status, long ms)
    {
        Interlocked.Decrement(ref _httpInFlight);
        if (status==404) Interlocked.Increment(ref _http404); else if (status==500) Interlocked.Increment(ref _http500);
        LogHttp($"[{DateTime.UtcNow:O}] RESP {status} {ms}ms {url}");
        var label = BuildDisplayLabel(url);
        WithVm(vm => vm.UpdatePanel(latest:label));
    }
    private static string BuildDisplayLabel(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            if (url.Contains("/cards/", StringComparison.OrdinalIgnoreCase)) return url; // metadata URL full
            if (_imageUrlNameMap.TryGetValue(url, out var name)) return $"img: {name}";
        }
        catch { }
        return ShortenUrl(url);
    }
    public static void SetImageUrlName(string url, string name)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name)) return;
        _imageUrlNameMap[url] = name;
    }
    private static string ShortenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try {
            // If it's a Scryfall card image URL, reduce to last path segment before query
            var u = new Uri(url);
            var last = u.Segments.Length>0 ? u.Segments[^1].Trim('/') : url;
            if (last.Length>40) last = last[..40];
            return last;
        } catch { return url; }
    }

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand NextBinderCommand { get; }
    public ICommand PrevBinderCommand { get; }
    public ICommand JumpToPageCommand { get; }

    private string _jumpBinderInput = "1";
    public string JumpBinderInput { get => _jumpBinderInput; set { _jumpBinderInput = value; OnPropertyChanged(); } }
    private string _jumpPageInput = "1";
    public string JumpPageInput { get => _jumpPageInput; set { _jumpPageInput = value; OnPropertyChanged(); } }

    public BinderViewModel()
    {
    RegisterInstance(this);
    if (Environment.GetEnvironmentVariable("ENFOLDERER_HTTP_DEBUG") == "1") _debugHttpLogging = true;
    NextCommand = new RelayCommand(_ => { _currentViewIndex++; Refresh(); }, _ => _currentViewIndex < _views.Count - 1);
    PrevCommand = new RelayCommand(_ => { _currentViewIndex--; Refresh(); }, _ => _currentViewIndex > 0);
    FirstCommand = new RelayCommand(_ => { _currentViewIndex = 0; Refresh(); }, _ => _currentViewIndex != 0);
    LastCommand = new RelayCommand(_ => { if (_views.Count>0) { _currentViewIndex = _views.Count -1; Refresh(); } }, _ => _views.Count>0 && _currentViewIndex != _views.Count -1);
        NextBinderCommand = new RelayCommand(_ => { JumpBinder(1); }, _ => CanJumpBinder(1));
        PrevBinderCommand = new RelayCommand(_ => { JumpBinder(-1); }, _ => CanJumpBinder(-1));
    JumpToPageCommand = new RelayCommand(_ => JumpToBinderPage(), _ => CanJumpToBinderPage());
        RebuildViews();
        Refresh();
    UpdatePanel();
    }

    private record PageView(int? LeftPage, int? RightPage, int BinderIndex);

    private void RebuildViews()
    {
        _views.Clear();
        // total pages needed based on card faces
        int totalFaces = _orderedFaces.Count;
        int totalPages = (int)Math.Ceiling(totalFaces / (double)SlotsPerPage);
        if (totalPages == 0) totalPages = 1; // at least one page even if empty
        int remaining = totalPages;
        int globalPage = 1;
        int binderIndex = 0;
        while (remaining > 0)
        {
            int pagesInBinder = Math.Min(PagesPerBinder, remaining);
            // Front cover view (page 1 right only)
            _views.Add(new PageView(null, globalPage, binderIndex));
            // Interior spreads
            // pages inside binder: 1..pagesInBinder
            for (int local = 2; local <= pagesInBinder - 1; local += 2)
            {
                int left = globalPage + (local -1) -1; // compute using offsets may be error; simpler: left page number = binder start globalPage + (local-2)
            }
            // Rebuild interior properly
            // Remove incorrectly added spreads (we will rebuild after front)
            _views.RemoveAll(v => v.BinderIndex==binderIndex && v.LeftPage.HasValue && v.RightPage.HasValue && v.LeftPage==null);
            // Add spreads correctly
            int binderStartGlobal = globalPage; // page number corresponding to local 1
            for (int local = 2; local <= pagesInBinder - 1; local += 2)
            {
                int leftPageNum = binderStartGlobal + (local -1);
                int rightPageNum = leftPageNum + 1;
                if (local == pagesInBinder) break; // safety
                if (rightPageNum > binderStartGlobal + pagesInBinder -1) break; // not enough pages for pair
                _views.Add(new PageView(leftPageNum, rightPageNum, binderIndex));
            }
            // Back cover view (last page left only) if more than 1 page in binder
            if (pagesInBinder > 1)
            {
                int lastPageGlobal = binderStartGlobal + pagesInBinder -1;
                _views.Add(new PageView(lastPageGlobal, null, binderIndex));
            }
            // Advance
            globalPage += pagesInBinder;
            remaining -= pagesInBinder;
            binderIndex++;
        }
        if (_currentViewIndex >= _views.Count) _currentViewIndex = _views.Count -1;
    }

    private void JumpBinder(int delta)
    {
        if (_views.Count==0) return;
        var currentBinder = _views[_currentViewIndex].BinderIndex;
        var targetBinder = currentBinder + delta;
        if (targetBinder <0) targetBinder =0;
        int maxBinder = _views[^1].BinderIndex;
        if (targetBinder>maxBinder) targetBinder = maxBinder;
        // Jump to first view of target binder
        int idx = _views.FindIndex(v => v.BinderIndex==targetBinder);
        if (idx>=0) { _currentViewIndex = idx; Refresh(); }
    }
    private bool CanJumpBinder(int delta)
    {
        if (_views.Count==0) return false;
        var currentBinder = _views[_currentViewIndex].BinderIndex;
        int target = currentBinder + delta;
        if (target <0) return false;
        int maxBinder = _views[^1].BinderIndex;
        if (target>maxBinder) return false;
        return true;
    }

    private bool CanJumpToBinderPage()
    {
        if (!int.TryParse(JumpBinderInput, out int binder) || binder <1) return false;
        if (!int.TryParse(JumpPageInput, out int page) || page <1 || page>PagesPerBinder) return false;
        int maxBinder = _views.Count==0?0:_views[^1].BinderIndex +1;
        if (binder>maxBinder) return false;
        return true;
    }
    private void JumpToBinderPage()
    {
        if (!int.TryParse(JumpBinderInput, out int binder) || binder <1) return;
        if (!int.TryParse(JumpPageInput, out int page) || page <1 || page>PagesPerBinder) return;
        int binderIndex = binder -1;
        // Translate binder+local page to global page number
        int globalPage = binderIndex * PagesPerBinder + page;
        // Find a view containing that page
        int idx = _views.FindIndex(v => (v.LeftPage==globalPage) || (v.RightPage==globalPage));
        if (idx>=0)
        {
            _currentViewIndex = idx;
            Refresh();
        }
    }

    public void LoadFromFile(string path)
    {
        var lines = File.ReadAllLines(path);
        _cards.Clear();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue; // comment line
            try
            {
                var entry = CardEntry.FromCsv(line);
                _cards.Add(entry);
                if (entry.IsModalDoubleFaced)
                {
                    // Add a synthetic back side with same number + marker
                    // Build back display: show original front (if parsed) with back in parentheses if we had both
                    string backDisplay;
                    if (!string.IsNullOrWhiteSpace(entry.FrontRaw) && !string.IsNullOrWhiteSpace(entry.BackRaw))
                        backDisplay = $"{entry.BackRaw} ({entry.FrontRaw})";
                    else
                        backDisplay = entry.Name + " (Back)";
                    _cards.Add(new CardEntry(backDisplay, entry.Number, entry.Set, false, true, entry.FrontRaw, entry.BackRaw, entry.DisplayNumber));
                }
            }
            catch
            {
                // ignore malformed lines
            }
        }
        Status = $"Loaded {_cards.Count} faces from file.";
        BuildOrderedFaces();
        _currentViewIndex = 0;
        RebuildViews();
        Refresh();
    }

    // New format loader (async):
    // Lines:
    // # comment
    // =[SETCODE]
    // number;[optional name override]
    // numberStart-numberEnd  (inclusive range) optionally followed by ; prefix for name hints (ignored here)
    public async Task LoadFromFileAsync(string path)
    {
    // Recompute slot theme (seeded by file path + last write ticks for variability when file changes)
    try { var fi = new FileInfo(path); CardSlotTheme.Recalculate(path + fi.LastWriteTimeUtc.Ticks); } catch { CardSlotTheme.Recalculate(path); }
    _currentCollectionDir = System.IO.Path.GetDirectoryName(path);
    _localBackImagePath = null; // reset; will lazily resolve when first placeholder encountered
    var lines = await File.ReadAllLinesAsync(path);
    // Directive: first non-comment line starting with ** can specify colors/layout/pages
    _customBinderBrushes.Clear();
    _generatedRandomBinderBrushes.Clear();
    foreach (var dirLine in lines)
    {
        if (string.IsNullOrWhiteSpace(dirLine)) continue;
        var tl = dirLine.Trim();
        if (tl.StartsWith('#')) continue;
        if (tl.StartsWith("**"))
        {
            var payload = tl.Substring(2).Trim();
            if (!string.IsNullOrEmpty(payload))
            {
                var parts = payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var partRaw in parts)
                {
                    var part = partRaw.Trim();
                    if (part.StartsWith("pages=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(part.Substring(6), out int pages) && pages>0) PagesPerBinder = pages;
                        continue;
                    }
                    if (string.Equals(part, "4x3", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "3x3", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "2x2", StringComparison.OrdinalIgnoreCase))
                    { LayoutMode = part.ToLowerInvariant(); continue; }
                    if (part.Equals("httplog", StringComparison.OrdinalIgnoreCase) || part.Equals("debughttp", StringComparison.OrdinalIgnoreCase))
                    { _debugHttpLogging = true; continue; }
                    if (TryParseColorToken(part, out var b)) _customBinderBrushes.Add(b);
                }
            }
            break; // processed directive (only first considered)
        }
        break; // first non-comment line not directive stops search
    }
    // Compute hash of input file contents for metadata/image cache lookup
    string fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    _currentFileHash = fileHash;
    if (IsCacheComplete(fileHash) && TryLoadMetadataCache(fileHash))
    {
        Status = "Loaded metadata from cache.";
        BuildOrderedFaces();
        _currentViewIndex = 0;
        RebuildViews();
        Refresh();
        // Fire off background image warm for first two pages (slots) if desired later
        return;
    }
    _cards.Clear();
    _specs.Clear();
    _mfcBacks.Clear();
    _orderedFaces.Clear();
        string? currentSet = null;
    var fetchList = new List<(string setCode,string number,string? nameOverride,int specIndex)>();
        // Helper for paired range expansion (primary & secondary ranges of equal length)
        static List<string> ExpandSimpleNumericRange(string text)
        {
            var list = new List<string>();
            text = text.Trim();
            if (string.IsNullOrEmpty(text)) return list;
            if (text.Contains('-'))
            {
                var parts = text.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e) && s <= e)
                {
                    for (int n = s; n <= e; n++) list.Add(n.ToString());
                    return list;
                }
            }
            if (int.TryParse(text, out int single)) list.Add(single.ToString());
            return list;
        }
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith("**")) continue; // skip directive line completely
            if (line.StartsWith('#')) continue;
            // Backface placeholder syntax: "N;backface" => insert N placeholder slots showing MTG card back
            var backfaceMatch = Regex.Match(line, @"^(?<count>\d+)\s*;\s*backface$", RegexOptions.IgnoreCase);
            if (backfaceMatch.Success)
            {
                if (int.TryParse(backfaceMatch.Groups["count"].Value, out int backCount) && backCount > 0)
                {
                    // Resolve local placeholder image path (cached) on first use
                    if (_localBackImagePath == null)
                        _localBackImagePath = ResolveLocalBackImagePath(logIfMissing:true);
                    bool hasLocal = _localBackImagePath != null && File.Exists(_localBackImagePath);
                    for (int bi = 0; bi < backCount; bi++)
                    {
                        var spec = new CardSpec("__BACK__", "BACK", overrideName: null, explicitEntry: true);
                        _specs.Add(spec);
                        var entry = new CardEntry("Backface", "BACK", "__BACK__", false, false, null, null, string.Empty);
                        // Map image URL (front & back same) to local file if present, else fallback to remote standard back.
                        var frontUrl = hasLocal ? _localBackImagePath! : "https://c1.scryfall.com/file/scryfall-card-backs/en.png";
                        CardImageUrlStore.Set("__BACK__", "BACK", frontUrl, frontUrl);
                        _specs[^1] = _specs[^1] with { Resolved = entry };
                    }
                }
                continue; // processed line
            }
            if (line.StartsWith('=') && line.Length>1)
            {
                currentSet = line.Substring(1).Trim();
                continue;
            }
            if (currentSet == null) continue; // ignore until a set code defined
            // Explicit placeholder/card line: Name;SetCode;Number (bypasses API, used for tokens or custom entries)
            // Detect by having at least two semicolons and last segment numeric
            if (line.Count(c => c==';') >= 2)
            {
                var parts = line.Split(';', StringSplitOptions.TrimEntries);
                if (parts.Length >=3)
                {
                    string possibleName = parts[0];
                    string possibleSet = parts[1].ToUpperInvariant();
                    string possibleNumber = parts[2];
                    if (int.TryParse(possibleNumber, out _))
                    {
            _specs.Add(new CardSpec(possibleSet, possibleNumber, overrideName: possibleName, explicitEntry:true));
                        continue;
                    }
                }
            }
            // Range or single
            string? nameOverride = null;
            var semiIdx = line.IndexOf(';');
            string numberPart = line;
            if (semiIdx >=0)
            {
                numberPart = line.Substring(0, semiIdx).Trim();
                nameOverride = line.Substring(semiIdx+1).Trim();
                if (nameOverride.Length==0) nameOverride = null;
            }
            // Support prefixed collector numbers like "RA 1-8" or "GR 5" => RA1..RA8 / GR5
            // Pattern: PREFIX (letters) whitespace startNumber optional - endNumber
            var prefixRangeMatch = Regex.Match(numberPart, @"^(?<pfx>[A-Za-z]{1,8})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
            if (prefixRangeMatch.Success)
            {
                var pfx = prefixRangeMatch.Groups["pfx"].Value.Trim();
                var startStr = prefixRangeMatch.Groups["start"].Value;
                var endGrp = prefixRangeMatch.Groups["end"];
                if (endGrp.Success && int.TryParse(startStr, out int ps) && int.TryParse(endGrp.Value, out int pe) && ps <= pe)
                {
                    for (int n = ps; n <= pe; n++)
                    {
                        var fullNum = pfx + n.ToString();
                        _specs.Add(new CardSpec(currentSet, fullNum, null, false));
                        fetchList.Add((currentSet, fullNum, null, _specs.Count-1));
                    }
                    continue;
                }
                else
                {
                    // Single prefixed number
                    var fullNum = pfx + startStr;
                    _specs.Add(new CardSpec(currentSet, fullNum, nameOverride, false));
                    fetchList.Add((currentSet, fullNum, nameOverride, _specs.Count-1));
                    continue;
                }
            }
            // Paired range syntax: 296-340&&361-405 -> single slots whose NUMBER displays as "296(361)", name remains the fetched card name.
            if (numberPart.Contains("&&"))
            {
                var pairSegs = numberPart.Split("&&", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pairSegs.Length == 2)
                {
                    var primaryList = ExpandSimpleNumericRange(pairSegs[0]);
                    var secondaryList = ExpandSimpleNumericRange(pairSegs[1]);
                    if (primaryList.Count > 0 && primaryList.Count == secondaryList.Count)
                    {
                        for (int i = 0; i < primaryList.Count; i++)
                        {
                            var prim = primaryList[i];
                            var sec = secondaryList[i];
                            var numberDisplay = prim + "(" + sec + ")";
                            // numberDisplayOverride stores the composite number; overrideName stays null so real name is fetched
                            _specs.Add(new CardSpec(currentSet, prim, null, false, numberDisplay));
                            fetchList.Add((currentSet, prim, null, _specs.Count -1));
                        }
                        continue; // processed line
                    }
                }
            }
            // Generalized attached OR spaced prefix + range or single: e.g. J1-5, J1, 2024-0 7-8 (prefix may include digits and hyphens), 2024-07
            // Skip this block if it's a pure numeric range (e.g. 1-44) so later generic range logic handles it.
            bool isPureNumericRange = Regex.IsMatch(numberPart, @"^\d+-\d+$");
            if (!isPureNumericRange)
            {
                // Pattern 1: Attached prefix: <prefix><start>(-<end>)? where prefix must contain at least one letter (avoid treating 1-44 as prefix 1- + 44)
                var attachedPrefixMatch = Regex.Match(numberPart, @"^(?<pfx>(?=.*[A-Za-z])[A-Za-z0-9\-]{1,24}?)(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
                if (attachedPrefixMatch.Success)
                {
                    var pfx = attachedPrefixMatch.Groups["pfx"].Value;
                    var startStr = attachedPrefixMatch.Groups["start"].Value;
                    var endGrp = attachedPrefixMatch.Groups["end"];
                    int width = startStr.Length; // preserve zero padding
                    if (endGrp.Success && int.TryParse(startStr, out int aps) && int.TryParse(endGrp.Value, out int ape) && aps <= ape)
                    {
                        for (int n = aps; n <= ape; n++)
                        {
                            var fullNum = pfx + n.ToString().PadLeft(width, '0');
                            _specs.Add(new CardSpec(currentSet, fullNum, null, false));
                            fetchList.Add((currentSet, fullNum, null, _specs.Count -1));
                        }
                        continue;
                    }
                    else
                    {
                        // Single
                        var fullNum = pfx + startStr;
                        _specs.Add(new CardSpec(currentSet, fullNum, nameOverride, false));
                        fetchList.Add((currentSet, fullNum, nameOverride, _specs.Count -1));
                        continue;
                    }
                }
            }
            // Pattern 2: Spaced general prefix with range or single (expands earlier letter-only rule): <prefix> <start>(-<end>)?
            var spacedPrefixMatch = Regex.Match(numberPart, @"^(?<pfx>[A-Za-z0-9\-]{1,24})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
            if (spacedPrefixMatch.Success)
            {
                var pfx = spacedPrefixMatch.Groups["pfx"].Value;
                var startStr = spacedPrefixMatch.Groups["start"].Value;
                var endGrp = spacedPrefixMatch.Groups["end"];
                int width = startStr.Length;
                if (endGrp.Success && int.TryParse(startStr, out int sps) && int.TryParse(endGrp.Value, out int spe) && sps <= spe)
                {
                    for (int n = sps; n <= spe; n++)
                    {
                        var fullNum = pfx + n.ToString().PadLeft(width, '0');
                        _specs.Add(new CardSpec(currentSet, fullNum, null, false));
                        fetchList.Add((currentSet, fullNum, null, _specs.Count-1));
                    }
                    continue;
                }
                else
                {
                    var fullNum = pfx + startStr;
                    _specs.Add(new CardSpec(currentSet, fullNum, nameOverride, false));
                    fetchList.Add((currentSet, fullNum, nameOverride, _specs.Count-1));
                    continue;
                }
            }
            // Pattern 3: Range with suffix (e.g. 2J-b, 5J-b or 01X etc.)  start-end<suffix> OR start-end <suffix>
            var rangeSuffixMatch = Regex.Match(numberPart, @"^(?<start>\d+)-(?: (?<endSpace>\d+)|(?<end>\d+))(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
            if (rangeSuffixMatch.Success)
            {
                string startStr = rangeSuffixMatch.Groups["start"].Value;
                string endStr = rangeSuffixMatch.Groups["end"].Success ? rangeSuffixMatch.Groups["end"].Value : rangeSuffixMatch.Groups["endSpace"].Value;
                string suffix = rangeSuffixMatch.Groups["suffix"].Value;
                if (int.TryParse(startStr, out int rs) && int.TryParse(endStr, out int re) && rs <= re)
                {
                    int width = startStr.Length;
                    for (int n = rs; n <= re; n++)
                    {
                        var fullNum = n.ToString().PadLeft(width, '0') + suffix;
                        _specs.Add(new CardSpec(currentSet, fullNum, null, false));
                        fetchList.Add((currentSet, fullNum, null, _specs.Count -1));
                    }
                    continue;
                }
            }
            // Pattern 4: Single number with suffix (e.g. 2J-b)
            var singleSuffixMatch = Regex.Match(numberPart, @"^(?<num>\d+)(?<suffix>[A-Za-z][A-Za-z0-9\-]+)$", RegexOptions.Compiled);
            if (singleSuffixMatch.Success)
            {
                var numStr = singleSuffixMatch.Groups["num"].Value;
                var suffix = singleSuffixMatch.Groups["suffix"].Value;
                var fullNum = numStr + suffix;
                _specs.Add(new CardSpec(currentSet, fullNum, nameOverride, false));
                fetchList.Add((currentSet, fullNum, nameOverride, _specs.Count -1));
                continue;
            }
            // Star suffix syntax: "★1-36" expands to 1★,2★,...,36★ (input has leading star, meaning output gets trailing star)
            if (numberPart.StartsWith('★'))
            {
                var starBody = numberPart.Substring(1).Trim();
                if (starBody.Contains('-'))
                {
                    var pieces = starBody.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (pieces.Length == 2 && int.TryParse(pieces[0], out int s) && int.TryParse(pieces[1], out int e) && s <= e)
                    {
                        for (int n=s; n<=e; n++)
                        {
                            var fullNum = n.ToString() + '★';
                            _specs.Add(new CardSpec(currentSet, fullNum, null, false));
                            fetchList.Add((currentSet, fullNum, null, _specs.Count-1));
                        }
                        continue;
                    }
                }
                // Single number with leading star => trailing star number
                if (int.TryParse(starBody, out int singleStar))
                {
                    var fullNum = singleStar.ToString() + '★';
                    _specs.Add(new CardSpec(currentSet, fullNum, nameOverride, false));
                    fetchList.Add((currentSet, fullNum, nameOverride, _specs.Count-1));
                    continue;
                }
            }
            // Interleaving syntax: a line containing "||" splits into multiple segments; we round-robin them.
            if (numberPart.Contains("||", StringComparison.Ordinal))
            {
                var segments = numberPart.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1)
                {
                    var lists = new List<List<string>>();
                    foreach (var seg in segments)
                    {
                        // Each segment can be a range A-B or single C
                        var segPrefixMatch = Regex.Match(seg, @"^(?<pfx>[A-Za-z]{1,8})\s+(?<start>\d+)(?:-(?<end>\d+))?$", RegexOptions.Compiled);
                        if (segPrefixMatch.Success)
                        {
                            var pfx = segPrefixMatch.Groups["pfx"].Value;
                            var sStr = segPrefixMatch.Groups["start"].Value;
                            var eGrp = segPrefixMatch.Groups["end"];
                            if (eGrp.Success && int.TryParse(sStr, out int sNum) && int.TryParse(eGrp.Value, out int eNum) && sNum <= eNum)
                            {
                                var l = new List<string>();
                                for (int n = sNum; n <= eNum; n++) l.Add(pfx + n.ToString());
                                lists.Add(l);
                            }
                            else
                            {
                                lists.Add(new List<string>{ pfx + sStr });
                            }
                        }
                        else if (seg.Contains('-', StringComparison.Ordinal))
                        {
                            var pieces = seg.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (pieces.Length==2 && int.TryParse(pieces[0], out int s) && int.TryParse(pieces[1], out int e) && s<=e)
                            {
                                var l = new List<string>();
                                for (int n=s; n<=e; n++) l.Add(n.ToString());
                                lists.Add(l);
                            }
                        }
                        else if (int.TryParse(seg, out int singleNum))
                        {
                            lists.Add(new List<string>{ singleNum.ToString() });
                        }
                    }
                    if (lists.Count > 0)
                    {
                        bool anyLeft;
                        do
                        {
                            anyLeft = false;
                            foreach (var l in lists)
                            {
                                if (l.Count == 0) continue;
                                _specs.Add(new CardSpec(currentSet, l[0], null, false));
                                fetchList.Add((currentSet, l[0], null, _specs.Count-1));
                                l.RemoveAt(0);
                                if (l.Count > 0) anyLeft = true; // still more in at least one list
                            }
                            // If after removing first elements some lists still have items, loop continues
                            anyLeft = lists.Exists(x => x.Count > 0);
                        } while (anyLeft);
                        continue; // processed this line fully
                    }
                }
            }
            // Range with language variant syntax: N-M+lang => for each number k in [N,M] add k and k/lang variant
            var rangeVariantMatch = Regex.Match(numberPart, @"^(?<start>\d+)-(?:)(?<end>\d+)\+(?<lang>[A-Za-z]{1,8})$", RegexOptions.Compiled);
            if (rangeVariantMatch.Success)
            {
                var startStr = rangeVariantMatch.Groups["start"].Value;
                var endStr = rangeVariantMatch.Groups["end"].Value;
                var lang = rangeVariantMatch.Groups["lang"].Value.ToLowerInvariant();
                if (int.TryParse(startStr, out int rs) && int.TryParse(endStr, out int re) && rs <= re)
                {
                    int padWidth = (startStr.StartsWith('0') && startStr.Length == endStr.Length) ? startStr.Length : 0;
                    for (int k = rs; k <= re; k++)
                    {
                        var baseNum = padWidth>0 ? k.ToString().PadLeft(padWidth,'0') : k.ToString();
                        _specs.Add(new CardSpec(currentSet, baseNum, nameOverride, false));
                        fetchList.Add((currentSet, baseNum, nameOverride, _specs.Count-1));
                        var variantNumber = baseNum + "/" + lang;
                        var variantDisplay = baseNum + " (" + lang + ")";
                        _specs.Add(new CardSpec(currentSet, variantNumber, nameOverride, false, variantDisplay));
                        fetchList.Add((currentSet, variantNumber, nameOverride, _specs.Count-1));
                        try { _pendingExplicitVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch { }
                    }
                    continue;
                }
            }
            if (numberPart.Contains('-', StringComparison.Ordinal))
            {
                var pieces = numberPart.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length==2 && int.TryParse(pieces[0], out int startNum) && int.TryParse(pieces[1], out int endNum) && startNum<=endNum)
                {
                    int padWidth = (pieces[0].StartsWith('0') && pieces[0].Length == pieces[1].Length) ? pieces[0].Length : 0;
                    for (int n=startNum; n<=endNum; n++)
                    {
                        var numStr = padWidth>0 ? n.ToString().PadLeft(padWidth,'0') : n.ToString();
                        _specs.Add(new CardSpec(currentSet, numStr, null, false));
                        fetchList.Add((currentSet, numStr, null, _specs.Count-1));
                    }
                }
                continue;
            }
            // Variant language/extra path segment syntax: N+xx => two entries: N and N/xx (API path segment variant)
            var plusVariantMatch = Regex.Match(numberPart, @"^(?<base>[A-Za-z0-9]+)\+(?<seg>[A-Za-z]{1,8})$", RegexOptions.Compiled);
            if (plusVariantMatch.Success)
            {
                var baseNum = plusVariantMatch.Groups["base"].Value;
                var seg = plusVariantMatch.Groups["seg"].Value.ToLowerInvariant();
                // Base printing
                _specs.Add(new CardSpec(currentSet, baseNum, nameOverride, false));
                fetchList.Add((currentSet, baseNum, nameOverride, _specs.Count-1));
                // Variant printing uses extra path segment (e.g. 804/ja)
                var variantNumber = baseNum + "/" + seg;
                var variantDisplay = baseNum + " (" + seg + ")"; // show language in display number
                _specs.Add(new CardSpec(currentSet, variantNumber, nameOverride, false, variantDisplay));
                fetchList.Add((currentSet, variantNumber, nameOverride, _specs.Count-1));
                // Record explicit pair key (set+baseNum) used later after resolution to enforce pairing
                try { _pendingExplicitVariantPairs.Add((currentSet, baseNum, variantNumber)); } catch { }
                continue;
            }
            // Single number
            var num = numberPart.Trim();
            if (num.Length>0)
            {
                _specs.Add(new CardSpec(currentSet, num, nameOverride, false));
                fetchList.Add((currentSet, num, nameOverride, _specs.Count-1));
            }
        }
        // Lazy initial fetch: just enough for first two pages (current + lookahead)
        int neededFaces = SlotsPerPage * 2; // 24
        var initialSpecIndexes = new HashSet<int>();
        for (int i = 0; i < _specs.Count && initialSpecIndexes.Count < neededFaces; i++) initialSpecIndexes.Add(i);
        await ResolveSpecsAsync(fetchList, initialSpecIndexes);
        RebuildCardListFromSpecs();
        Status = $"Initial load {_cards.Count} faces (placeholders included).";
        BuildOrderedFaces();
        _currentViewIndex = 0;
        RebuildViews();
        Refresh();
        // Background fetch remaining
        _ = Task.Run(async () =>
        {
            var remaining = new HashSet<int>();
            for (int i = 0; i < _specs.Count; i++) if (!initialSpecIndexes.Contains(i)) remaining.Add(i);
            if (remaining.Count == 0) return;
            await ResolveSpecsAsync(fetchList, remaining, updateInterval:15);
            Application.Current.Dispatcher.Invoke(() =>
            {
                RebuildCardListFromSpecs();
                BuildOrderedFaces();
                RebuildViews();
                Refresh();
                Status = $"All metadata loaded ({_cards.Count} faces).";
                PersistMetadataCache(_currentFileHash);
                MarkCacheComplete(_currentFileHash);
            });
        });
    }

    private string? _currentFileHash;
    private const int CacheSchemaVersion = 5; // bump: refined two-sided classification & invalidating prior misclassification cache
    private static readonly HashSet<string> PhysicallyTwoSidedLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"
    };
    private static readonly HashSet<string> SingleFaceMultiLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "split","aftermath","adventure","meld","flip","leveler","saga","class","plane","planar","scheme","vanguard","token","emblem","art_series"
    };
    private record CachedFace(string Name, string Number, string? Set, bool IsMfc, bool IsBack, string? FrontRaw, string? BackRaw, string? FrontImageUrl, string? BackImageUrl, string? Layout, int SchemaVersion);
    private string MetaCacheDir => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "meta");
    private string MetaCachePath(string hash) => System.IO.Path.Combine(MetaCacheDir, hash + ".json");
    private string MetaCacheDonePath(string hash) => System.IO.Path.Combine(MetaCacheDir, hash + ".done");
    private bool IsCacheComplete(string hash) => File.Exists(MetaCacheDonePath(hash));
    // Per-card cache (reused across different file hashes). One JSON file per set+number.
    private string CardCacheDir => System.IO.Path.Combine(MetaCacheDir, "cards");
    private string CardCachePath(string setCode, string number)
    {
        var safeSet = (setCode ?? string.Empty).ToLowerInvariant();
        var safeNum = number.Replace('/', '_').Replace('\\', '_').Replace(':','_');
        return System.IO.Path.Combine(CardCacheDir, safeSet + "-" + safeNum + ".json");
    }
    private record CardCacheEntry(string Set, string Number, string Name, bool IsMfc, string? FrontRaw, string? BackRaw, string? FrontImageUrl, string? BackImageUrl, string? Layout, int SchemaVersion, DateTime FetchedUtc);
    private bool TryLoadCardFromCache(string setCode, string number, out CardEntry? entry)
    {
        entry = null;
        try
        {
            var path = CardCachePath(setCode, number);
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<CardCacheEntry>(json);
            if (data == null) return false;
            if (string.IsNullOrEmpty(data.Layout)) return false; // layout required to classify
            bool physTwoSided = data.Layout != null && PhysicallyTwoSidedLayouts.Contains(data.Layout);
            bool effectiveMfc = data.IsMfc && physTwoSided;
            var ce = new CardEntry(data.Name, data.Number, data.Set, effectiveMfc, false, data.FrontRaw, data.BackRaw, null);
            entry = ce;
            CardImageUrlStore.Set(data.Set, data.Number, data.FrontImageUrl, data.BackImageUrl);
            CardLayoutStore.Set(data.Set, data.Number, data.Layout);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerCardCache] Failed to load {setCode} {number}: {ex.Message}");
            return false;
        }
    }
    private void PersistCardToCache(CardEntry ce)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ce.Set) || string.IsNullOrWhiteSpace(ce.Number)) return;
            Directory.CreateDirectory(CardCacheDir);
            var (frontImg, backImg) = CardImageUrlStore.Get(ce.Set, ce.Number);
            var layout = CardLayoutStore.Get(ce.Set!, ce.Number);
            var data = new CardCacheEntry(ce.Set!, ce.Number, ce.Name, ce.IsModalDoubleFaced && !ce.IsBackFace, ce.FrontRaw, ce.BackRaw, frontImg, backImg, layout, CacheSchemaVersion, DateTime.UtcNow);
            var path = CardCachePath(ce.Set!, ce.Number);
            if (!File.Exists(path)) // do not overwrite (keep earliest)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(data));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerCardCache] Persist failed {ce.Set} {ce.Number}: {ex.Message}");
        }
    }
    private bool TryLoadMetadataCache(string hash)
    {
        try
        {
            var path = MetaCachePath(hash);
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var faces = JsonSerializer.Deserialize<List<CachedFace>>(json);
            if (faces == null || faces.Count == 0) return false;
            // If any face has older schema, invalidate whole file cache
            if (faces.Exists(f => string.IsNullOrEmpty(f.Layout))) return false; // accept older schema versions as long as layout present
            _cards.Clear();
            foreach (var f in faces)
            {
                bool physTwoSided = f.Layout != null && PhysicallyTwoSidedLayouts.Contains(f.Layout);
                bool effectiveMfc = f.IsMfc && physTwoSided && !f.IsBack;
                var ce = new CardEntry(f.Name, f.Number, f.Set, effectiveMfc, f.IsBack, f.FrontRaw, f.BackRaw, null);
                _cards.Add(ce);
                if (!f.IsBack)
                    CardImageUrlStore.Set(f.Set ?? string.Empty, f.Number, f.FrontImageUrl, f.BackImageUrl);
                if (!string.IsNullOrEmpty(f.Layout) && f.Set != null)
                    CardLayoutStore.Set(f.Set, f.Number, f.Layout);
            }
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to load metadata cache: {ex.Message}"); return false; }
    }
    private void PersistMetadataCache(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        try
        {
            Directory.CreateDirectory(MetaCacheDir);
            var list = new List<CachedFace>();
            foreach (var c in _cards)
            {
                var (frontImg, backImg) = CardImageUrlStore.Get(c.Set ?? string.Empty, c.Number);
                var layout = c.Set != null ? CardLayoutStore.Get(c.Set, c.Number) : null;
                list.Add(new CachedFace(c.Name, c.Number, c.Set, c.IsModalDoubleFaced, c.IsBackFace, c.FrontRaw, c.BackRaw, frontImg, backImg, layout, CacheSchemaVersion));
            }
            var json = JsonSerializer.Serialize(list);
            File.WriteAllText(MetaCachePath(hash), json);
            Debug.WriteLine($"[Cache] Wrote metadata cache {hash} faces={list.Count}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to write metadata cache: {ex.Message}"); }
    }

    private void MarkCacheComplete(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        try
        {
            File.WriteAllText(MetaCacheDonePath(hash), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Failed to mark cache complete: {ex.Message}"); }
    }

    private async Task ResolveSpecsAsync(List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList, HashSet<int> targetIndexes, int updateInterval = 5)
    {
        int total = targetIndexes.Count;
        int done = 0;
        var concurrency = new SemaphoreSlim(6);
        var tasks = new List<Task>();
        foreach (var f in fetchList)
        {
            if (!targetIndexes.Contains(f.specIndex)) continue;
            await concurrency.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Attempt per-card cache first (avoid API call) if not already resolved
                    if (_specs[f.specIndex].Resolved == null && TryLoadCardFromCache(f.setCode, f.number, out var cachedEntry) && cachedEntry != null)
                    {
                        _specs[f.specIndex] = _specs[f.specIndex] with { Resolved = cachedEntry };
                        if (cachedEntry.IsModalDoubleFaced && !string.IsNullOrEmpty(cachedEntry.FrontRaw) && !string.IsNullOrEmpty(cachedEntry.BackRaw))
                        {
                            var backDisplay = $"{cachedEntry.BackRaw} ({cachedEntry.FrontRaw})";
                            var backEntry = new CardEntry(backDisplay, cachedEntry.Number, cachedEntry.Set, false, true, cachedEntry.FrontRaw, cachedEntry.BackRaw);
                            _mfcBacks[ f.specIndex ] = backEntry; // idempotent write acceptable
                        }
                        return; // skip network
                    }
                    var ce = await FetchCardMetadataAsync(f.setCode, f.number, f.nameOverride);
                    if (ce != null)
                    {
                        _specs[f.specIndex] = _specs[f.specIndex] with { Resolved = ce };
                        if (ce.IsModalDoubleFaced && !string.IsNullOrEmpty(ce.FrontRaw) && !string.IsNullOrEmpty(ce.BackRaw))
                        {
                            var backDisplay = $"{ce.BackRaw} ({ce.FrontRaw})";
                            var backEntry = new CardEntry(backDisplay, ce.Number, ce.Set, false, true, ce.FrontRaw, ce.BackRaw);
                            _mfcBacks[f.specIndex] = backEntry; // idempotent concurrent write acceptable
                        }
                        PersistCardToCache(ce);
                        if (_mfcBacks.TryGetValue(f.specIndex, out var backFace)) PersistCardToCache(backFace);
                    }
                }
                finally
                {
                    Interlocked.Increment(ref done);
                    if (done % updateInterval == 0 || done == total)
                    {
                        Status = $"Resolving metadata {done}/{total} ({(int)(done*100.0/Math.Max(1,total))}%)";
                    }
                    concurrency.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
    }

    private void RebuildCardListFromSpecs()
    {
        _cards.Clear();
        _explicitPairKeys.Clear();
        for (int i=0;i<_specs.Count;i++)
        {
            var s = _specs[i];
            if (s.Resolved != null)
            {
                var resolved = s.Resolved;
                // Attach display number without altering canonical number used for API/cache
                if (s.numberDisplayOverride != null && resolved.DisplayNumber != s.numberDisplayOverride)
                    resolved = resolved with { DisplayNumber = s.numberDisplayOverride };
                _cards.Add(resolved);
                // After adding card, if it matches a pending variant pair, map base+variant to same pair key
                try
                {
                    foreach (var pending in _pendingExplicitVariantPairs)
                    {
                        if (!string.Equals(pending.set, resolved.Set, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(resolved.Number, pending.baseNum, StringComparison.OrdinalIgnoreCase))
                        {
                            // Locate variant entry if already added later; handled after loop as well
                        }
                    }
                } catch { }
            }
            else
            {
                var placeholderName = s.overrideName ?? s.number; // unresolved: show number placeholder
                var displayNumber = s.numberDisplayOverride; // may be null
                _cards.Add(new CardEntry(placeholderName, s.number, s.setCode, false, false, null, null, displayNumber));
            }
            if (_mfcBacks.TryGetValue(i, out var back))
                _cards.Add(back);
        }
        // Build explicit pair key map now that all resolved/placeholder entries exist
        foreach (var tup in _pendingExplicitVariantPairs)
        {
            CardEntry? baseEntry = _cards.FirstOrDefault(c => string.Equals(c.Set, tup.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, tup.baseNum, StringComparison.OrdinalIgnoreCase));
            CardEntry? varEntry = _cards.FirstOrDefault(c => string.Equals(c.Set, tup.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, tup.variantNum, StringComparison.OrdinalIgnoreCase));
            if (baseEntry != null && varEntry != null)
            {
                string key = $"{tup.set.ToLowerInvariant()}|{tup.baseNum.ToLowerInvariant()}|{tup.variantNum.ToLowerInvariant()}";
                _explicitPairKeys[baseEntry] = key;
                _explicitPairKeys[varEntry] = key;
            }
        }
    }

    private record CardSpec(string setCode, string number, string? overrideName, bool explicitEntry, string? numberDisplayOverride = null)
    {
        public CardEntry? Resolved { get; set; }
    }

    private async Task<CardEntry?> FetchCardMetadataAsync(string setCode, string number, string? overrideName)
    {
        try
        {
            await ApiRateLimiter.WaitAsync();
            var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            string? displayName = overrideName;
            string? frontRaw = null; string? backRaw = null; bool isMfc = false;
            string? frontImg = null; string? backImg = null;
            // Distinguish true two-sided physical cards (transform/modal_dfc/battle/etc) vs split/aftermath/adventure where both halves are on one physical face.
            bool hasRootImageUris = root.TryGetProperty("image_uris", out var rootImgs); // split & similar layouts usually have this
            string? layout = null; if (root.TryGetProperty("layout", out var layoutProp) && layoutProp.ValueKind == JsonValueKind.String) layout = layoutProp.GetString();
            bool isPhysicallyTwoSidedLayout = layout != null && PhysicallyTwoSidedLayouts.Contains(layout);
            bool forcedSingleByLayout = layout != null && SingleFaceMultiLayouts.Contains(layout);
            if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array && faces.GetArrayLength() >= 2)
            {
                var f0 = faces[0]; var f1 = faces[1];
                int faceCount = faces.GetArrayLength();
                bool facesHaveDistinctArt = false;
                try
                {
                    if (f0.TryGetProperty("illustration_id", out var ill0) && f1.TryGetProperty("illustration_id", out var ill1) && ill0.ValueKind==JsonValueKind.String && ill1.ValueKind==JsonValueKind.String && ill0.GetString()!=ill1.GetString())
                        facesHaveDistinctArt = true;
                } catch { }
                bool forceAllTwoSided = Environment.GetEnvironmentVariable("ENFOLDERER_FORCE_TWO_SIDED_ALL_FACES") == "1";
                // Heuristic: treat as two-sided if explicit two-sided layout OR (not a forced-single layout AND (no root image OR distinct art))
                // Fallback: if layout missing/null AND not forced single & exactly 2 faces.
                bool heuristicTwoSided = !forcedSingleByLayout && (isPhysicallyTwoSidedLayout || (!hasRootImageUris) || facesHaveDistinctArt || (layout == null && faceCount == 2));
                bool treatAsTwoSided = isPhysicallyTwoSidedLayout || forceAllTwoSided || heuristicTwoSided;
                if (!treatAsTwoSided && !forcedSingleByLayout)
                {
                    Debug.WriteLine($"[MFC Heuristic] Unexpected single-face classification for {setCode} {number} layout={layout} faces={faceCount} hasRootImgs={hasRootImageUris} distinctArt={facesHaveDistinctArt}");
                }
                if (treatAsTwoSided)
                {
                    frontRaw = f0.TryGetProperty("name", out var f0Name) && f0Name.ValueKind==JsonValueKind.String ? f0Name.GetString() : null;
                    backRaw = f1.TryGetProperty("name", out var f1Name) && f1Name.ValueKind==JsonValueKind.String ? f1Name.GetString() : null;
                    isMfc = true;
                    if (displayName == null) displayName = $"{frontRaw} ({backRaw})";
                    // Face-specific images preferred
                    if (f0.TryGetProperty("image_uris", out var f0Imgs))
                    {
                        if (f0Imgs.TryGetProperty("normal", out var f0Norm) && f0Norm.ValueKind==JsonValueKind.String) frontImg = f0Norm.GetString();
                        else if (f0Imgs.TryGetProperty("large", out var f0Large) && f0Large.ValueKind==JsonValueKind.String) frontImg = f0Large.GetString();
                    }
                    if (f1.TryGetProperty("image_uris", out var f1Imgs))
                    {
                        if (f1Imgs.TryGetProperty("normal", out var f1Norm) && f1Norm.ValueKind==JsonValueKind.String) backImg = f1Norm.GetString();
                        else if (f1Imgs.TryGetProperty("large", out var f1Large) && f1Large.ValueKind==JsonValueKind.String) backImg = f1Large.GetString();
                    }
                    // Fallback to root image if front missing
                    if (frontImg == null && hasRootImageUris)
                    {
                        if (rootImgs.TryGetProperty("normal", out var rootNorm) && rootNorm.ValueKind==JsonValueKind.String) frontImg = rootNorm.GetString();
                        else if (rootImgs.TryGetProperty("large", out var rootLarge) && rootLarge.ValueKind==JsonValueKind.String) frontImg = rootLarge.GetString();
                    }
                    // Additional fallback: if back image missing but Scryfall supplied a single root image (some older layouts), reuse front image so slot isn't blank
                    if (backImg == null && frontImg != null)
                    {
                        backImg = frontImg; // better to show duplicate art than empty slot
                    }
                }
                else
                {
                    // Treat as single-slot multi-face (split/aftermath/etc)
                    if (displayName == null && root.TryGetProperty("name", out var npropSplit) && npropSplit.ValueKind==JsonValueKind.String) displayName = npropSplit.GetString();
                    if (hasRootImageUris)
                    {
                        if (rootImgs.TryGetProperty("normal", out var rootNorm2) && rootNorm2.ValueKind==JsonValueKind.String) frontImg = rootNorm2.GetString();
                        else if (rootImgs.TryGetProperty("large", out var rootLarge2) && rootLarge2.ValueKind==JsonValueKind.String) frontImg = rootLarge2.GetString();
                    }
                    else if (f0.TryGetProperty("image_uris", out var f0Imgs2))
                    {
                        if (f0Imgs2.TryGetProperty("normal", out var f0Norm2) && f0Norm2.ValueKind==JsonValueKind.String) frontImg = f0Norm2.GetString();
                        else if (f0Imgs2.TryGetProperty("large", out var f0Large2) && f0Large2.ValueKind==JsonValueKind.String) frontImg = f0Large2.GetString();
                    }
                }
            }
            else
            {
                if (displayName == null && root.TryGetProperty("name", out var nprop)) displayName = nprop.GetString();
                if (root.TryGetProperty("image_uris", out var singleImgs) && singleImgs.TryGetProperty("normal", out var singleNorm)) frontImg = singleNorm.GetString();
                else if (root.TryGetProperty("image_uris", out singleImgs) && singleImgs.TryGetProperty("large", out var singleLarge)) frontImg = singleLarge.GetString();
            }
            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"{number}"; // fallback
            CardImageUrlStore.Set(setCode, number, frontImg, backImg);
            CardLayoutStore.Set(setCode, number, layout);
            return new CardEntry(displayName!, number, setCode, isMfc, false, frontRaw, backRaw);
        }
        catch { return null; }
    }

    private void BuildOrderedFaces()
    {
        _orderedFaces.Clear();
        if (_cards.Count == 0) return;
        // In 3x3 layout we disable pair grouping (MFC adjacency + duplicate-name pairing) and
        // preserve the natural sequence (front then back already injected during load).
        if (string.Equals(LayoutMode, "3x3", StringComparison.OrdinalIgnoreCase))
        {
            _orderedFaces.AddRange(_cards);
            return;
        }
        // Precompute cards that belong to duplicate name runs of length >=3 so we disable forced pairing for them.
        var longRunCards = new HashSet<CardEntry>();
        int iRun = 0;
        while (iRun < _cards.Count)
        {
            var c = _cards[iRun];
            if (c != null && !c.IsModalDoubleFaced && !c.IsBackFace)
            {
                string name = (c.Name ?? string.Empty).Trim();
                int j = iRun + 1;
                while (j < _cards.Count)
                {
                    var n = _cards[j];
                    if (n == null || n.IsModalDoubleFaced || n.IsBackFace) break;
                    if (!string.Equals(name, (n.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) break;
                    j++;
                }
                int runLen = j - iRun;
                if (runLen >= 3)
                {
                    for (int k = iRun; k < j; k++) longRunCards.Add(_cards[k]);
                }
                iRun = j;
                continue;
            }
            iRun++;
        }
        // Work on a queue (list) of indices; we will remove as we schedule.
        var remaining = new List<CardEntry>(_cards); // copy
        int globalSlot = 0;
        while (remaining.Count > 0)
        {
            int col = (globalSlot % SlotsPerPage) % ColumnsPerPage; // column within current row

            bool IsPairStart(List<CardEntry> list, int idx)
            {
                if (idx < 0 || idx >= list.Count) return false;
                var c = list[idx];
                if (c == null)
                {
                    Debug.WriteLine($"[BuildOrderedFaces] Null entry at index {idx} in remaining list (IsPairStart). Treating as single.");
                    return false;
                }
        bool IsNazgul(CardEntry ce) => string.Equals(ce.Name?.Trim(), "Nazgûl", StringComparison.OrdinalIgnoreCase);
                bool IsBackPlaceholder(CardEntry ce) => string.Equals(ce.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                // MFC front + back
                if (c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
                {
                    var next = list[idx + 1];
                    if (next != null && next.IsBackFace) return true;
                }
                // Explicit variant pair (base + language variant) irrespective of name match
                if (_explicitPairKeys.Count > 0)
                {
                    if (idx + 1 < list.Count && c != null && _explicitPairKeys.TryGetValue(c, out var key1))
                    {
                        var n2 = list[idx + 1];
                        if (n2 != null && _explicitPairKeys.TryGetValue(n2, out var key2) && key1 == key2)
                        {
                            return true;
                        }
                    }
                }
                // Duplicate pair (exactly two identical names, excluding long runs)
                if (!c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
                {
                    var n = list[idx + 1];
                    if (n != null && !n.IsModalDoubleFaced && !n.IsBackFace)
                    {
                        // Treat consecutive backface placeholders as independent singles (never force pair alignment)
                        if (IsBackPlaceholder(c) && IsBackPlaceholder(n)) return false;
            // Nazgûl copies are always treated as independent singles (no enforced pairing)
            if (IsNazgul(c) && IsNazgul(n)) return false;
                        var cName = c.Name ?? string.Empty;
                        var nName = n.Name ?? string.Empty;
                        if (string.Equals(cName.Trim(), nName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            // If either card is in a long run (>=3), do not treat as a pair
                            if (longRunCards.Contains(c) || longRunCards.Contains(n)) return false;
                            // Check if there is a third consecutive with same name; if so, treat all as singles
                            if (idx + 2 < list.Count)
                            {
                                var third = list[idx + 2];
                                if (third != null && !third.IsModalDoubleFaced && !third.IsBackFace)
                                {
                                    var tName = third.Name ?? string.Empty;
                                    if (string.Equals(cName.Trim(), tName.Trim(), StringComparison.OrdinalIgnoreCase))
                                        return false; // 3+ run -> no enforced pairing
                                }
                            }
                            return true; // exactly two
                        }
                    }
                }
                return false;
            }

            bool IsSecondOfPair(List<CardEntry> list, int idx)
            {
                if (idx <= 0 || idx >= list.Count) return false;
                var c = list[idx];
                if (c == null)
                {
                    Debug.WriteLine($"[BuildOrderedFaces] Null entry at index {idx} in remaining list (IsSecondOfPair). Treating as single.");
                    return false;
                }
                // MFC back face is inherently second
                if (c.IsBackFace) return true;
                // Explicit variant pair second
                if (_explicitPairKeys.Count > 0)
                {
                    var prevExp = list[idx - 1];
                    if (prevExp != null && c != null && _explicitPairKeys.TryGetValue(prevExp, out var pk1) && _explicitPairKeys.TryGetValue(c, out var pk2) && pk1 == pk2)
                        return true;
                }
                // Duplicate second if previous + this form a standard duplicate-name pair
                var prev = list[idx - 1];
                if (prev != null && c != null && !prev.IsModalDoubleFaced && !prev.IsBackFace && !c.IsModalDoubleFaced && !c.IsBackFace)
                {
                    bool IsBackPlaceholder(CardEntry ce) => string.Equals(ce.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                    bool IsNazgul(CardEntry ce) => string.Equals(ce.Name?.Trim(), "Nazgûl", StringComparison.OrdinalIgnoreCase);
                    // If both are backface placeholders, treat as independent singles
                    if (IsBackPlaceholder(prev) && IsBackPlaceholder(c)) return false;
                    // Nazgûl copies: never second of a forced pair
                    if (IsNazgul(prev) && IsNazgul(c)) return false;
                    var prevName = prev.Name ?? string.Empty;
                    var cName = c.Name ?? string.Empty;
                    if (string.Equals(prevName.Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        // If either card belongs to a long run (>=3), skip pairing logic.
                        if (longRunCards.Contains(prev) || longRunCards.Contains(c)) return false;
                        // Ensure not part of a 3+ run: if previous-previous shares name or next shares name, skip
                        bool hasPrevPrev = idx - 2 >= 0 && list[idx - 2] != null && !list[idx - 2].IsModalDoubleFaced && !list[idx - 2].IsBackFace && string.Equals((list[idx-2].Name??string.Empty).Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase);
                        bool hasNext = idx + 1 < list.Count && list[idx + 1] != null && !list[idx + 1].IsModalDoubleFaced && !list[idx + 1].IsBackFace && string.Equals((list[idx+1].Name??string.Empty).Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (hasPrevPrev || hasNext) return false; // part of 3+ run => not a strict second
                        return true; // exactly a second of a 2-run
                    }
                }
                return false;
            }

            // Determine group at head
            int groupSize;
            if (IsPairStart(remaining, 0)) groupSize = 2; else groupSize = 1;
            if (groupSize == 2)
            {
                // Need to start pair at an even column. Last column cannot host first half of a pair.
                if (col % 2 == 1 || col == ColumnsPerPage -1)
                {
                    // Find first true single later (not part of any pair front or back, nor second half)
                    int singleIndex = -1;
                    for (int i = 1; i < remaining.Count; i++)
                    {
                        var cand = remaining[i];
                        bool isBackPlaceholder = string.Equals(cand.Number, "BACK", StringComparison.OrdinalIgnoreCase);
                        // Backface placeholder acts as barrier: stop searching beyond this point so we don't reorder across it
                        if (isBackPlaceholder) break;
                        if (!IsPairStart(remaining, i) && !IsSecondOfPair(remaining, i) && !cand.IsBackFace)
                        {
                            singleIndex = i;
                            break;
                        }
                    }
                    if (singleIndex != -1)
                    {
                        // Pull forward that single to fill this misaligned slot
                        _orderedFaces.Add(remaining[singleIndex]);
                        remaining.RemoveAt(singleIndex);
                        globalSlot++;
                        continue; // reconsider same pair at next column (which will now be even)
                    }
                    // Fallback: no singles available, we must place pair misaligned (will straddle rows) to avoid holes.
                }
            }

            if (groupSize == 1)
            {
                if (remaining[0] == null)
                {
                    Debug.WriteLine("[BuildOrderedFaces] Encountered null single at head; skipping.");
                    remaining.RemoveAt(0);
                    continue;
                }
                _orderedFaces.Add(remaining[0]);
                remaining.RemoveAt(0);
                globalSlot++;
            }
            else // groupSize == 2
            {
                if (remaining[0] == null || remaining[1] == null)
                {
                    Debug.WriteLine("[BuildOrderedFaces] Encountered null within pair; downgrading to single placement.");
                    if (remaining[0] != null) { _orderedFaces.Add(remaining[0]); }
                    remaining.RemoveAt(0);
                    globalSlot++;
                    continue;
                }
                _orderedFaces.Add(remaining[0]);
                _orderedFaces.Add(remaining[1]);
                remaining.RemoveRange(0, 2);
                globalSlot += 2;
            }
        }
    }

    private void Refresh()
    {
        // Use _views list to determine what to display
        LeftSlots.Clear();
        RightSlots.Clear();
        if (_views.Count == 0)
        {
            PageDisplay = "No pages";
            return;
        }
        var view = _views[_currentViewIndex];
        if (view.LeftPage.HasValue)
            FillPage(LeftSlots, view.LeftPage.Value);
        if (view.RightPage.HasValue)
            FillPage(RightSlots, view.RightPage.Value);
        // Trigger async metadata resolution for shown pages
        TriggerPageResolution(view.LeftPage ?? 0, view.RightPage ?? 0);
        // Build display text
        int binderNumber = view.BinderIndex + 1;
        if (view.LeftPage.HasValue && view.RightPage.HasValue)
        {
            int leftLocal = ((view.LeftPage.Value -1) % PagesPerBinder) +1;
            int rightLocal = ((view.RightPage.Value -1) % PagesPerBinder) +1;
            PageDisplay = $"Binder {binderNumber}: Pages {leftLocal}-{rightLocal}";
        }
        else if (view.RightPage.HasValue)
        {
            int local = ((view.RightPage.Value -1) % PagesPerBinder) +1;
            PageDisplay = $"Binder {binderNumber}: Page {local} (Front Cover)";
        }
        else if (view.LeftPage.HasValue)
        {
            int local = ((view.LeftPage.Value -1) % PagesPerBinder) +1;
            PageDisplay = $"Binder {binderNumber}: Page {local} (Back Cover)";
        }
        OnPropertyChanged(nameof(PageDisplay));
        UpdateBinderBackground(binderNumber);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateBinderBackground(int binderNumber)
    {
        // Binder numbering starts at 1; we no longer force binder 1 to black.
        int idx = binderNumber - 1; // zero-based index into custom/random sequence
        Brush baseBrush;
        if (idx < _customBinderBrushes.Count)
        {
            baseBrush = _customBinderBrushes[idx];
        }
        else
        {
            int needed = idx - _customBinderBrushes.Count;
            while (_generatedRandomBinderBrushes.Count <= needed)
            {
                var col = Color.FromRgb((byte)_rand.Next(48,256), (byte)_rand.Next(48,256), (byte)_rand.Next(48,256));
                _generatedRandomBinderBrushes.Add(new SolidColorBrush(col));
            }
            baseBrush = _generatedRandomBinderBrushes[needed];
        }
        var solid = baseBrush as SolidColorBrush;
        var c = solid?.Color ?? Colors.Gray;
        var brush = new LinearGradientBrush();
        brush.StartPoint = new Point(0,0);
        brush.EndPoint = new Point(1,1);
        brush.GradientStops.Add(new GradientStop(c, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R/3),(byte)(c.G/3),(byte)(c.B/3)), 1));
        if (brush.CanFreeze) brush.Freeze();
        BinderBackground = brush;
    }

    private void FillPage(ObservableCollection<CardSlot> collection, int pageNumber)
    {
        if (pageNumber <= 0) return;
    // Metadata resolution happens asynchronously; placeholders shown until resolved
        int startIndex = (pageNumber - 1) * SlotsPerPage;
        var tasks = new List<Task>();
        for (int i = 0; i < SlotsPerPage; i++)
        {
            int gi = startIndex + i;
            if (gi < _orderedFaces.Count)
            {
                var face = _orderedFaces[gi];
                var slot = new CardSlot(face, gi);
                collection.Add(slot);
                tasks.Add(slot.TryLoadImageAsync(Http, face.Set ?? string.Empty, face.Number, face.IsBackFace));
            }
            else
            {
                collection.Add(new CardSlot("(Empty)", gi));
            }
        }
        _ = Task.WhenAll(tasks);
    }

    private bool TryParseColorToken(string token, out Brush brush)
    {
        brush = Brushes.Transparent;
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        // Try known colors via ColorConverter
        try
        {
            var obj = ColorConverter.ConvertFromString(token);
            if (obj is Color col)
            {
                var solid = new SolidColorBrush(col);
                if (solid.CanFreeze) solid.Freeze();
                brush = solid;
                return true;
            }
        }
        catch { }
        // Hex shorthand without #
        if (Regex.IsMatch(token, "^[0-9A-Fa-f]{6}$"))
        {
            try
            {
                byte r = Convert.ToByte(token.Substring(0,2),16);
                byte g = Convert.ToByte(token.Substring(2,2),16);
                byte b = Convert.ToByte(token.Substring(4,2),16);
                var solid = new SolidColorBrush(Color.FromRgb(r,g,b));
                if (solid.CanFreeze) solid.Freeze();
                brush = solid;
                return true;
            } catch { }
        }
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void TriggerPageResolution(params int[] pageNumbers)
    {
        // Collect needed spec indices for provided pages plus lookahead page for each
        var neededSpecs = new HashSet<int>();
        int desiredFacesStart = int.MaxValue;
    int preFaceCount = _orderedFaces.Count; // capture current face count so we can detect growth (e.g., MFC back injection)
        foreach (var p in pageNumbers)
        {
            if (p <=0) continue;
            int startFace = (p -1) * SlotsPerPage;
            int endFace = startFace + SlotsPerPage * 2; // include lookahead
            desiredFacesStart = Math.Min(desiredFacesStart, startFace);
            int faceCounter = 0;
            for (int si=0; si<_specs.Count && faceCounter < endFace; si++)
            {
                if (faceCounter >= startFace && faceCounter < endFace && _specs[si].Resolved == null && !_specs[si].explicitEntry)
                    neededSpecs.Add(si);
                faceCounter++;
                if (_mfcBacks.ContainsKey(si)) faceCounter++; // skip back
            }
        }
        if (neededSpecs.Count == 0) return;
        var quickList = new List<(string setCode,string number,string? nameOverride,int specIndex)>();
        foreach (var si in neededSpecs)
        {
            var s = _specs[si];
            quickList.Add((s.setCode, s.number, s.overrideName, si));
        }
        _ = Task.Run(async () =>
        {
            await ResolveSpecsAsync(quickList, neededSpecs, updateInterval: 3);
            Application.Current.Dispatcher.Invoke(() =>
            {
                RebuildCardListFromSpecs();
                BuildOrderedFaces();
                bool faceCountChanged = _orderedFaces.Count != preFaceCount;
                if (faceCountChanged)
                {
                    // Page boundaries depend on total face count; rebuild them to avoid duplicated fronts after new MFC backs appear.
                    RebuildViews();
                    // Clamp current view index in case count changed
                    if (_currentViewIndex >= _views.Count) _currentViewIndex = Math.Max(0, _views.Count -1);
                }
                // redraw current view only if still same index
                if (_currentViewIndex < _views.Count)
                {
                    var v = _views[_currentViewIndex];
                    LeftSlots.Clear(); RightSlots.Clear();
                    if (v.LeftPage.HasValue) FillPage(LeftSlots, v.LeftPage.Value);
                    if (v.RightPage.HasValue) FillPage(RightSlots, v.RightPage.Value);
                }
            });
        });
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

internal static class ScryfallUrlHelper
{
    public static string BuildCardApiUrl(string setCode, string number)
    {
        if (string.IsNullOrWhiteSpace(setCode)) return string.Empty;
        setCode = setCode.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(number)) return $"https://api.scryfall.com/cards/{setCode}";
        var segments = number.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(s => Uri.EscapeDataString(s));
        return $"https://api.scryfall.com/cards/{setCode}/" + string.Join('/', segments);
    }
}
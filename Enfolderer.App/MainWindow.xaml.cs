using System;
using System.Collections.Generic;
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
}

public record CardEntry(string Name, string Number, string? Set, bool IsModalDoubleFaced, bool IsBackFace = false, string? FrontRaw = null, string? BackRaw = null)
{
    public string Display => string.IsNullOrWhiteSpace(Number) ? Name : $"{Number} {Name}";
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
    public static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
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
    private static readonly Dictionary<string,(string? front,string? back)> _map = new(StringComparer.OrdinalIgnoreCase);
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
    private static readonly Dictionary<string,string?> _map = new(StringComparer.OrdinalIgnoreCase);
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
        Number = entry.Number;
        Set = entry.Set ?? string.Empty;
        Tooltip = entry.Display + (string.IsNullOrEmpty(Set) ? string.Empty : $" ({Set})");
        Background = new SolidColorBrush(GenerateColor(index));
    }
    public CardSlot(string placeholder, int index)
    {
        Name = placeholder;
        Number = string.Empty;
        Set = string.Empty;
        Tooltip = placeholder;
        Background = new SolidColorBrush(GenerateColor(index));
    }
    private static Color GenerateColor(int index)
    {
        var rnd = new Random(HashCode.Combine(index, 7919));
        byte r = (byte)(90 + rnd.Next(0, 120));
        byte g = (byte)(90 + rnd.Next(0, 120));
        byte b = (byte)(90 + rnd.Next(0, 120));
        return Color.FromArgb(255, r, g, b);
    }

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
                var apiUrl = $"https://api.scryfall.com/cards/{setCode.ToLowerInvariant()}/{Uri.EscapeDataString(number)}";
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
            var cacheKey = imgUrl + (isBackFace ? "|back" : "|front");
            if (ImageCacheStore.Cache.TryGetValue(cacheKey, out var cachedBmp)) { ImageSource = cachedBmp; return; }
            if (ImageCacheStore.TryLoadFromDisk(cacheKey, out var diskBmp)) { ImageSource = diskBmp; return; }
            await ApiRateLimiter.WaitAsync();
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
    private const int SlotsPerPage = 12; // 4x3
    // Binder has 20 physical (double-sided) pages => 40 numbered sides (pages) we display
    private const int PagesPerBinder = 40; // numbered sides per binder
    private readonly List<CardEntry> _cards = new();
    private readonly List<CardEntry> _orderedFaces = new(); // reordered faces honoring placement constraints
    private readonly List<CardSpec> _specs = new(); // raw specs in file order
    private readonly Dictionary<int, CardEntry> _mfcBacks = new(); // synthetic back faces keyed by spec index
    private readonly List<PageView> _views = new(); // sequence of display views across all binders
    private int _currentViewIndex = 0;
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var c = new HttpClient(handler);
    // User-Agent must be a series of product tokens and optional comments; remove invalid free-form text
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer/0.1");
    // Optional comment with project URL
    c.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/yourrepo)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
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
    NextCommand = new RelayCommand(_ => { _currentViewIndex++; Refresh(); }, _ => _currentViewIndex < _views.Count - 1);
    PrevCommand = new RelayCommand(_ => { _currentViewIndex--; Refresh(); }, _ => _currentViewIndex > 0);
    FirstCommand = new RelayCommand(_ => { _currentViewIndex = 0; Refresh(); }, _ => _currentViewIndex != 0);
    LastCommand = new RelayCommand(_ => { if (_views.Count>0) { _currentViewIndex = _views.Count -1; Refresh(); } }, _ => _views.Count>0 && _currentViewIndex != _views.Count -1);
        NextBinderCommand = new RelayCommand(_ => { JumpBinder(1); }, _ => CanJumpBinder(1));
        PrevBinderCommand = new RelayCommand(_ => { JumpBinder(-1); }, _ => CanJumpBinder(-1));
    JumpToPageCommand = new RelayCommand(_ => JumpToBinderPage(), _ => CanJumpToBinderPage());
        RebuildViews();
        Refresh();
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
                    _cards.Add(new CardEntry(backDisplay, entry.Number, entry.Set, false, true, entry.FrontRaw, entry.BackRaw));
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
    var lines = await File.ReadAllLinesAsync(path);
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
        string? currentSet = null;
    var fetchList = new List<(string setCode,string number,string? nameOverride,int specIndex)>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
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
            if (numberPart.Contains('-', StringComparison.Ordinal))
            {
                var pieces = numberPart.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length==2 && int.TryParse(pieces[0], out int startNum) && int.TryParse(pieces[1], out int endNum) && startNum<=endNum)
                {
                    for (int n=startNum; n<=endNum; n++)
                    {
                        _specs.Add(new CardSpec(currentSet, n.ToString(), null, false));
                        fetchList.Add((currentSet, n.ToString(), null, _specs.Count-1));
                    }
                }
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
    private const int CacheSchemaVersion = 2; // bump when cache layout/logic changes
    private static readonly HashSet<string> PhysicallyTwoSidedLayouts = new(StringComparer.OrdinalIgnoreCase){"transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype"};
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
            if (data.SchemaVersion != CacheSchemaVersion || string.IsNullOrEmpty(data.Layout)) return false; // force refresh
            bool physTwoSided = data.Layout != null && PhysicallyTwoSidedLayouts.Contains(data.Layout);
            bool effectiveMfc = data.IsMfc && physTwoSided;
            var ce = new CardEntry(data.Name, data.Number, data.Set, effectiveMfc, false, data.FrontRaw, data.BackRaw);
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
            if (faces.Exists(f => f.SchemaVersion != CacheSchemaVersion || string.IsNullOrEmpty(f.Layout))) return false;
            _cards.Clear();
            foreach (var f in faces)
            {
                bool physTwoSided = f.Layout != null && PhysicallyTwoSidedLayouts.Contains(f.Layout);
                bool effectiveMfc = f.IsMfc && physTwoSided && !f.IsBack;
                var ce = new CardEntry(f.Name, f.Number, f.Set, effectiveMfc, f.IsBack, f.FrontRaw, f.BackRaw);
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
                            _mfcBacks[f.specIndex] = backEntry;
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
                            _mfcBacks[f.specIndex] = backEntry; // store for injection after front
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
        for (int i=0;i<_specs.Count;i++)
        {
            var s = _specs[i];
            if (s.Resolved != null)
                _cards.Add(s.Resolved);
            else
            {
                var placeholderName = s.overrideName ?? s.number;
                _cards.Add(new CardEntry(placeholderName, s.number, s.setCode, false));
            }
            if (_mfcBacks.TryGetValue(i, out var back))
                _cards.Add(back);
        }
    }

    private record CardSpec(string setCode, string number, string? overrideName, bool explicitEntry)
    {
        public CardEntry? Resolved { get; set; }
    }

    private async Task<CardEntry?> FetchCardMetadataAsync(string setCode, string number, string? overrideName)
    {
        try
        {
            await ApiRateLimiter.WaitAsync();
            var url = $"https://api.scryfall.com/cards/{setCode.ToLowerInvariant()}/{Uri.EscapeDataString(number)}";
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
            if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array && faces.GetArrayLength() >= 2)
            {
                var f0 = faces[0]; var f1 = faces[1];
                if (!hasRootImageUris && isPhysicallyTwoSidedLayout)
                {
                    // Treat as true double-sided (needs two slots)
                    frontRaw = f0.GetProperty("name").GetString();
                    backRaw = f1.GetProperty("name").GetString();
                    isMfc = true;
                    if (displayName == null) displayName = $"{frontRaw} ({backRaw})";
                    if (f0.TryGetProperty("image_uris", out var f0Imgs) && f0Imgs.TryGetProperty("normal", out var f0Norm)) frontImg = f0Norm.GetString(); else if (f0.TryGetProperty("image_uris", out f0Imgs) && f0Imgs.TryGetProperty("large", out var f0Large)) frontImg = f0Large.GetString();
                    if (f1.TryGetProperty("image_uris", out var f1Imgs) && f1Imgs.TryGetProperty("normal", out var f1Norm)) backImg = f1Norm.GetString(); else if (f1.TryGetProperty("image_uris", out f1Imgs) && f1Imgs.TryGetProperty("large", out var f1Large)) backImg = f1Large.GetString();
                }
                else
                {
                    // Multi-face metadata but single physical face (split/aftermath/adventure etc) -> single slot
                    if (displayName == null && root.TryGetProperty("name", out var npropSplit)) displayName = npropSplit.GetString();
                    if (hasRootImageUris)
                    {
                        if (rootImgs.TryGetProperty("normal", out var rootNorm)) frontImg = rootNorm.GetString();
                        else if (rootImgs.TryGetProperty("large", out var rootLarge)) frontImg = rootLarge.GetString();
                    }
                    else if (f0.TryGetProperty("image_uris", out var f0Imgs2) && f0Imgs2.TryGetProperty("normal", out var f0Norm2)) frontImg = f0Norm2.GetString();
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
        // Work on a queue (list) of indices; we will remove as we schedule.
        var remaining = new List<CardEntry>(_cards); // copy
        int globalSlot = 0;
        while (remaining.Count > 0)
        {
            int col = (globalSlot % SlotsPerPage) % 4; // 0..3 within row

            bool IsPairStart(List<CardEntry> list, int idx)
            {
                if (idx < 0 || idx >= list.Count) return false;
                var c = list[idx];
                if (c == null)
                {
                    Debug.WriteLine($"[BuildOrderedFaces] Null entry at index {idx} in remaining list (IsPairStart). Treating as single.");
                    return false;
                }
                // MFC front + back
                if (c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
                {
                    var next = list[idx + 1];
                    if (next != null && next.IsBackFace) return true;
                }
                // Duplicate pair
                if (!c.IsModalDoubleFaced && !c.IsBackFace && idx + 1 < list.Count)
                {
                    var n = list[idx + 1];
                    if (n != null && !n.IsModalDoubleFaced && !n.IsBackFace)
                    {
                        var cName = c.Name ?? string.Empty;
                        var nName = n.Name ?? string.Empty;
                        if (string.Equals(cName.Trim(), nName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
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
                // Duplicate second if previous + this form pair
                var prev = list[idx - 1];
                if (prev != null && !prev.IsModalDoubleFaced && !prev.IsBackFace && !c.IsModalDoubleFaced && !c.IsBackFace)
                {
                    var prevName = prev.Name ?? string.Empty;
                    var cName = c.Name ?? string.Empty;
                    if (string.Equals(prevName.Trim(), cName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }

            // Determine group at head
            int groupSize;
            if (IsPairStart(remaining, 0)) groupSize = 2; else groupSize = 1;
            if (groupSize == 2)
            {
                // Need to start pair at even column (0 or 2). Column 3 cannot host first half of a pair.
                if (col == 1 || col == 3)
                {
                    // Find first true single later (not part of any pair front or back, nor second half)
                    int singleIndex = -1;
                    for (int i = 1; i < remaining.Count; i++)
                    {
                        if (!IsPairStart(remaining, i) && !IsSecondOfPair(remaining, i) && !remaining[i].IsBackFace)
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
        CommandManager.InvalidateRequerySuggested();
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
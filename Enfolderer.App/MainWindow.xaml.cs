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

    private void OpenCollection_Click(object sender, RoutedEventArgs e)
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
                _vm.LoadFromFile(dlg.FileName);
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
            var apiUrl = $"https://api.scryfall.com/cards/{setCode.ToLowerInvariant()}/{Uri.EscapeDataString(number)}";
            Debug.WriteLine($"[CardSlot] API fetch {apiUrl} face={faceIndex}");
            await ApiRateLimiter.WaitAsync(); // rate-limit metadata request
            await FetchGate.WaitAsync();
            HttpResponseMessage resp = null!;
            try
            {
                resp = await client.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead);
            }
            finally
            {
                FetchGate.Release();
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = string.Empty;
                    try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                    Debug.WriteLine($"[CardSlot] API status {(int)resp.StatusCode} {resp.ReasonPhrase} Body: {body}");
                    return;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                JsonElement root = doc.RootElement;
                string? imgUrl = null;
                if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array && faces.GetArrayLength() > faceIndex)
                {
                    var face = faces[faceIndex];
                    if (face.TryGetProperty("image_uris", out var faceImages) && faceImages.TryGetProperty("normal", out var normalProp))
                        imgUrl = normalProp.GetString();
                    else if (face.TryGetProperty("image_uris", out faceImages) && faceImages.TryGetProperty("large", out var largeProp))
                        imgUrl = largeProp.GetString();
                }
                if (imgUrl == null && root.TryGetProperty("image_uris", out var images) && images.TryGetProperty("normal", out var normalRoot))
                    imgUrl = normalRoot.GetString();
                if (imgUrl == null && root.TryGetProperty("image_uris", out images) && images.TryGetProperty("large", out var largeRoot))
                    imgUrl = largeRoot.GetString();

                if (string.IsNullOrWhiteSpace(imgUrl))
                {
                    Debug.WriteLine("[CardSlot] No image URL found in API response.");
                    return;
                }
                var cacheKey = imgUrl + (isBackFace ? "|back" : "|front");
                if (ImageCacheStore.Cache.TryGetValue(cacheKey, out var cachedBmp)) { ImageSource = cachedBmp; return; }
                await ApiRateLimiter.WaitAsync(); // rate-limit image request
                var bytes = await client.GetByteArrayAsync(imgUrl);
                try
                {
                    var bmp2 = CreateFrozenBitmap(bytes);
                    ImageSource = bmp2;
                    ImageCacheStore.Cache[cacheKey] = bmp2;
                }
                catch (Exception exBmp)
                {
                    Debug.WriteLine($"[CardSlot] Bitmap create failed: {exBmp.Message}");
                }
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
    private const int PagesPerBinder = 20; // physical binder size
    private readonly List<CardEntry> _cards = new();
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
        int totalFaces = _cards.Count;
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
        _currentViewIndex = 0;
        RebuildViews();
        Refresh();
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
        int startIndex = (pageNumber - 1) * SlotsPerPage;
        var faces = new CardEntry?[SlotsPerPage];
        // Initial pull
        for (int i = 0; i < SlotsPerPage; i++)
        {
            int gi = startIndex + i;
            if (gi < _cards.Count) faces[i] = _cards[gi];
        }
        // Compact to remove internal nulls (causing unexpected (Empty) gaps ) while preserving order
        var compact = new List<CardEntry>(SlotsPerPage);
        foreach (var f in faces) if (f != null) compact.Add(f);
        for (int i = 0; i < SlotsPerPage; i++) faces[i] = i < compact.Count ? compact[i] : null;

        // Build list of front/back pairs on this page (only if both faces fall inside this page)
        var processed = new bool[SlotsPerPage];
    for (int i = 0; i < SlotsPerPage; i++)
        {
            if (faces[i] == null || processed[i]) continue;
            var entry = faces[i]!;
            if (entry.IsModalDoubleFaced && !entry.IsBackFace)
            {
                // Assume its back is immediately following in global list; check if inside page
                int globalIndexFront = startIndex + i;
                int globalIndexBack = globalIndexFront + 1;
                if (globalIndexBack < _cards.Count)
                {
                    var back = _cards[globalIndexBack];
                    if (back.IsBackFace)
                    {
                        int localBack = globalIndexBack - startIndex;
                        if (localBack is >= 0 and < SlotsPerPage)
                        {
                            // Both faces present locally; ensure placement rule
                            int row = i / 4;
                            int col = i % 4;
                            if (col == 1 || col == 3 || (col == 2 && localBack != i + 1) || (col == 0 && localBack != i + 1))
                            {
                                // Need to relocate pair so front at col 0 or 2, back at +1
                                int targetColA = (col <= 1) ? 0 : 2; // choose block start
                                int targetIndex = row * 4 + targetColA;
                                // If block occupied by something else not part of this pair, attempt swap
                                bool canPlace = true;
                                for (int offset = 0; offset < 2; offset++)
                                {
                                    int idx = targetIndex + offset;
                                    if (idx >= SlotsPerPage) { canPlace = false; break; }
                                    if (faces[idx] != null && faces[idx] != entry && faces[idx] != back)
                                    {
                                        // If occupant is part of another MFC pair, skip relocation for now.
                                        if (faces[idx]!.IsModalDoubleFaced && !faces[idx]!.IsBackFace)
                                        { canPlace = false; break; }
                                    }
                                }
                                if (canPlace)
                                {
                                    // Clear old spots and move
                                    faces[i] = null; faces[localBack] = null;
                                    // Slide following entries forward to fill gaps before placement to keep compaction
                                    var remaining = new List<CardEntry>();
                                    foreach (var ff in faces) if (ff != null) remaining.Add(ff);
                                    for (int k = 0; k < SlotsPerPage; k++) faces[k] = k < remaining.Count ? remaining[k] : null;
                                    // Recompute pair indices after compaction (entry/back still in remaining list)
                                    int newFrontIndex = Array.IndexOf(faces, entry);
                                    int newBackIndex = Array.IndexOf(faces, back);
                                    // If they are separated incorrectly, move as needed
                                    if (newBackIndex != newFrontIndex + 1)
                                    {
                                        // Remove both then insert as block at row start target
                                        var list = new List<CardEntry>();
                                        foreach (var ff in faces) if (ff != null && ff != entry && ff != back) list.Add(ff);
                                        int insertRow = i / 4;
                                        int insertStart = row * 4 + targetColA;
                                        // Ensure list capacity
                                        while (list.Count < insertStart) list.Add(null!);
                                        list.Insert(Math.Min(insertStart, list.Count), entry);
                                        list.Insert(Math.Min(insertStart + 1, list.Count), back);
                                        // Re-pack into faces
                                        var packed = new List<CardEntry>();
                                        foreach (var ff in list) if (ff != null) packed.Add(ff);
                                        for (int k = 0; k < SlotsPerPage; k++) faces[k] = k < packed.Count ? packed[k] : null;
                                    }
                                    else
                                    {
                                        // Already adjacent now. If front column invalid, shift block to valid start within row if space.
                                        int frontCol = newFrontIndex % 4;
                                        if (frontCol is 1 or 3)
                                        {
                                            int blockStart = frontCol <= 1 ? newFrontIndex - 1 : newFrontIndex - 1;
                                            if (blockStart >= 0 && blockStart / 4 == newFrontIndex / 4)
                                            {
                                                var tmpFront = faces[newFrontIndex];
                                                var tmpBack = faces[newFrontIndex + 1];
                                                faces[newFrontIndex] = null; faces[newFrontIndex + 1] = null;
                                                // shift others forward
                                                var remain2 = new List<CardEntry>();
                                                foreach (var ff in faces) if (ff != null) remain2.Add(ff);
                                                for (int k = 0; k < SlotsPerPage; k++) faces[k] = k < remain2.Count ? remain2[k] : null;
                                                if (blockStart + 1 < SlotsPerPage)
                                                {
                                                    // Insert block at blockStart
                                                    var list2 = new List<CardEntry>();
                                                    foreach (var ff in faces) if (ff != null) list2.Add(ff);
                                                    if (tmpBack != null && tmpFront != null)
                                                    {
                                                        list2.Insert(Math.Min(blockStart, list2.Count), tmpBack);
                                                        list2.Insert(Math.Min(blockStart, list2.Count), tmpFront);
                                                    }
                                                    var packed2 = new List<CardEntry>();
                                                    foreach (var ff in list2) if (ff != null) packed2.Add(ff);
                                                    for (int k = 0; k < SlotsPerPage; k++) faces[k] = k < packed2.Count ? packed2[k] : null;
                                                }
                                            }
                                        }
                                    }
                                    processed[targetIndex] = true;
                                    processed[targetIndex + 1] = true;
                                    continue;
                                }
                            }
                            processed[i] = true;
                            processed[localBack] = true;
                        }
                    }
                }
            }
        }

        var tasks = new List<Task>();
        for (int i = 0; i < SlotsPerPage; i++)
        {
            int gi = startIndex + i;
            if (faces[i] != null)
            {
                var slot = new CardSlot(faces[i]!, gi);
                collection.Add(slot);
                tasks.Add(slot.TryLoadImageAsync(Http, faces[i]!.Set ?? string.Empty, faces[i]!.Number, faces[i]!.IsBackFace));
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
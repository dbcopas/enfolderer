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

    private void UpdateMainDbFromCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select CSV File to Update mainDb.db",
                    Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;
                var result = CsvMainDbUpdater.Process(dlg.FileName);
                MessageBox.Show(this, $"mainDb.db update complete:\nUpdated: {result.Updated}\nInserted: {result.Inserted}\nErrors: {result.Errors}", "CSV Utility", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "CSV Utility Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


    private async void ImportScryfallSet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("Enter Scryfall set code (e.g., mom)", "Import Set", "");
            if (string.IsNullOrWhiteSpace(input)) return;
            if (_vm == null || string.IsNullOrEmpty(_vm.CurrentCollectionDir)) { MessageBox.Show(this, "Open a collection file first so the mainDb location is known."); return; }
            string dbPath = System.IO.Path.Combine(_vm.CurrentCollectionDir!, "mainDb.db");
            if (!File.Exists(dbPath)) { MessageBox.Show(this, "mainDb.db not found."); return; }
            bool forceReimport = (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
            var importer = new ScryfallSetImporter();
            var summary = await importer.ImportAsync(input.Trim(), forceReimport, dbPath, msg => _vm.SetStatus(msg));
            _vm.SetStatus($"Import {summary.SetCode}: inserted {summary.Inserted}, updated {summary.UpdatedExisting}, skipped {summary.Skipped}. Total fetched {summary.TotalFetched}{(summary.DeclaredCount.HasValue?"/"+summary.DeclaredCount.Value:"")}.");
        }
        catch (Exception ex)
        {
            _vm.SetStatus("Import error: " + ex.Message);
        }
    }

    // Auto-import all binder set codes not present in mainDb
    private async void AutoImportMissingSets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || string.IsNullOrEmpty(_vm.CurrentCollectionDir)) { _vm?.SetStatus("Open a collection first."); return; }
            string dbPath = System.IO.Path.Combine(_vm.CurrentCollectionDir!, "mainDb.db");
            var service = new AutoImportMissingSetsService();
            HashSet<string> binderSets = _vm.GetCurrentSetCodes();
            bool confirm = true;
            bool ConfirmPrompt(string list) => MessageBox.Show(this, $"Import missing sets into mainDb?\n\n{list}", "Auto Import", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
            await service.AutoImportAsync(binderSets, dbPath, null, confirm, list => ConfirmPrompt(list), _vm);
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Auto import error: " + ex.Message);
        }
    }



    public MainWindow()
    {
        // Invoke generated InitializeComponent if present; otherwise manual load (design-time / analysis env may lack XAML compile)
        var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (init != null)
        {
            init.Invoke(this, null);
        }
        else
        {
            try
            {
                var resourceLocater = new Uri("/Enfolderer.App;component/MainWindow.xaml", UriKind.Relative);
                Application.LoadComponent(this, resourceLocater);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WPF] Manual XAML load failed: {ex.Message}");
                throw;
            }
        }
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
                if (_vm != null)
                {
                    // Force reload logic now handled internally by LoadFromFileAsync (it calls Reload)
                    await _vm.LoadFromFileAsync(dlg.FileName);
                }
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
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            int delta = e.Delta; // >0 wheel up, <0 wheel down
            if (ctrl)
            {
                // Ctrl + wheel = binder jump
                if (delta > 0)
                {
                    if (_vm.PrevBinderCommand.CanExecute(null)) _vm.PrevBinderCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextBinderCommand.CanExecute(null)) _vm.NextBinderCommand.Execute(null);
                }
            }
            else if (shift)
            {
                // Shift + wheel = set boundary jump
                if (delta > 0)
                {
                    if (_vm.PrevSetCommand.CanExecute(null)) _vm.PrevSetCommand.Execute(null);
                }
                else if (delta < 0)
                {
                    if (_vm.NextSetCommand.CanExecute(null)) _vm.NextSetCommand.Execute(null);
                }
            }
            else
            {
                // No modifier = normal page navigation
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

    private void RefreshQuantities_Click(object sender, RoutedEventArgs e)
    {
    _vm?.RefreshQuantities();
    }

    private void CardSlot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            if (sender is not Border b) return;
            if (b.DataContext is not CardSlot slot) return;
            _vm.ToggleCardQuantity(slot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UI] Click handler error: {ex.Message}");
        }
    }
}




// Computes a single beige tone variation per binder load

public class BinderViewModel : INotifyPropertyChanged, IStatusSink
{
    // Directory of currently loaded collection (binder) text file
    private string? _currentCollectionDir;
    public string? CurrentCollectionDir => _currentCollectionDir;
    // ==== Restored state fields (previously lost during file corruption) ====
    private static BinderViewModel? _singleton;
    private static readonly object _singletonLock = new();
    public static void RegisterInstance(BinderViewModel vm) { lock(_singletonLock) _singleton = vm; }
    public static void WithVm(Action<BinderViewModel> action) { BinderViewModel? vm; lock(_singletonLock) vm = _singleton; if (vm!=null) { try { action(vm); } catch { } } }

    private static CancellationTokenSource? _apiFlashCts;
    private string _apiStatus = string.Empty;
    public string ApiStatus { get => _apiStatus; private set { if (_apiStatus!=value) { _apiStatus = value; OnPropertyChanged(); } } }

    private string _status = string.Empty;
    public string Status { get => _status; private set { if (_status!=value) { _status = value; OnPropertyChanged(); } } }
    public void SetStatus(string message) => Status = message;

    private static bool _debugHttpLogging = true;
    private static readonly object _httpLogLock = new();
    private static readonly ConcurrentDictionary<string,string> _imageUrlNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static string HttpLogPath => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "http-log.txt");

    private void UpdatePanel(string? latest = null)
    {
        // Minimal implementation: reflect latest URL/status plus simple counters.
        if (!string.IsNullOrEmpty(latest)) ApiStatus = latest;
    }

    // UI-bound collections & properties (redeclared after corruption)
    public ObservableCollection<CardSlot> LeftSlots { get; } = new();
    public ObservableCollection<CardSlot> RightSlots { get; } = new();
    private string _pageDisplay = string.Empty;
    public string PageDisplay { get => _pageDisplay; private set { if (_pageDisplay!=value) { _pageDisplay = value; OnPropertyChanged(); } } }
    private Brush _binderBackground = Brushes.Black;
    public Brush BinderBackground { get => _binderBackground; private set { if (_binderBackground!=value) { _binderBackground = value; OnPropertyChanged(); } } }
    private readonly BinderThemeService _binderTheme = new();
    private readonly Random _rand = new(12345);
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
    private readonly NavigationService _nav = new(); // centralized navigation
    private IReadOnlyList<NavigationService.PageView> _views => _nav.Views; // proxy for legacy references
    private readonly CardCollectionData _collection = new(); // collection DB data
    private readonly CardQuantityService _quantityService = new(); // phase 2 extracted quantity logic
    private readonly CollectionRepository _collectionRepo; // phase 3 collection repo
    private readonly CardBackImageService _backImageService = new();
    private readonly CardMetadataResolver _metadataResolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, PhysicallyTwoSidedLayouts, CacheSchemaVersion);
    private TelemetryService? _telemetry; // extracted telemetry service
    // Expose distinct set codes present in current binder specs/cards
    public HashSet<string> GetCurrentSetCodes()
    {
        var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var c in _cards)
                if (!string.IsNullOrWhiteSpace(c.Set)) hs.Add(c.Set.Trim());
        }
        catch { }
        return hs;
    }
    // Exposed refresh method invoked by MainWindow
    public void RefreshQuantities()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentCollectionDir)) { SetStatus("No collection loaded."); return; }
            _collection.Reload(_currentCollectionDir);
            if (!_collection.IsLoaded) { SetStatus("Collection DBs not found."); return; }
            _quantityService.EnrichQuantities(_collection, _cards);
            _quantityService.AdjustMfcQuantities(_cards);
            BuildOrderedFaces();
            RebuildViews();
            Refresh();
            SetStatus("Quantities refreshed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Collection] Refresh failed: {ex.Message}");
            SetStatus("Refresh failed.");
        }
    }

    public void ToggleCardQuantity(CardSlot slot)
    {
        if (slot == null) return;
        if (slot.IsPlaceholderBack) { SetStatus("Back face placeholder"); return; }
        if (string.IsNullOrEmpty(slot.Set) || string.IsNullOrEmpty(slot.Number)) { SetStatus("No set/number"); return; }
        if (string.IsNullOrEmpty(_currentCollectionDir)) { SetStatus("No collection loaded"); return; }
        _collectionRepo.EnsureLoaded(_currentCollectionDir);
        if (!_collection.IsLoaded) { SetStatus("Collection not loaded"); return; }

    _quantityService.ToggleQuantity(slot, _currentCollectionDir, _collection, _cards, _orderedFaces, ResolveCardIdFromDb, SetStatus);
    Refresh();
    }

    // EnsureCollectionLoaded logic moved to CollectionRepository

    private int? ResolveCardIdFromDb(string setOriginal, string baseNum, string trimmed) => _collectionRepo.ResolveCardId(_currentCollectionDir, setOriginal, baseNum, trimmed);
    // Explicit pair keys (e.g. base number + language variant) -> enforced pair placement regardless of name differences
    private readonly Dictionary<CardEntry,string> _explicitVariantPairKeys = new(); // built by VariantPairingService
    private readonly List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs = new(); // captured during parse
    private readonly VariantPairingService _variantPairing = new();
    // _currentViewIndex removed; NavigationService.CurrentIndex is authoritative
    private string? _localBackImagePath; // cached resolved local back image path (or null if not found)
    private static readonly HttpClient Http = CreateClient();
    private class HttpLoggingHandler : DelegatingHandler
    {
        public HttpLoggingHandler(HttpMessageHandler inner) : base(inner) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var sw = Stopwatch.StartNew();
            BinderViewModel.WithVm(vm => vm._telemetry?.Start(url));
            try
            {
                var resp = await base.SendAsync(request, cancellationToken);
                sw.Stop();
                BinderViewModel.WithVm(vm => vm._telemetry?.Done(url, (int)resp.StatusCode, sw.ElapsedMilliseconds));
                return resp;
            }
            catch (Exception)
            {
                sw.Stop();
                BinderViewModel.WithVm(vm => vm._telemetry?.Done(url, -1, sw.ElapsedMilliseconds));
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
    // Local back image resolution moved to CardBackImageService
    public void FlashImageFetch(string cardName)
    {
        try
        {
            _apiFlashCts?.Cancel();
            var cts = new CancellationTokenSource();
            _apiFlashCts = cts;
            Application.Current?.Dispatcher?.Invoke(() => ApiStatus = $"fetching image for {cardName}");
            _ = Task.Run(async () => { try { await Task.Delay(2000, cts.Token); } catch { return; } if (!cts.IsCancellationRequested) Application.Current?.Dispatcher?.Invoke(() => { if (ReferenceEquals(cts, _apiFlashCts)) ApiStatus = string.Empty; }); });
        }
        catch { }
    }
    public void FlashMetaUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _apiFlashCts?.Cancel();
            var cts = new CancellationTokenSource();
            _apiFlashCts = cts;
            Application.Current?.Dispatcher?.Invoke(() => ApiStatus = url);
            _ = Task.Run(async () => { try { await Task.Delay(2000, cts.Token); } catch { return; } if (!cts.IsCancellationRequested) Application.Current?.Dispatcher?.Invoke(() => { if (ReferenceEquals(cts, _apiFlashCts)) ApiStatus = string.Empty; }); });
        }
        catch { }
    }
    private void RefreshSummaryIfIdle() { /* no-op now; counters centralized */ }

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand NextBinderCommand { get; }
    public ICommand PrevBinderCommand { get; }
    public ICommand JumpToPageCommand { get; }
    public ICommand NextSetCommand { get; }
    public ICommand PrevSetCommand { get; }

    private string _jumpBinderInput = "1";
    public string JumpBinderInput { get => _jumpBinderInput; set { _jumpBinderInput = value; OnPropertyChanged(); } }
    private string _jumpPageInput = "1";
    public string JumpPageInput { get => _jumpPageInput; set { _jumpPageInput = value; OnPropertyChanged(); } }

    public BinderViewModel()
    {
        RegisterInstance(this);
        _collectionRepo = new CollectionRepository(_collection);
        if (Environment.GetEnvironmentVariable("ENFOLDERER_HTTP_DEBUG") == "1") _debugHttpLogging = true;
    _telemetry = new TelemetryService(s => UpdatePanel(s), _debugHttpLogging);
        _nav.ViewChanged += NavOnViewChanged;
        var commandFactory = new CommandFactory(_nav,
            () => PagesPerBinder,
            () => _orderedFaces.Count,
            () => JumpBinderInput,
            () => JumpPageInput,
            () => SlotsPerPage,
            () => _orderedFaces);
        NextCommand = commandFactory.CreateNext();
        PrevCommand = commandFactory.CreatePrev();
        FirstCommand = commandFactory.CreateFirst();
        LastCommand  = commandFactory.CreateLast();
        NextBinderCommand = commandFactory.CreateNextBinder();
        PrevBinderCommand = commandFactory.CreatePrevBinder();
        JumpToPageCommand = commandFactory.CreateJumpToPage();
        NextSetCommand = commandFactory.CreateNextSet();
        PrevSetCommand = commandFactory.CreatePrevSet();
        RebuildViews();
        Refresh();
        UpdatePanel();
    }

        public static void SetImageUrlName(string url, string name)
        { WithVm(vm => vm._telemetry?.SetImageUrlName(url, name)); }

    // NavigationService now owns all navigation (page, binder, set, first/last/next/prev)
    private void NavOnViewChanged()
    {
        Refresh();
    }

    // Legacy set jump logic removed; NavigationService handles set navigation

    private void RebuildViews()
    {
        _nav.Rebuild(_orderedFaces.Count, SlotsPerPage, PagesPerBinder);
    }

    // Removed legacy synchronous LoadFromFile method (was unused and empty)

    // New format loader (async):
    // Lines:
    // # comment
    // =[SETCODE]
    // number;[optional name override]
    // numberStart-numberEnd  (inclusive range) optionally followed by ; prefix for name hints (ignored here)
    public async Task LoadFromFileAsync(string path)
    {
        // Recompute slot theme seed
        try { var fi = new FileInfo(path); CardSlotTheme.Recalculate(path + fi.LastWriteTimeUtc.Ticks); } catch { CardSlotTheme.Recalculate(path); }
    var parser = new BinderFileParser(_binderTheme, _metadataResolver, log => _backImageService.Resolve(_currentCollectionDir, log), IsCacheComplete);
        var parseResult = await parser.ParseAsync(path, SlotsPerPage);
        _currentFileHash = parseResult.FileHash;
        _currentCollectionDir = parseResult.CollectionDir;
        if (parseResult.PagesPerBinderOverride.HasValue) PagesPerBinder = parseResult.PagesPerBinderOverride.Value;
        if (!string.IsNullOrEmpty(parseResult.LayoutModeOverride)) LayoutMode = parseResult.LayoutModeOverride;
        if (parseResult.HttpDebugEnabled) _debugHttpLogging = true;
        _localBackImagePath = parseResult.LocalBackImagePath; // may be null
        _cards.Clear();
        _specs.Clear();
        _mfcBacks.Clear();
        _orderedFaces.Clear();
        _pendingExplicitVariantPairs.Clear();
        if (parseResult.CacheHit)
        {
            _cards.AddRange(parseResult.CachedCards);
            Status = "Loaded metadata from cache.";
            BuildOrderedFaces();
            _nav.ResetIndex();
            RebuildViews();
            Refresh();
            return;
        }
        foreach (var ps in parseResult.Specs)
        {
            var cs = new CardSpec(ps.SetCode, ps.Number, ps.OverrideName, ps.ExplicitEntry, ps.NumberDisplayOverride)
            {
                Resolved = ps.Resolved
            };
            _specs.Add(cs);
        }
        foreach (var p in parseResult.PendingVariantPairs) _pendingExplicitVariantPairs.Add(p);
        await ResolveSpecsWithServiceAsync(parseResult.FetchList, parseResult.InitialSpecIndexes);
        RebuildCardListFromSpecs();
        // Load collection DBs
        if (!string.IsNullOrEmpty(_currentCollectionDir))
        {
            try { _collection.Load(_currentCollectionDir); } catch (Exception ex) { Debug.WriteLine($"[Collection] Load failed (db): {ex.Message}"); }
            if (_collection.IsLoaded && _collection.Quantities.Count > 0)
            {
                try { _quantityService.EnrichQuantities(_collection, _cards); _quantityService.AdjustMfcQuantities(_cards); } catch (Exception ex) { Debug.WriteLine($"[Collection] Enrichment failed: {ex.Message}"); }
            }
        }
        Status = $"Initial load {_cards.Count} faces (placeholders included).";
        BuildOrderedFaces();
        _nav.ResetIndex();
        RebuildViews();
        Refresh();
        // Background fetch of remaining specs
        _ = Task.Run(async () =>
        {
            var remaining = new HashSet<int>();
            for (int i = 0; i < _specs.Count; i++) if (!parseResult.InitialSpecIndexes.Contains(i)) remaining.Add(i);
            if (remaining.Count == 0) return;
            await ResolveSpecsWithServiceAsync(parseResult.FetchList, remaining, updateInterval:15);
            Application.Current.Dispatcher.Invoke(() =>
            {
                RebuildCardListFromSpecs();
                if (_collection.IsLoaded) _quantityService.EnrichQuantities(_collection, _cards);
                if (_collection.IsLoaded) _quantityService.AdjustMfcQuantities(_cards);
                BuildOrderedFaces();
                RebuildViews();
                Refresh();
                Status = $"All metadata loaded ({_cards.Count} faces).";
                _metadataResolver.PersistMetadataCache(_currentFileHash, _cards);
                _metadataResolver.MarkCacheComplete(_currentFileHash);
            });
        });
    }

    // Quantity enrichment & MFC adjustment moved to CardQuantityService (Phase 2)

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
    private string MetaCacheDir => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "meta");
    private string MetaCacheDonePath(string hash) => System.IO.Path.Combine(MetaCacheDir, hash + ".done");
    private bool IsCacheComplete(string hash) => File.Exists(MetaCacheDonePath(hash));

    private async Task ResolveSpecsWithServiceAsync(List<(string setCode,string number,string? nameOverride,int specIndex)> fetchList, HashSet<int> targetIndexes, int updateInterval = 5)
    {
        int total = targetIndexes.Count;
        await _metadataResolver.ResolveSpecsAsync(
            fetchList,
            targetIndexes,
            done =>
            {
                if (done % updateInterval == 0 || done == total)
                {
                    Status = $"Resolving metadata {done}/{total} ({(int)(done*100.0/Math.Max(1,total))}%)";
                }
                return done;
            },
            (specIndex, resolved) =>
            {
                if (specIndex < 0 || specIndex >= _specs.Count) return (null,false);
                if (resolved != null)
                {
                    if (resolved.IsBackFace)
                    {
                        _mfcBacks[specIndex] = resolved;
                    }
                    else
                    {
                        _specs[specIndex] = _specs[specIndex] with { Resolved = resolved };
                    }
                }
                return (null,false);
            },
            async (set, num, nameOverride) => await FetchCardMetadataAsync(set, num, nameOverride)
        );
    }

    private void RebuildCardListFromSpecs()
    {
        _cards.Clear();
    _explicitVariantPairKeys.Clear();
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
    var built = _variantPairing.BuildExplicitPairKeyMap(_cards, _pendingExplicitVariantPairs);
    foreach (var kv in built) _explicitVariantPairKeys[kv.Key] = kv.Value;
    }

    // CardSpec record extracted to CardSpec.cs (Phase 1 refactor)

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
    var service = new FaceLayoutService(new PairGroupingAnalyzer());
    var ordered = service.BuildOrderedFaces(_cards, LayoutMode, SlotsPerPage, ColumnsPerPage, _explicitVariantPairKeys);
    _orderedFaces.AddRange(ordered);
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
    var view = _views[_nav.CurrentIndex];
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
    int idx = binderNumber - 1;
    Brush baseBrush = _binderTheme.GetBrushForBinder(idx);
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

    // Color token parsing moved into BinderThemeService

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
            await ResolveSpecsWithServiceAsync(quickList, neededSpecs, updateInterval: 3);
    await ResolveSpecsWithServiceAsync(quickList, neededSpecs, updateInterval: 3);
            Application.Current.Dispatcher.Invoke(() =>
            {
                RebuildCardListFromSpecs();
                BuildOrderedFaces();
                bool faceCountChanged = _orderedFaces.Count != preFaceCount;
                if (faceCountChanged)
                {
                    // Page boundaries depend on total face count; rebuild them to avoid duplicated fronts after new MFC backs appear.
                    RebuildViews();
                }
                // redraw current view
                if (_nav.CurrentIndex < _views.Count)
                {
                    var v = _views[_nav.CurrentIndex];
                    LeftSlots.Clear(); RightSlots.Clear();
                    if (v.LeftPage.HasValue) FillPage(LeftSlots, v.LeftPage.Value);
                    if (v.RightPage.HasValue) FillPage(RightSlots, v.RightPage.Value);
                }
            });
        });
    }
}


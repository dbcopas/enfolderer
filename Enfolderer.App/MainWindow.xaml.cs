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

    private readonly StatusFlashService _statusFlash = new();
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
    private NavigationViewBuilder? _navBuilder; // deferred until ctor end
    private IReadOnlyList<NavigationService.PageView> _views => _nav.Views; // proxy for legacy references
    private readonly CardCollectionData _collection = new(); // collection DB data
    private readonly CardQuantityService _quantityService = new(); // phase 2 extracted quantity logic
    private readonly QuantityEnrichmentService _quantityEnrichment;
    private readonly CollectionRepository _collectionRepo; // phase 3 collection repo
    private readonly CardBackImageService _backImageService = new();
    private readonly CardMetadataResolver _metadataResolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, PhysicallyTwoSidedLayouts, CacheSchemaVersion);
    private readonly BinderLoadService _binderLoadService;
    private readonly SpecResolutionService _specResolutionService;
    private readonly MetadataLoadOrchestrator _metadataOrchestrator;
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
    private readonly Dictionary<string,string> _explicitVariantPairKeys = new(); // built by VariantPairingService (key: Set:Number)
    private readonly List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs = new(); // captured during parse
    private readonly VariantPairingService _variantPairing = new();
    // _currentViewIndex removed; NavigationService.CurrentIndex is authoritative
    private string? _localBackImagePath; // cached resolved local back image path (or null if not found)
    internal static readonly HttpClient Http = CreateClient();
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
    _statusFlash.Flash($"fetching image for {cardName}", TimeSpan.FromSeconds(2), s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
    }
    public void FlashMetaUrl(string url)
    {
    if (string.IsNullOrWhiteSpace(url)) return;
    _statusFlash.Flash(url, TimeSpan.FromSeconds(2), s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
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
    _cachePaths = new CachePathService(ImageCacheStore.CacheRoot);
    _statusPanel = new StatusPanelService(s => ApiStatus = s);
    _telemetry = new TelemetryService(s => _statusPanel.Update(s, s2 => ApiStatus = s2), _debugHttpLogging);
    _binderLoadService = new BinderLoadService(_binderTheme, _metadataResolver, _backImageService, hash => _cachePaths.IsMetaComplete(hash));
    _specResolutionService = new SpecResolutionService(_metadataResolver);
    _metadataOrchestrator = new MetadataLoadOrchestrator(_specResolutionService, _quantityService, _metadataResolver);
    _quantityEnrichment = new QuantityEnrichmentService(_quantityService);
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
    _statusPanel.Update(null, s => ApiStatus = s);
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
    _navBuilder ??= new NavigationViewBuilder(_nav);
    _navBuilder.Rebuild(_orderedFaces.Count, SlotsPerPage, PagesPerBinder);
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
        var load = await _binderLoadService.LoadAsync(path, SlotsPerPage);
        _currentFileHash = load.FileHash;
        _currentCollectionDir = load.CollectionDir;
        if (load.PagesPerBinderOverride.HasValue) PagesPerBinder = load.PagesPerBinderOverride.Value;
        if (!string.IsNullOrEmpty(load.LayoutModeOverride)) LayoutMode = load.LayoutModeOverride;
        if (load.HttpDebugEnabled) _debugHttpLogging = true;
        _localBackImagePath = load.LocalBackImagePath;
        _cards.Clear(); _specs.Clear(); _mfcBacks.Clear(); _orderedFaces.Clear(); _pendingExplicitVariantPairs.Clear();
        if (load.CacheHit)
        {
            _cards.AddRange(load.CachedCards);
            Status = "Loaded metadata from cache.";
            BuildOrderedFaces(); _nav.ResetIndex(); RebuildViews(); Refresh();
            return;
        }
        await _metadataOrchestrator.RunInitialAsync(
            load,
            _currentCollectionDir,
            _specs,
            _mfcBacks,
            _cards,
            _orderedFaces,
            _specs,
            _pendingExplicitVariantPairs,
            s => Status = s,
            () => _collection.IsLoaded,
            _collection,
            RebuildCardListFromSpecs,
            BuildOrderedFaces,
            () => { _nav.ResetIndex(); RebuildViews(); },
            Refresh,
            () => _metadataResolver.PersistMetadataCache(_currentFileHash, _cards),
            () => _metadataResolver.MarkCacheComplete(_currentFileHash)
        );
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
    private readonly CachePathService _cachePaths; // centralized cache path logic
    private readonly StatusPanelService _statusPanel; // status panel abstraction

    // Spec resolution now handled by SpecResolutionService

    private void RebuildCardListFromSpecs()
    {
        var builder = new CardListBuilder(_variantPairing);
        var (cards, pairMap) = builder.Build(_specs, _mfcBacks, _pendingExplicitVariantPairs);
        _cards.Clear(); _cards.AddRange(cards);
        _explicitVariantPairKeys.Clear(); foreach (var kv in pairMap)
        {
            var key = (kv.Key.Set ?? "") + ":" + kv.Key.Number;
            _explicitVariantPairKeys[key] = kv.Value;
        }
    }

    // CardSpec record extracted to CardSpec.cs (Phase 1 refactor)

    // FetchCardMetadataAsync moved into CardMetadataResolver / SpecResolutionService

    private readonly FaceOrderingService _faceOrdering = new(); // singleton instance reused
    private void BuildOrderedFaces()
    {
        _orderedFaces.Clear();
        if (_cards.Count == 0) return;
        var ordered = _faceOrdering.BuildOrderedFaces(_cards, LayoutMode, SlotsPerPage, ColumnsPerPage, _explicitVariantPairKeys);
        _orderedFaces.AddRange(ordered);
    }

    private void Refresh()
    {
        var presenter = new PageViewPresenter();
        var result = presenter.Present(_nav, LeftSlots, RightSlots, _orderedFaces, SlotsPerPage, PagesPerBinder, Http, _binderTheme);
        PageDisplay = result.PageDisplay; OnPropertyChanged(nameof(PageDisplay));
        BinderBackground = result.BinderBackground; OnPropertyChanged(nameof(BinderBackground));
        if (_nav.Views.Count>0)
        {
            var v = _nav.Views[_nav.CurrentIndex];
            TriggerPageResolution(v.LeftPage ?? 0, v.RightPage ?? 0);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateBinderBackground(int binderNumber)
    {
    BinderBackground = _binderTheme.CreateBinderBackground(binderNumber - 1);
    }

    // FillPage logic moved into PageSlotBuilder

    // Color token parsing moved into BinderThemeService

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly PageResolutionBatcher _pageBatcher = new();
    private void TriggerPageResolution(params int[] pageNumbers)
    {
        _pageBatcher.Trigger(pageNumbers, SlotsPerPage, _specs, _mfcBacks, _orderedFaces, _explicitVariantPairKeys,
            _specResolutionService,
            () => Status,
            s => Status = s,
            RebuildCardListFromSpecs,
            BuildOrderedFaces,
            RebuildViews,
            _nav,
            _views,
            LeftSlots,
            RightSlots);
    }
}


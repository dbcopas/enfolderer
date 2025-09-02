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


public class BinderViewModel : INotifyPropertyChanged, IStatusSink
{
    // BinderViewModel orchestrates UI state: layout config, navigation, metadata & quantity updates, and delegates heavy logic to extracted services.
    // It now mainly wires services together and exposes observable properties for binding.
    private string? _currentCollectionDir;
    public string? CurrentCollectionDir => _currentCollectionDir;
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


    public ObservableCollection<CardSlot> LeftSlots { get; } = new();
    public ObservableCollection<CardSlot> RightSlots { get; } = new();
    private string _pageDisplay = string.Empty;
    public string PageDisplay { get => _pageDisplay; private set { if (_pageDisplay!=value) { _pageDisplay = value; OnPropertyChanged(); } } }
    private Brush _binderBackground = Brushes.Black;
    public Brush BinderBackground { get => _binderBackground; private set { if (_binderBackground!=value) { _binderBackground = value; OnPropertyChanged(); } } }
    private readonly BinderThemeService _binderTheme = new();
    private readonly Random _rand = new(12345);
    private int _rowsPerPage = 3;
    private int _columnsPerPage = 4;
    public int RowsPerPage { get => _rowsPerPage; set { if (value>0 && value!=_rowsPerPage) { _rowsPerPage = value; OnPropertyChanged(); RecomputeAfterLayoutChange(); } } }
    public int ColumnsPerPage { get => _columnsPerPage; set { if (value>0 && value!=_columnsPerPage) { _columnsPerPage = value; OnPropertyChanged(); RecomputeAfterLayoutChange(); } } }
    public int SlotsPerPage => RowsPerPage * ColumnsPerPage;
    private int _pagesPerBinder = 40; // displayed sides per binder (not physical sheets)
    public int PagesPerBinder { get => _pagesPerBinder; set { if (value>0 && value!=_pagesPerBinder) { _pagesPerBinder = value; OnPropertyChanged(); RebuildViews(); Refresh(); } } }
    private string _layoutMode = "4x3"; // UI selection token
    public string LayoutMode { get => _layoutMode; set { if (!string.Equals(_layoutMode, value, StringComparison.OrdinalIgnoreCase)) { _layoutMode = value; OnPropertyChanged(); ApplyLayoutModeToken(); } } }
    private LayoutConfigService _layoutConfig = new();
    private void ApplyLayoutModeToken()
    {
        var (r,c,canon) = _layoutConfig.ApplyToken(_layoutMode);
        RowsPerPage = r; ColumnsPerPage = c; if (_layoutMode != canon) { _layoutMode = canon; OnPropertyChanged(nameof(LayoutMode)); }
    }
    private void RecomputeAfterLayoutChange()
    {
        BuildOrderedFaces();
        RebuildViews();
        Refresh();
    }
    private readonly BinderSession _session = new();
    private List<CardEntry> _cards => _session.Cards;
    private List<CardEntry> _orderedFaces => _session.OrderedFaces; // reordered faces honoring placement constraints
    private List<CardSpec> _specs => _session.Specs; // raw specs in file order
    private System.Collections.Concurrent.ConcurrentDictionary<int, CardEntry> _mfcBacks => _session.MfcBacks; // synthetic back faces keyed by spec index
    private readonly NavigationService _nav = new(); // centralized navigation
    private NavigationViewBuilder? _navBuilder; // deferred until ctor end
    private IReadOnlyList<NavigationService.PageView> _views => _nav.Views; // proxy for legacy references
    private readonly CardCollectionData _collection = new();
    private readonly CardQuantityService _quantityService = new();
    private readonly QuantityEnrichmentService _quantityEnrichment;
    private readonly CollectionRepository _collectionRepo; // phase 3 collection repo
    private readonly CardBackImageService _backImageService = new();
    private readonly CardMetadataResolver _metadataResolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, PhysicallyTwoSidedLayouts, CacheSchemaVersion);
    private readonly BinderLoadService _binderLoadService;
    private readonly SpecResolutionService _specResolutionService;
    private readonly MetadataLoadOrchestrator _metadataOrchestrator;
    private TelemetryService? _telemetry;
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

    private readonly QuantityToggleService _quantityToggleService;
    public void ToggleCardQuantity(CardSlot slot)
    {
        _quantityToggleService.Toggle(slot, _currentCollectionDir, _cards, _orderedFaces, ResolveCardIdFromDb, SetStatus);
        Refresh();
    }

    private int? ResolveCardIdFromDb(string setOriginal, string baseNum, string trimmed) => _collectionRepo.ResolveCardId(_currentCollectionDir, setOriginal, baseNum, trimmed);
    private System.Collections.Generic.Dictionary<string,string> _explicitVariantPairKeys => _session.ExplicitVariantPairKeys; // Set:Number -> pair id
    private System.Collections.Generic.List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs => _session.PendingExplicitVariantPairs;
    private readonly VariantPairingService _variantPairing = new();
    // local back image path lives in session
    internal static HttpClient Http => _httpFactory?.Client ?? throw new InvalidOperationException("HTTP factory not initialized");
    private static IHttpClientFactoryService? _httpFactory; // static so slots can reference BinderViewModel.Http
    // Local back image resolution moved to CardBackImageService
    public void FlashImageFetch(string cardName) => _statusFlash.FlashImageFetch(cardName, s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
    public void FlashMetaUrl(string url) => _statusFlash.FlashMetaUrl(url, s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
    private void RefreshSummaryIfIdle() { }

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
    _httpFactory = new HttpClientFactoryService(_telemetry);
    _binderLoadService = new BinderLoadService(_binderTheme, _metadataResolver, _backImageService, hash => _cachePaths.IsMetaComplete(hash));
    _specResolutionService = new SpecResolutionService(_metadataResolver);
    _metadataOrchestrator = new MetadataLoadOrchestrator(_specResolutionService, _quantityService, _metadataResolver);
    _quantityEnrichment = new QuantityEnrichmentService(_quantityService);
        _quantityToggleService = new QuantityToggleService(_quantityService, _collectionRepo, _collection);
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

    private void NavOnViewChanged()
    {
        Refresh();
    }

    private void RebuildViews()
    {
    _navBuilder ??= new NavigationViewBuilder(_nav);
    _navBuilder.Rebuild(_orderedFaces.Count, SlotsPerPage, PagesPerBinder);
    }

    // Load binder file (async). Supported lines: comments (#), set headers (=SET), single numbers, ranges start-end, optional ;name overrides.
    public async Task LoadFromFileAsync(string path)
    {
        var load = await _binderLoadService.LoadAsync(path, SlotsPerPage);
    _session.CurrentFileHash = load.FileHash;
        _currentCollectionDir = load.CollectionDir;
        if (load.PagesPerBinderOverride.HasValue) PagesPerBinder = load.PagesPerBinderOverride.Value;
        if (!string.IsNullOrEmpty(load.LayoutModeOverride)) LayoutMode = load.LayoutModeOverride;
        if (load.HttpDebugEnabled) _debugHttpLogging = true;
    _session.LocalBackImagePath = load.LocalBackImagePath;
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
            () => _metadataResolver.PersistMetadataCache(_session.CurrentFileHash!, _cards),
            () => _metadataResolver.MarkCacheComplete(_session.CurrentFileHash!)
        );
    }

    private const int CacheSchemaVersion = 5; // bump: refined two-sided classification & invalidating prior misclassification cache
    private static readonly HashSet<string> PhysicallyTwoSidedLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"
    };
    private static readonly HashSet<string> SingleFaceMultiLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "split","aftermath","adventure","meld","flip","leveler","saga","class","plane","planar","scheme","vanguard","token","emblem","art_series"
    };
    private readonly CachePathService _cachePaths;
    private readonly StatusPanelService _statusPanel;

    private void RebuildCardListFromSpecs()
    {
        var builder = new CardListBuilder(_variantPairing);
    var (cards, pairMap) = builder.Build(_specs, _mfcBacks, _pendingExplicitVariantPairs);
        _cards.Clear(); _cards.AddRange(cards);
    _explicitVariantPairKeys.Clear(); foreach (var kv in pairMap) _explicitVariantPairKeys[kv.Key] = kv.Value;
    }

    private readonly FaceOrderingService _faceOrdering = new();
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


using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Enfolderer.App.Imaging;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Importing;
using Enfolderer.App.Collection;
using Enfolderer.App.Quantity;
using Enfolderer.App.Layout;
using Enfolderer.App.Binder;
using Enfolderer.App.Metadata;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Utilities;

namespace Enfolderer.App;

public record PriceSegment(string Text, Brush? Color, FontWeight Weight);
public record MissingPriceOutlierSummary(int PricedCount, int InlierCount, int OutlierCount, decimal Total, decimal TrimmedTotal, decimal OutlierTotal);

public class NullToUnsetConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value ?? DependencyProperty.UnsetValue;
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class BinderViewModel : INotifyPropertyChanged, IStatusSink
{
    // BinderViewModel orchestrates UI state: layout config, navigation, metadata & quantity updates, and delegates heavy logic to extracted services.
    // It now mainly wires services together and exposes observable properties for binding.
    // Collection directory is now fixed to the executable's directory so the app always
    // looks beside the running EXE for mainDb.db and mtgstudio.collection.
    // (Requirement: always & only look in exe directory for the two databases.)
    private readonly string _currentCollectionDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    public string? CurrentCollectionDir => _currentCollectionDir;
    private static BinderViewModel? _singleton;
    private static readonly object _singletonLock = new();
    public static void RegisterInstance(BinderViewModel vm) { lock(_singletonLock) _singleton = vm; }
    public static void WithVm(Action<BinderViewModel> action) { BinderViewModel? vm; lock(_singletonLock) vm = _singleton; if (vm!=null) { try { action(vm); } catch (System.Exception) { throw; } } }

    private readonly StatusFlashService _statusFlash = new();
    private string _apiStatus = string.Empty;
    public string ApiStatus { get => _apiStatus; private set { if (_apiStatus!=value) { _apiStatus = value; OnPropertyChanged(); } } }
    private string _httpPanel = string.Empty; // aggregated HTTP status / counters
    public string HttpPanel { get => _httpPanel; private set { if (_httpPanel!=value) { _httpPanel = value; OnPropertyChanged(); } } }

    private string _status = string.Empty;
    public string Status { get => _status; private set { if (_status!=value) { _status = value; OnPropertyChanged(); } } }
    public void SetStatus(string message) => Status = message;

    private static readonly object _httpLogLock = new();
    private static readonly ConcurrentDictionary<string,string> _imageUrlNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static string HttpLogPath => System.IO.Path.Combine(ImageCacheStore.CacheRoot, "http-log.txt");


    public ObservableCollection<CardSlot> LeftSlots { get; } = new();
    public ObservableCollection<CardSlot> RightSlots { get; } = new();
    private string _pageDisplay = string.Empty;
    public string PageDisplay { get => _pageDisplay; private set { if (_pageDisplay!=value) { _pageDisplay = value; OnPropertyChanged(); } } }
    public ObservableCollection<PriceSegment> SetMissingPriceSegments { get; } = new();
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
        
    // Delegate fallback enrichment to coordinator (extracted from VM)
    _quantityCoordinator.LayoutChangeFallback(_collection, _quantityService, _cards, BuildOrderedFaces, RebuildViews, Refresh, debug:false);
    }
    private readonly BinderSession _session = new();
    private List<CardEntry> _cards => _session.Cards;
    public IReadOnlyList<CardEntry> Cards => _session.Cards;
    private List<CardEntry> _orderedFaces => _session.OrderedFaces; // reordered faces honoring placement constraints
    private List<CardSpec> _specs => _session.Specs; // raw specs in file order
    private System.Collections.Concurrent.ConcurrentDictionary<int, CardEntry> _mfcBacks => _session.MfcBacks; // synthetic back faces keyed by spec index
    // Explicitly reference layout navigation service to avoid ambiguity with System.Windows.Navigation.NavigationService
    private readonly Enfolderer.App.Layout.NavigationService _nav = new(); // centralized navigation
    private Enfolderer.App.Binder.NavigationViewBuilder? _navBuilder; // deferred until ctor end
    private IReadOnlyList<Enfolderer.App.Layout.NavigationService.PageView> _views => _nav.Views; // proxy for legacy references
    private readonly Enfolderer.App.Collection.CardCollectionData _collection = new();
    public Enfolderer.App.Collection.CardCollectionData Collection => _collection;
    private readonly Enfolderer.App.Quantity.CardQuantityService _quantityService; // repository-backed instance created in ctor
    private readonly Enfolderer.App.Quantity.QuantityEnrichmentCoordinator _quantityCoordinator = new();
    private readonly Enfolderer.App.Collection.CollectionRepository _collectionRepo; // repository
    private readonly CardBackImageService _backImageService = new();
    // Concrete resolver retained internally by core graph; VM now only holds higher-level provider abstraction.
    private readonly Enfolderer.App.Core.Abstractions.IMetadataProvider _metadataProvider;
    private readonly Enfolderer.App.Core.Abstractions.IMetadataCachePersistence _cachePersistence;
    private readonly Enfolderer.App.Core.Abstractions.IBinderLoadService _binderLoadService;
    private readonly SpecResolutionService _specResolutionService;
    private readonly Enfolderer.App.Metadata.MetadataLoadOrchestrator _metadataOrchestrator;
    private readonly Enfolderer.App.Core.Abstractions.ICardArrangementService? _arrangementService; // injected arrangement adapter
    private Enfolderer.App.Metadata.PriceBackfillService? _priceBackfill;
    private System.Threading.CancellationTokenSource? _backfillCts;
    public IImportService ImportService { get; private set; } = default!; // injected
    private TelemetryService? _telemetry;
    public HashSet<string> GetCurrentSetCodes()
    {
        var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var c in _cards)
                if (!string.IsNullOrWhiteSpace(c.Set)) hs.Add(c.Set.Trim());
        }
    catch (System.Exception) { throw; }
        return hs;
    }
    public void RefreshQuantities()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentCollectionDir)) { SetStatus("No collection loaded."); return; }
            _collection.Reload(_currentCollectionDir);
            if (!_collection.IsLoaded) { SetStatus("Collection DBs not found."); return; }
            // Unified path: service exposes ApplyAll; orchestrator kept for external consumers
            _quantityService.ApplyAll(_collection, _cards);
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

    private readonly Enfolderer.App.Quantity.QuantityToggleService _quantityToggleService;
    public void ToggleCardQuantity(CardSlot slot)
    {
        // Toggle underlying quantity (service returns new logical quantity or -1 on failure)
        _quantityToggleService.Toggle(slot, _currentCollectionDir, _cards, _orderedFaces, ResolveCardIdFromDb, SetStatus);
        // Instead of rebuilding immediately (which recreates CardSlot instances and can defer the visual until after async image work),
        // directly update any existing slot objects bound to the same logical card (matching on Set + Number).
        int newQty = slot.Quantity;
        if (newQty >= 0)
        {
            int logical = newQty;
        int FrontDisplay(int q) => q <= 0 ? 0 : q == 1 ? 1 : 2;
        int BackDisplay(int q) => q >= 2 ? 2 : 0; // logical 1 => 0 dim
            void apply(ObservableCollection<CardSlot> slots)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (!string.Equals(s.Set, slot.Set, StringComparison.OrdinalIgnoreCase) || !string.Equals(s.Number, slot.Number, StringComparison.OrdinalIgnoreCase)) continue;
            int target = s.IsBackFace ? BackDisplay(logical) : FrontDisplay(logical);
                    if (s.Quantity != target) s.Quantity = target;
                }
            }
            apply(LeftSlots);
            apply(RightSlots);
        }
    // We intentionally avoid a full enrichment pass here (previous redundancy removed) because ToggleQuantity already
    // updated in-memory card entries, orderedFaces, and collection dictionaries. This keeps click latency minimal.
        // Skip immediate full page refresh to preserve existing CardSlot instances and let binding update in-place.
        // Schedule a lightweight ordered-face rebuild later so navigation remains consistent (no immediate UI churn).
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            BuildOrderedFaces();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private int? ResolveCardIdFromDb(string setOriginal, string baseNum, string trimmed) => _collectionRepo.ResolveCardId(_currentCollectionDir, setOriginal, baseNum, trimmed);
    private System.Collections.Generic.Dictionary<string,string> _explicitVariantPairKeys => _session.ExplicitVariantPairKeys; // Set:Number -> pair id
    private System.Collections.Generic.List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs => _session.PendingExplicitVariantPairs;
    private readonly VariantPairingService _variantPairing = new();
    // local back image path lives in session
    private IHttpClientFactoryService? _httpFactory; // instance now
    // Local back image resolution moved to CardBackImageService
    public void FlashImageFetch(string cardName) => _statusFlash.FlashImageFetch(cardName, s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
    public void FlashMetaUrl(string url) => _statusFlash.FlashMetaUrl(url, s => Application.Current?.Dispatcher?.Invoke(() => ApiStatus = s));
    public void UpdateHttpPanel(string line)
    {
        // Keep last 1 line for now; could extend to rolling log.
        HttpPanel = line;
    }

    public int RunBackfillZeroQty(int threshold = 150000)
    {
        try
        {
            var log = Enfolderer.App.Core.Logging.LogHost.Sink;
            var repo = new Enfolderer.App.Collection.CollectionRepository(_collection, log);
            return repo.BackfillZeroQuantityRowsUnderThreshold(threshold, qtyDebug: true);
        }
        catch { return 0; }
    }

    public int RunSetQtyEqualsCardId()
    {
        try
        {
            var log = Enfolderer.App.Core.Logging.LogHost.Sink;
            var repo = new Enfolderer.App.Collection.CollectionRepository(_collection, log);
            return repo.SetQuantityEqualsCardIdAll(qtyDebug: true);
        }
        catch { return 0; }
    }

    public bool RunRestoreCollectionBackup()
    {
        try
        {
            var log = Enfolderer.App.Core.Logging.LogHost.Sink;
            var repo = new Enfolderer.App.Collection.CollectionRepository(_collection, log);
            return repo.RestoreCollectionBackup(qtyDebug: true);
        }
        catch { return false; }
    }
    private void RefreshSummaryIfIdle() { }

    public BinderViewModel()
    {
        RegisterInstance(this);
    // Use bootstrapper exclusively for service construction (eliminate duplicated manual wiring here)
    var localCachePaths = new CachePathService(ImageCacheStore.CacheRoot);
    _cachePaths = localCachePaths;
    var boot = Enfolderer.App.Core.Composition.AppBootstrapper.Build(ImageCacheStore.CacheRoot, _binderTheme, hash => localCachePaths.IsMetaComplete(hash));
    _collectionRepo = boot.CollectionRepo;
    _quantityService = boot.QuantityService;
    _statusPanel = boot.StatusPanel;
    _telemetry = boot.Telemetry;
    _httpFactory = boot.HttpFactory;
    _metadataProvider = boot.MetadataProvider; // currently unused directly; placeholder for future direct calls if needed
    _binderLoadService = boot.CoreGraph.BinderLoad;
    _specResolutionService = boot.CoreGraph.SpecResolution;
    _metadataOrchestrator = boot.CoreGraph.Orchestrator;
    _quantityCoordinator = boot.QuantityCoordinator;
    _quantityToggleService = boot.QuantityToggle as Enfolderer.App.Quantity.QuantityToggleService ?? new Enfolderer.App.Quantity.QuantityToggleService(_quantityService, _collectionRepo, _collection);
        _cachePersistence = boot.CachePersistence; // ensure existing field assigned
        _arrangementService = boot.ArrangementService;
    _priceBackfill = new Enfolderer.App.Metadata.PriceBackfillService(
        boot.CoreGraph.ResolverAdapter,
        msg => Application.Current?.Dispatcher?.BeginInvoke(() => SetStatus(msg)),
        () => PriceDisplayMode,
        () => Application.Current?.Dispatcher?.BeginInvoke(UpdateSetMissingPrice));
    ImportService = boot.ImportService;
    _pagePresenter = boot.PagePresenter;
    _pageBatcher = boot.PageBatcher;
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
        SearchNextCommand = new RelayCommand(_ => PerformSearchNext(), _ => !string.IsNullOrWhiteSpace(SearchName) && _orderedFaces.Count > 0);
        RebuildViews();
        Refresh();
    _statusPanel.Update(null, s => ApiStatus = s);
    }

    // CSV import progress UI
    private bool _importInProgress;
    public bool ImportInProgress { get => _importInProgress; private set { if (_importInProgress != value) { _importInProgress = value; OnPropertyChanged(); } } }
    private int _importProgress;
    public int ImportProgress { get => _importProgress; private set { if (_importProgress != value) { _importProgress = value; OnPropertyChanged(); } } }
    private string _importProgressText = string.Empty;
    public string ImportProgressText { get => _importProgressText; private set { if (_importProgressText != value) { _importProgressText = value; OnPropertyChanged(); } } }
    public void StartImportProgress(string stage)
    {
        Application.Current?.Dispatcher?.Invoke(() => { ImportInProgress = true; ImportProgress = 0; ImportProgressText = stage + ": 0%"; });
    }
    public void ReportImportProgress(int processed, int total)
    {
        if (total <= 0) return;
        int pct = (int)Math.Round(100.0 * processed / total);
        Application.Current?.Dispatcher?.Invoke(() => { ImportProgress = Math.Clamp(pct, 0, 100); ImportProgressText = $"Import: {processed}/{total} ({ImportProgress}%)"; });
    }
    public void FinishImportProgress()
    {
        Application.Current?.Dispatcher?.Invoke(() => { ImportProgress = 100; ImportProgressText = "Done"; ImportInProgress = false; });
    }

        public static void SetImageUrlName(string url, string name)
        { WithVm(vm => vm._telemetry?.SetImageUrlName(url, name)); }

    private void NavOnViewChanged()
    {
        Refresh();
        // Attempt to apply pending highlight after view change
        ApplyHighlightIfVisible();
    }

    private void RebuildViews()
    {
    _navBuilder ??= new NavigationViewBuilder(_nav);
    _navBuilder.Rebuild(_orderedFaces.Count, SlotsPerPage, PagesPerBinder);
    }

    private void Refresh()
    {
    // Coordinator handles fallback enrichment if any -1 quantities remain.
    _quantityCoordinator.EnrichFallbackIfNeeded(_collection, _quantityService, _cards, BuildOrderedFaces, Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug);
    var result = _pagePresenter.Present(_nav, LeftSlots, RightSlots, _orderedFaces, SlotsPerPage, PagesPerBinder, _httpFactory!.Client, _binderTheme);
        PageDisplay = result.PageDisplay; OnPropertyChanged(nameof(PageDisplay));
        BinderBackground = result.BinderBackground; OnPropertyChanged(nameof(BinderBackground));
        UpdateSetMissingPrice();
        if (_nav.Views.Count>0)
        {
            var v = _nav.Views[_nav.CurrentIndex];
            TriggerPageResolution(v.LeftPage ?? 0, v.RightPage ?? 0);
        }
        CommandManager.InvalidateRequerySuggested();
        // After refresh may have rebuilt slots; try to apply highlight
        ApplyHighlightIfVisible();
        // Apply price display mode to newly-created slots
        RefreshVisiblePrices();
        // Lazily backfill EUR prices for visible missing cards
        if (_priceBackfill != null && _httpFactory != null)
        {
            // Cancel any previous backfill so only the latest Refresh()'s backfill runs
            _backfillCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _backfillCts = cts;
            var visibleSlots = LeftSlots.Concat(RightSlots).ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _priceBackfill.BackfillVisibleAsync(visibleSlots, _orderedFaces, _httpFactory.Client, cts.Token);
                }
                catch (Exception ex)
                {
                    Enfolderer.App.Core.Logging.LogHost.Sink?.Log($"[PriceBackfill] Top-level exception: {ex}", "Price");
                }
            });
        }
    }

    private void UpdateBinderBackground(int binderNumber)
    {
    BinderBackground = _binderTheme.CreateBinderBackground(binderNumber - 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly PageResolutionBatcher _pageBatcher; // injected
    private readonly PageViewPresenter _pagePresenter; // injected
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
            RightSlots,
            _httpFactory!.Client);
    }
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging; // may still be used indirectly for image handling
using Enfolderer.App.Imaging;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Importing; // import service usage
using Enfolderer.App.Collection;
using Enfolderer.App.Quantity;
using Enfolderer.App.Layout;
using Enfolderer.App.Binder;
using Enfolderer.App.Metadata; // orchestrator & provider types
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Utilities;

namespace Enfolderer.App;

public partial class MainWindow : Window
{
    private readonly BinderViewModel _vm;
    private void SearchFocus_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            var tb = this.FindName("SearchNameBox") as System.Windows.Controls.TextBox;
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }
        catch { }
    }

    private async void UpdateMainDbFromCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select CSV File to Update mainDb.db",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            // If Ctrl is held, run legacy updater; otherwise, run MTGS mapping flow (dry-run first)
            bool useLegacy = (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl));
            if (useLegacy)
            {
                _vm.StartImportProgress("Legacy CSV update");
                var result = await Task.Run(() => CsvMainDbUpdater.Process(dlg.FileName, progress: (done, total) => _vm.ReportImportProgress(done, total)));
                _vm.FinishImportProgress();
                MessageBox.Show(this, $"Legacy update complete:\nUpdated: {result.Updated}\nInserted: {result.Inserted}\nErrors: {result.Errors}", "CSV Utility", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // MTGS mapping dry-run: set MtgsId for matching rows; collect unmatched/conflicts
            _vm.StartImportProgress("MTGS dry-run");
            var dry = await Task.Run(() => CsvMainDbUpdater.ProcessMtgsMapping(dlg.FileName, dryRun: true, insertMissing: false, progress: (done, total) => _vm.ReportImportProgress(done, total)));
            _vm.FinishImportProgress();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("MTGS mapping dry-run:");
            sb.AppendLine($"Will update MtgsId on: {dry.UpdatedMtgsIds}");
            sb.AppendLine($"Already mapped (skipped): {dry.SkippedExisting}");
            sb.AppendLine($"Conflicts: {dry.Conflicts}");
            sb.AppendLine($"Unmatched: {(dry.UnmatchedLogPath != null ? "see log " + dry.UnmatchedLogPath : "0")} ");
            sb.AppendLine($"Errors: {dry.Errors}");
            sb.AppendLine();
            sb.AppendLine("Proceed to apply updates?\nClick Yes to apply only updates; No to also insert unmatched; Cancel to abort.");
            var choice = MessageBox.Show(this, sb.ToString(), "MTGS Mapping", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            bool insertMissing = (choice == MessageBoxResult.No);
            _vm.StartImportProgress(insertMissing ? "MTGS apply + insert" : "MTGS apply");
            var apply = await Task.Run(() => CsvMainDbUpdater.ProcessMtgsMapping(dlg.FileName, dryRun: false, insertMissing: insertMissing, progress: (done, total) => _vm.ReportImportProgress(done, total)));
            _vm.FinishImportProgress();
            MessageBox.Show(this, $"MTGS mapping applied:\nUpdated MtgsId: {apply.UpdatedMtgsIds}\nInserted new: {apply.InsertedNew}\nSkipped existing: {apply.SkippedExisting}\nConflicts: {apply.Conflicts}\nErrors: {apply.Errors}\nUnmatched log: {(apply.UnmatchedLogPath ?? "(none)")}", "CSV Utility", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var summary = await _vm.ImportService.ImportSetAsync(input.Trim(), forceReimport, dbPath, msg => _vm.SetStatus(msg));
            _vm.SetStatus($"Import {summary.SetCode}: inserted {summary.Inserted}, updated {summary.UpdatedExisting}, skipped {summary.Skipped}. Total fetched {summary.TotalFetched}{(summary.DeclaredCount.HasValue?"/"+summary.DeclaredCount.Value:"")}.");
        }
        catch (Exception ex)
        {
            _vm.SetStatus("Import error: " + ex.Message);
        }
    }

    private void ExportPlaysetNeedsGeneric_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string setCode = Microsoft.VisualBasic.Interaction.InputBox("Enter source set code (e.g., DMR, MOM, BRO)", "Playset Needs Export", "DMR");
            if (string.IsNullOrWhiteSpace(setCode)) return;
            string path = PlaysetNeedsExporter.ExportPlaysetNeedsForSet(setCode.Trim());
            _vm.SetStatus($"{setCode.ToUpperInvariant()} playset needs exported: " + System.IO.Path.GetFileName(path));
            MessageBox.Show(this, $"Export complete for {setCode.ToUpperInvariant()}:\n{path}", "Playset Needs Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Tools menu handlers (delegate to view model)
    private void Layout4x3_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "4x3"; }
    private void Layout3x3_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "3x3"; }
    private void Layout2x2_Click(object sender, RoutedEventArgs e) { if (_vm!=null) _vm.LayoutMode = "2x2"; }

    private void SetPagesPerBinder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("Pages per binder?", "Set Pages/Binder", _vm.PagesPerBinder.ToString());
            if (string.IsNullOrWhiteSpace(input)) return;
            if (int.TryParse(input.Trim(), out int pages) && pages > 0 && pages <= 1000)
            {
                _vm.PagesPerBinder = pages;
                _vm.SetStatus($"Pages/Binder set to {pages}");
            }
            else MessageBox.Show(this, "Invalid number.", "Pages/Binder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { _vm.SetStatus("Pages/Binder error: " + ex.Message); }
    }

    private void JumpDialog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string binder = Microsoft.VisualBasic.Interaction.InputBox("Binder number? (blank to keep current)", "Jump", "");
            string page = Microsoft.VisualBasic.Interaction.InputBox("Page number? (blank to keep current)", "Jump", "");
            bool binderChanged = false; bool pageChanged = false;
            if (!string.IsNullOrWhiteSpace(binder) && int.TryParse(binder.Trim(), out int b) && b > 0)
            {
                _vm.JumpBinderInput = b.ToString(); binderChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(page) && int.TryParse(page.Trim(), out int p) && p > 0)
            {
                _vm.JumpPageInput = p.ToString(); pageChanged = true;
            }
            if (binderChanged || pageChanged) _vm.JumpToPageCommand.Execute(null);
        }
        catch (Exception ex) { _vm.SetStatus("Jump error: " + ex.Message); }
    }

    // Auto-import all binder set codes not present in mainDb
    private async void AutoImportMissingSets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || string.IsNullOrEmpty(_vm.CurrentCollectionDir)) { _vm?.SetStatus("Open a collection first."); return; }
            string dbPath = System.IO.Path.Combine(_vm.CurrentCollectionDir!, "mainDb.db");
            HashSet<string> binderSets = _vm.GetCurrentSetCodes();
            bool confirm = true;
            bool ConfirmPrompt(string list) => MessageBox.Show(this, $"Import missing sets into mainDb?\n\n{list}", "Auto Import", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
            await _vm.ImportService.AutoImportMissingAsync(binderSets, dbPath, confirm, list => ConfirmPrompt(list), _vm);
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
#if SELF_TESTS
        try
        {
            AppRuntimeFlags.DisableImageFetching = true; // suppress external HTTP during self tests
            int fail = Enfolderer.App.Tests.SelfTests.RunAll();
            if (fail==0) Debug.WriteLine("[SELF-TEST] All self tests passed.");
            else Debug.WriteLine($"[SELF-TEST] FAILURES: {fail}");
        }
        catch (Exception stex)
        {
            Debug.WriteLine($"[SELF-TEST] Exception: {stex.Message}");
        }
        finally
        {
            AppRuntimeFlags.DisableImageFetching = false; // re-enable for normal runtime use
        }
#endif
    }

    private async void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Binder Text File (ie binder_alt_arts.txt)",
            Filter = "TXT Files (*.txt)|*.txt"
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

    private void BackfillZeroQty_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            int inserted = _vm.RunBackfillZeroQty(threshold: 150000);
            _vm.SetStatus($"Backfill inserted {inserted} rows (id < 150000).");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Backfill failed: " + ex.Message);
        }
    }

    private void SetQtyEqualsCardId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var confirm = MessageBox.Show(this, "This will set Qty = CardId for ALL rows in mtgstudio.collection beside the EXE. Continue?", "Confirm Update", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
            int updated = _vm.RunSetQtyEqualsCardId();
            _vm.SetStatus($"Set Qty=CardId updated {updated} rows.");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Set Qty=CardId failed: " + ex.Message);
        }
    }

    private void RestoreCollectionBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var confirm = MessageBox.Show(this, "Restore mtgstudio.collection from .bak beside the EXE?", "Confirm Restore", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;
            bool ok = _vm.RunRestoreCollectionBackup();
            _vm.SetStatus(ok ? "Restore completed from .bak." : "Restore failed or backup not found.");
        }
        catch (Exception ex)
        {
            _vm?.SetStatus("Restore failed: " + ex.Message);
        }
    }

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

    private void SearchNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.PerformSearchNext();
            e.Handled = true;
        }
    }

    private void SearchButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _vm?.PerformSearchNextSet();
        e.Handled = true;
    }
}


public class BinderViewModel : INotifyPropertyChanged, IStatusSink
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
    private List<CardEntry> _orderedFaces => _session.OrderedFaces; // reordered faces honoring placement constraints
    private List<CardSpec> _specs => _session.Specs; // raw specs in file order
    private System.Collections.Concurrent.ConcurrentDictionary<int, CardEntry> _mfcBacks => _session.MfcBacks; // synthetic back faces keyed by spec index
    // Explicitly reference layout navigation service to avoid ambiguity with System.Windows.Navigation.NavigationService
    private readonly Enfolderer.App.Layout.NavigationService _nav = new(); // centralized navigation
    private Enfolderer.App.Binder.NavigationViewBuilder? _navBuilder; // deferred until ctor end
    private IReadOnlyList<Enfolderer.App.Layout.NavigationService.PageView> _views => _nav.Views; // proxy for legacy references
    private readonly Enfolderer.App.Collection.CardCollectionData _collection = new();
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

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand NextBinderCommand { get; }
    public ICommand PrevBinderCommand { get; }
    public ICommand JumpToPageCommand { get; }
    public ICommand NextSetCommand { get; }
    public ICommand PrevSetCommand { get; }
    public ICommand SearchNextCommand { get; }

    private string _jumpBinderInput = "1";
    public string JumpBinderInput { get => _jumpBinderInput; set { _jumpBinderInput = value; OnPropertyChanged(); } }
    private string _jumpPageInput = "1";
    public string JumpPageInput { get => _jumpPageInput; set { _jumpPageInput = value; OnPropertyChanged(); } }

    // Search state
    private string _searchName = string.Empty;
    public string SearchName { get => _searchName; set { if (_searchName != value) { _searchName = value; OnPropertyChanged(); _lastSearchIndex = -1; } } }
    private int _lastSearchIndex = -1; // face index of last match to continue from
    // Search highlight state
    private int _highlightedIndex = -1; // currently highlighted global face index
    private int _pendingHighlightIndex = -1; // requested highlight to apply after view/page rebuild

    private void ClearExistingHighlight()
    {
        if (_highlightedIndex < 0) return;
        // Determine if current highlighted slot is visible; if so clear flag.
        foreach (var s in LeftSlots)
        {
            if (s.GlobalIndex == _highlightedIndex) { s.IsSearchHighlight = false; break; }
        }
        foreach (var s in RightSlots)
        {
            if (s.GlobalIndex == _highlightedIndex) { s.IsSearchHighlight = false; break; }
        }
        _highlightedIndex = -1;
    }

    private void RequestHighlight(int globalIndex)
    {
        _pendingHighlightIndex = globalIndex;
        ApplyHighlightIfVisible();
    }

    private void ApplyHighlightIfVisible()
    {
        if (_pendingHighlightIndex < 0) return;
        int gi = _pendingHighlightIndex;
        bool applied = false;
        foreach (var s in LeftSlots)
        {
            if (s.GlobalIndex == gi)
            {
                ClearExistingHighlight();
                s.IsSearchHighlight = true; _highlightedIndex = gi; applied = true; break;
            }
        }
        if (!applied)
        {
            foreach (var s in RightSlots)
            {
                if (s.GlobalIndex == gi)
                {
                    ClearExistingHighlight();
                    s.IsSearchHighlight = true; _highlightedIndex = gi; applied = true; break;
                }
            }
        }
        if (applied) _pendingHighlightIndex = -1; // done
    }

    private bool TryFindNextByName(string term, out int faceIndex)
    {
        faceIndex = -1;
        if (string.IsNullOrWhiteSpace(term) || _orderedFaces.Count == 0) return false;
        // Case-insensitive contains match against display Name (CardEntry.Name)
        var comp = StringComparison.OrdinalIgnoreCase;
        int start = _lastSearchIndex;
        int count = _orderedFaces.Count;
        // Begin after last index
        int i = (start + 1) % Math.Max(count,1);
        while (true)
        {
            if (_orderedFaces[i].Name.IndexOf(term, comp) >= 0)
            {
                faceIndex = i; return true;
            }
            i = (i + 1) % count;
            if (i == (start + 1) % count) break; // wrapped fully
        }
        return false;
    }

    // Exact set code jump (case-insensitive). Returns true if navigation occurred.
    private bool TryJumpToSetExact(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || _orderedFaces.Count == 0) return false;
        var comp = StringComparison.OrdinalIgnoreCase;
        // Prefer exact code equality; locate first face with matching Set.
        for (int i = 0; i < _orderedFaces.Count; i++)
        {
            var ce = _orderedFaces[i];
            if (!string.IsNullOrWhiteSpace(ce.Set) && string.Equals(ce.Set.Trim(), code.Trim(), comp))
            {
                _lastSearchIndex = i;
                RequestHighlight(i);
                int slotsPerPage = SlotsPerPage;
                int targetPage = (i / slotsPerPage) + 1;
                int binderIndex = (targetPage - 1) / PagesPerBinder;
                int binderOneBased = binderIndex + 1;
                int pageWithinBinder = ((targetPage - 1) % PagesPerBinder) + 1;
                if (_nav.CanJumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder))
                    _nav.JumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder);
                SetStatus($"Jumped to set '{code.ToUpperInvariant()}'");
                return true;
            }
        }
        return false;
    }

    public void PerformSearchNext()
    {
        try
        {
            // Heuristic: if search term length <= 3 attempt exact set jump first.
            string term = SearchName?.Trim() ?? string.Empty;
            if (term.Length > 0 && term.Length <= 3)
            {
                if (TryJumpToSetExact(term)) return; // done if set found
            }

            if (TryFindNextByName(term, out int idx))
            {
                _lastSearchIndex = idx;
                RequestHighlight(idx);
                // Navigate to page containing this face
                int slotsPerPage = SlotsPerPage;
                int targetPage = (idx / slotsPerPage) + 1; // global page (1-based)
                // Determine binder & local page using existing navigation service mapping: find view containing targetPage
                bool viewContains = false;
                foreach (var v in _nav.Views)
                {
                    if (v.LeftPage == targetPage || v.RightPage == targetPage) { viewContains = true; break; }
                }
                if (viewContains)
                {
                    int binderIndex = (targetPage - 1) / PagesPerBinder; // zero-based binder index
                    int binderOneBased = binderIndex + 1;
                    int pageWithinBinder = ((targetPage - 1) % PagesPerBinder) + 1;
                    if (_nav.CanJumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder))
                        _nav.JumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder);
                }
                SetStatus($"Found '{term}' at face {idx + 1}.");
            }
            else
            {
                if (term.Length <= 3)
                    SetStatus($"No set or name match for '{term}'.");
                else
                    SetStatus(string.IsNullOrWhiteSpace(term) ? "Enter a name to search." : $"No match for '{term}'.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Search failed: " + ex.Message);
        }
    }

    public void PerformSearchNextSet()
    {
        try
        {
            string term = SearchName;
            if (string.IsNullOrWhiteSpace(term) || _orderedFaces.Count == 0) { SetStatus("Enter a set code fragment."); return; }
            var comp = StringComparison.OrdinalIgnoreCase;
            int start = _lastSearchIndex;
            int count = _orderedFaces.Count;
            int i = (start + 1) % Math.Max(count,1);
            while (true)
            {
                var ce = _orderedFaces[i];
                if (!string.IsNullOrWhiteSpace(ce.Set) && ce.Set.IndexOf(term, comp) >= 0)
                {
                    _lastSearchIndex = i;
                    RequestHighlight(i);
                    int slotsPerPage = SlotsPerPage;
                    int targetPage = (i / slotsPerPage) + 1;
                    int binderIndex = (targetPage - 1) / PagesPerBinder;
                    int binderOneBased = binderIndex + 1;
                    int pageWithinBinder = ((targetPage - 1) % PagesPerBinder) + 1;
                    if (_nav.CanJumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder))
                        _nav.JumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder);
                    SetStatus($"Found set match '{term}' at face {i + 1} ({ce.Set}).");
                    return;
                }
                i = (i + 1) % count;
                if (i == (start + 1) % count) break;
            }
            SetStatus($"No set match for '{term}'.");
        }
        catch (Exception ex)
        {
            SetStatus("Set search failed: " + ex.Message);
        }
    }

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

    // On metadata cache hits we only have raw Numbers. Re-parse binder file just enough to recover DisplayNumber overrides (e.g. parallel ranges A-B&&C-D => A(C)).
    private static void ReapplyDisplayOverridesFromFile(string path, List<CardEntry> cards)
    {
        if (!File.Exists(path) || cards.Count == 0) return;
        var lines = File.ReadAllLines(path);
        string? currentSet = null;
        var overrides = new List<(string set,string primary,string display)>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith("**")) continue;
            if (line.StartsWith('=')) { currentSet = line.Substring(1).Trim(); continue; }
            if (currentSet == null) continue;
            if (!line.Contains("&&")) continue;
            var seg = line.Split(';')[0].Trim(); // ignore optional name override part
            if (!seg.Contains("&&")) continue;
            var pairSegs = seg.Split("&&", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pairSegs.Length != 2) continue;
            static List<string> Expand(string text)
            {
                var list = new List<string>();
                if (string.IsNullOrWhiteSpace(text)) return list;
                if (text.Contains('-'))
                {
                    var parts = text.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e) && s <= e)
                    { for (int n=s;n<=e;n++) list.Add(n.ToString()); return list; }
                }
                if (int.TryParse(text, out int single)) list.Add(single.ToString());
                return list;
            }
            var primList = Expand(pairSegs[0]);
            var secList = Expand(pairSegs[1]);
            if (primList.Count == 0 || primList.Count != secList.Count) continue;
            for (int i=0;i<primList.Count;i++)
            {
                var disp = primList[i] + "(" + secList[i] + ")";
                overrides.Add((currentSet, primList[i], disp));
            }
        }
        if (overrides.Count == 0) return;
        // Apply: find matching card entries by Set & Number (primary) and assign DisplayNumber if not already present
        foreach (var ov in overrides)
        {
            foreach (var idx in Enumerable.Range(0, cards.Count))
            {
                var c = cards[idx];
                if (c.Set != null && c.DisplayNumber == null && string.Equals(c.Set, ov.set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number, ov.primary, StringComparison.OrdinalIgnoreCase))
                {
                    cards[idx] = c with { DisplayNumber = ov.display };
                }
            }
        }
    }

    // Load binder file (async). Supported lines: comments (#), set headers (=SET), single numbers, ranges start-end, optional ;name overrides.
    public async Task LoadFromFileAsync(string path)
    {
        var load = await _binderLoadService.LoadAsync(path, SlotsPerPage);
    Debug.WriteLine($"[Binder] LoadFromFileAsync start path={path}");
    // Auto-load collection databases early (before initial spec build) so enrichment has quantity keys ready
    try
    {
            // Always load from fixed exe directory now.
            _collection.Load(_currentCollectionDir);
            if (Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug)
                Debug.WriteLine($"[Binder] Collection auto-load (exe dir) invoked: loaded={_collection.IsLoaded} qtyKeys={_collection.Quantities.Count} dir={_currentCollectionDir}");
    }
    catch (Exception ex) { Debug.WriteLine($"[Binder] Collection auto-load failed: {ex.Message}"); }
    _session.CurrentFileHash = load.FileHash;
        // Ignore binder-derived directory (we always use exe directory now).
        if (load.PagesPerBinderOverride.HasValue) PagesPerBinder = load.PagesPerBinderOverride.Value;
        if (!string.IsNullOrEmpty(load.LayoutModeOverride)) LayoutMode = load.LayoutModeOverride;
    // load.HttpDebugEnabled no-op (deprecated debug flag removed).
    _session.LocalBackImagePath = load.LocalBackImagePath;
        _cards.Clear(); _specs.Clear(); _mfcBacks.Clear(); _orderedFaces.Clear(); _pendingExplicitVariantPairs.Clear();
        if (load.CacheHit)
        {
            _cards.AddRange(load.CachedCards);
            // Reconstruct DisplayNumber overrides (e.g., parallel ranges 296(361)) lost in cached metadata (cache only stores raw Number)
            try { ReapplyDisplayOverridesFromFile(path, _cards); } catch (Exception ex) { Debug.WriteLine($"[Binder] ReapplyDisplayOverrides failed: {ex.Message}"); }
            // Perform quantity enrichment on cache hit (mirrors non-cache initial path)
            try { (_quantityService as Quantity.CardQuantityService)?.ApplyAll(_collection, _cards); } catch (Exception ex) { Debug.WriteLine($"[Binder] CacheHit enrichment failed: {ex.Message}"); }
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
            () => _cachePersistence.Persist(_session.CurrentFileHash!, _cards),
            () => _cachePersistence.MarkComplete(_session.CurrentFileHash!)
        );
    Debug.WriteLine("[Binder] LoadFromFileAsync complete");
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
    var arranger = _arrangementService ?? new Enfolderer.App.Core.Arrangement.CardArrangementService(_variantPairing);
	var (cards, pairMap) = arranger.Build(_specs, _mfcBacks, _pendingExplicitVariantPairs);
        _cards.Clear(); _cards.AddRange(cards);
    _explicitVariantPairKeys.Clear(); foreach (var kv in pairMap) _explicitVariantPairKeys[kv.Key] = kv.Value;
        // Always enrich right after rebuild if collection loaded so UI reflects quantities immediately.
    _quantityCoordinator.EnrichAfterRebuildIfLoaded(_collection, _quantityService, _cards, BuildOrderedFaces, Refresh, Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug);
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
    // Coordinator handles fallback enrichment if any -1 quantities remain.
    _quantityCoordinator.EnrichFallbackIfNeeded(_collection, _quantityService, _cards, BuildOrderedFaces, Enfolderer.App.Core.RuntimeFlags.Default.QtyDebug);
    var result = _pagePresenter.Present(_nav, LeftSlots, RightSlots, _orderedFaces, SlotsPerPage, PagesPerBinder, _httpFactory!.Client, _binderTheme);
        PageDisplay = result.PageDisplay; OnPropertyChanged(nameof(PageDisplay));
        BinderBackground = result.BinderBackground; OnPropertyChanged(nameof(BinderBackground));
        if (_nav.Views.Count>0)
        {
            var v = _nav.Views[_nav.CurrentIndex];
            TriggerPageResolution(v.LeftPage ?? 0, v.RightPage ?? 0);
        }
        CommandManager.InvalidateRequerySuggested();
        // After refresh may have rebuilt slots; try to apply highlight
        ApplyHighlightIfVisible();
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


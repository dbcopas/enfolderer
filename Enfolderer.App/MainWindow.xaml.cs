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
    private static int _httpInFlight = 0; private static int _http404 = 0; private static int _http500 = 0;
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
    private readonly CardMetadataResolver _metadataResolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, PhysicallyTwoSidedLayouts, CacheSchemaVersion);
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
            EnrichQuantitiesFromCollection();
            AdjustMfcQuantities();
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
        EnsureCollectionLoaded();
        if (!_collection.IsLoaded) { SetStatus("Collection not loaded"); return; }

        // Derive base number key (strip variant portion inside parentheses and any trailing non-digits progressively)
        string numToken = slot.Number.Split('/')[0];
        int parenIdx = numToken.IndexOf('(');
        if (parenIdx > 0) numToken = numToken.Substring(0, parenIdx);
        string baseNum = numToken;
        string trimmed = baseNum.TrimStart('0'); if (trimmed.Length == 0) trimmed = "0";
        string setLower = slot.Set.ToLowerInvariant();
        (int cardId, int? gatherer) foundEntry = default;
        bool indexFound = false;
        // Handle special variant symbols (e.g. Japanese alt-art WAR planeswalkers using a star character)
        if (baseNum.IndexOf('★') >= 0)
        {
            var starStripped = baseNum.Replace("★", string.Empty);
            if (starStripped.Length == 0) starStripped = "0"; // safety
            // Try direct lookups with star stripped before falling back to progressive stripping logic below
            if (!indexFound && _collection.MainIndex.TryGetValue((setLower, starStripped), out foundEntry)) indexFound = true;
            var starTrimmed = starStripped.TrimStart('0'); if (starTrimmed.Length == 0) starTrimmed = "0";
            if (!indexFound && !string.Equals(starTrimmed, starStripped, StringComparison.Ordinal) && _collection.MainIndex.TryGetValue((setLower, starTrimmed), out foundEntry)) indexFound = true;
            // If we found a match using stripped form, treat that as the canonical baseNum for downstream matching
            if (indexFound)
            {
                baseNum = starStripped;
                trimmed = starTrimmed;
            }
        }
        if (_collection.MainIndex.TryGetValue((setLower, baseNum), out foundEntry)) indexFound = true;
        else if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal) && _collection.MainIndex.TryGetValue((setLower, trimmed), out foundEntry)) indexFound = true;
        else
        {
            string candidate = baseNum;
            while (candidate.Length > 0 && !indexFound && !char.IsDigit(candidate[^1]))
            {
                candidate = candidate.Substring(0, candidate.Length - 1);
                if (_collection.MainIndex.TryGetValue((setLower, candidate), out foundEntry)) { indexFound = true; break; }
            }
        }
        if (!indexFound)
        {
            // Special handling: WAR Japanese alt art star variants (e.g., 1★) should map to base number + Art JP modifier
            if (slot.Set.Equals("WAR", StringComparison.OrdinalIgnoreCase) && slot.Number.IndexOf('★') >= 0)
            {
                var baseStar = slot.Number.Replace("★", string.Empty);
                var baseStarTrim = baseStar.TrimStart('0'); if (baseStarTrim.Length == 0) baseStarTrim = "0";
                int variantId;
                if (_collection.TryGetVariantCardIdFlexible(slot.Set, baseStar, "Art JP", out variantId) ||
                    _collection.TryGetVariantCardIdFlexible(slot.Set, baseStarTrim, "Art JP", out variantId) ||
                    _collection.TryGetVariantCardIdFlexible(slot.Set, baseStar, "JP", out variantId) ||
                    _collection.TryGetVariantCardIdFlexible(slot.Set, baseStarTrim, "JP", out variantId))
                {
                    foundEntry = (variantId, null);
                    indexFound = true;
                }
            }
            if (!indexFound)
            {
                int? directId = ResolveCardIdFromDb(slot.Set, baseNum, trimmed);
                if (directId == null) { SetStatus("Card not found"); return; }
                foundEntry = (directId.Value, null);
            }
        }
        int cardId = foundEntry.cardId;

        int logicalQty = slot.Quantity;
        bool isMfcFront = false;
        var entry = _cards.FirstOrDefault(c => c.Set != null && string.Equals(c.Set, slot.Set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number.Split('/')[0], baseNum.Split('/')[0], StringComparison.OrdinalIgnoreCase) && c.Name == slot.Name);
        if (entry != null && entry.IsModalDoubleFaced && !entry.IsBackFace)
        {
            isMfcFront = true;
            logicalQty = slot.Quantity; // display already mapped
        }
        int newLogicalQty = !isMfcFront ? (logicalQty == 0 ? 1 : 0) : (logicalQty == 0 ? 1 : (logicalQty == 1 ? 2 : 0));

        bool isCustom = _collection.CustomCards.Contains(cardId);
        if (isCustom)
        {
            string mainDbPath = System.IO.Path.Combine(_currentCollectionDir, "mainDb.db");
            if (!File.Exists(mainDbPath)) { SetStatus("mainDb missing"); return; }
            try
            {
                using var conMain = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={mainDbPath}");
                conMain.Open();
                using var cmd = conMain.CreateCommand();
                cmd.CommandText = "UPDATE Cards SET Qty=@q WHERE id=@id";
                cmd.Parameters.AddWithValue("@q", newLogicalQty);
                cmd.Parameters.AddWithValue("@id", cardId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CustomQty] mainDb write failed: {ex.Message}");
                SetStatus("Write failed"); return;
            }
        }
        else
        {
            string collectionPath = System.IO.Path.Combine(_currentCollectionDir, "mtgstudio.collection");
            if (!File.Exists(collectionPath)) { SetStatus("Collection file missing"); return; }
            try
            {
                using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={collectionPath}");
                con.Open();
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                    cmd.Parameters.AddWithValue("@q", newLogicalQty);
                    cmd.Parameters.AddWithValue("@id", cardId);
                    int rows = cmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        using var ins = con.CreateCommand();
                        ins.CommandText = "INSERT INTO CollectionCards (CardId, Qty) VALUES (@id, @q)";
                        ins.Parameters.AddWithValue("@id", cardId);
                        ins.Parameters.AddWithValue("@q", newLogicalQty);
                        try { ins.ExecuteNonQuery(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Collection] Toggle write failed: {ex.Message}");
                SetStatus("Write failed"); return;
            }
        }

        if (newLogicalQty > 0) _collection.Quantities[(setLower, baseNum)] = newLogicalQty; else _collection.Quantities.Remove((setLower, baseNum));
        if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal))
        {
            if (newLogicalQty > 0) _collection.Quantities[(setLower, trimmed)] = newLogicalQty; else _collection.Quantities.Remove((setLower, trimmed));
        }
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            if (c.Set != null && string.Equals(c.Set, slot.Set, StringComparison.OrdinalIgnoreCase))
            {
                string cBase = c.Number.Split('/')[0];
                if (string.Equals(cBase, baseNum, StringComparison.OrdinalIgnoreCase) || string.Equals(cBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase))
                    _cards[i] = c with { Quantity = newLogicalQty };
            }
        }
        AdjustMfcQuantities();
        for (int i = 0; i < _orderedFaces.Count; i++)
        {
            var o = _orderedFaces[i];
            if (o.Set != null && string.Equals(o.Set, slot.Set, StringComparison.OrdinalIgnoreCase))
            {
                string oBase = o.Number.Split('/')[0];
                if (string.Equals(oBase, baseNum, StringComparison.OrdinalIgnoreCase) || string.Equals(oBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = _cards.FirstOrDefault(c => c.Set != null && c.Set.Equals(o.Set, StringComparison.OrdinalIgnoreCase) && c.Number == o.Number && c.IsBackFace == o.IsBackFace);
                    if (updated != null) _orderedFaces[i] = updated;
                }
            }
        }
        Refresh();
        SetStatus($"Set {slot.Set} #{slot.Number} => {newLogicalQty}");
    }

    private void EnsureCollectionLoaded()
    {
        try
        {
            if (!_collection.IsLoaded && !string.IsNullOrEmpty(_currentCollectionDir))
            {
                _collection.Load(_currentCollectionDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Collection] Ensure load failed: {ex.Message}");
        }
    }

    private int? ResolveCardIdFromDb(string setOriginal, string baseNum, string trimmed)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentCollectionDir)) return null;
            string mainDb = System.IO.Path.Combine(_currentCollectionDir, "mainDb.db");
            if (!File.Exists(mainDb)) return null;
            using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={mainDb};Mode=ReadOnly");
            con.Open();
            // Discover columns
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = con.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Cards)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) { try { cols.Add(r.GetString(1)); } catch { } }
            }
            string? idCol = cols.Contains("id") ? "id" : (cols.Contains("cardId") ? "cardId" : null);
            string? editionCol = cols.Contains("edition") ? "edition" : (cols.Contains("set") ? "set" : null);
            string? numberValueCol = cols.Contains("collectorNumberValue") ? "collectorNumberValue" : (cols.Contains("numberValue") ? "numberValue" : null);
            if (idCol == null || editionCol == null || numberValueCol == null) return null;
            // Normalize composite numbers like n(m) to use only leading n for DB lookups
            int parenIndex = baseNum.IndexOf('(');
            if (parenIndex > 0) baseNum = baseNum.Substring(0, parenIndex);

            // Candidate numbers list (original, trimmed, progressive stripping)
            var candidates = new List<string>();
            void AddCand(string c)
            {
                if (string.IsNullOrWhiteSpace(c)) return;
                if (!candidates.Contains(c, StringComparer.OrdinalIgnoreCase)) candidates.Add(c);
            }
            AddCand(baseNum);
            if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal)) AddCand(trimmed);
            // Star / special symbol normalization (e.g., WAR Japanese alt art: 1★)
            if (baseNum.IndexOf('★') >= 0)
            {
                var stripped = baseNum.Replace("★", string.Empty);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    AddCand(stripped);
                    var strippedTrim = stripped.TrimStart('0'); if (strippedTrim.Length == 0) strippedTrim = "0";
                    if (!string.Equals(strippedTrim, stripped, StringComparison.Ordinal)) AddCand(strippedTrim);
                }
            }
            // Progressive strip trailing non-digits
            string prog = baseNum;
            while (prog.Length > 0 && !char.IsDigit(prog[^1]))
            {
                prog = prog[..^1];
                if (prog.Length == 0) break;
                AddCand(prog);
            }

            // Padding variants: 0n, 00n etc (up to two leading zeros) for both baseNum and trimmed forms
            List<string> baseForPad = new();
            if (int.TryParse(baseNum, out _)) baseForPad.Add(baseNum);
            if (int.TryParse(trimmed, out _)) baseForPad.Add(trimmed);
            foreach (var b in baseForPad.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (b.Length < 3) // only pad reasonably small numbers to avoid explosion
                {
                    if (b.Length == 1)
                    {
                        AddCand("0" + b);
                        AddCand("00" + b);
                    }
                    else if (b.Length == 2)
                    {
                        AddCand("0" + b);
                    }
                }
            }
            // Edition candidates (original, upper, lower) to handle case mismatches
            var editionCandidates = new List<string>();
            if (!string.IsNullOrEmpty(setOriginal)) editionCandidates.Add(setOriginal);
            var upper = setOriginal?.ToUpperInvariant();
            var lower = setOriginal?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(upper) && !editionCandidates.Contains(upper)) editionCandidates.Add(upper);
            if (!string.IsNullOrEmpty(lower) && !editionCandidates.Contains(lower)) editionCandidates.Add(lower);

            foreach (var editionCandidate in editionCandidates)
            {
                foreach (var cand in candidates)
                {
                    using var cmd = con.CreateCommand();
                    // Use COLLATE NOCASE as an extra safety; still supply candidate edition.
                    cmd.CommandText = $"SELECT {idCol} FROM Cards WHERE {editionCol}=@set COLLATE NOCASE AND {numberValueCol}=@num LIMIT 1";
                    cmd.Parameters.AddWithValue("@set", editionCandidate);
                    cmd.Parameters.AddWithValue("@num", cand);
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                    {
                        if (int.TryParse(val.ToString(), out int idVal)) return idVal;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Collection] Direct cardId resolve failed: {ex.Message}");
        }
        return null;
    }
    // Explicit pair keys (e.g. base number + language variant) -> enforced pair placement regardless of name differences
    private readonly Dictionary<CardEntry,string> _explicitPairKeys = new();
    // Pending variant pairs captured during parse before resolution (set, baseNumber, variantNumber)
    private readonly List<(string set,string baseNum,string variantNum)> _pendingExplicitVariantPairs = new();
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
        var names = new[] { "Magic_card_back.jpg", "magic_card_back.jpg", "card_back.jpg", "back.jpg", "Magic_card_back.jpeg", "Magic_card_back.png" };
        var dirs = new[]
        {
            _currentCollectionDir,
            AppContext.BaseDirectory,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Enfolderer"),
            Directory.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "images")) ? System.IO.Path.Combine(AppContext.BaseDirectory, "images") : null
        };
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                foreach (var name in names)
                {
                    var path = System.IO.Path.Combine(dir!, name);
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }
        if (logIfMissing)
            Debug.WriteLine("[BackImage] No local card back image found.");
        return null;
    }
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
    public ICommand NextSetCommand { get; }
    public ICommand PrevSetCommand { get; }

    private string _jumpBinderInput = "1";
    public string JumpBinderInput { get => _jumpBinderInput; set { _jumpBinderInput = value; OnPropertyChanged(); } }
    private string _jumpPageInput = "1";
    public string JumpPageInput { get => _jumpPageInput; set { _jumpPageInput = value; OnPropertyChanged(); } }

    public BinderViewModel()
    {
        RegisterInstance(this);
        if (Environment.GetEnvironmentVariable("ENFOLDERER_HTTP_DEBUG") == "1") _debugHttpLogging = true;
        _nav.ViewChanged += NavOnViewChanged;
        NextCommand = new RelayCommand(_ => { if (_nav.CanNext) _nav.Next(); }, _ => _nav.CanNext);
        PrevCommand = new RelayCommand(_ => { if (_nav.CanPrev) _nav.Prev(); }, _ => _nav.CanPrev);
        FirstCommand = new RelayCommand(_ => { if (_nav.CanFirst) _nav.First(); }, _ => _nav.CanFirst);
        LastCommand  = new RelayCommand(_ => { if (_nav.CanLast)  _nav.Last();  }, _ => _nav.CanLast);
        NextBinderCommand = new RelayCommand(_ => { if (_nav.CanJumpBinder(1)) _nav.JumpBinder(1); }, _ => _nav.CanJumpBinder(1));
        PrevBinderCommand = new RelayCommand(_ => { if (_nav.CanJumpBinder(-1)) _nav.JumpBinder(-1); }, _ => _nav.CanJumpBinder(-1));
        JumpToPageCommand = new RelayCommand(_ => { if (TryParseJump(out int b, out int p) && _nav.CanJumpToPage(b,p,PagesPerBinder)) _nav.JumpToPage(b,p,PagesPerBinder); }, _ => TryParseJump(out int b, out int p) && _nav.CanJumpToPage(b,p,PagesPerBinder));
        NextSetCommand = new RelayCommand(_ => { if (_nav.CanJumpSet(true, _orderedFaces.Count)) _nav.JumpSet(true, _orderedFaces, SlotsPerPage, f=>f.Set); }, _ => _nav.CanJumpSet(true, _orderedFaces.Count));
        PrevSetCommand = new RelayCommand(_ => { if (_nav.CanJumpSet(false, _orderedFaces.Count)) _nav.JumpSet(false, _orderedFaces, SlotsPerPage, f=>f.Set); }, _ => _nav.CanJumpSet(false, _orderedFaces.Count));
        RebuildViews();
        Refresh();
        UpdatePanel();
    }

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

    // Legacy binder/page jump logic removed; NavigationService handles binder & page navigation
    private bool TryParseJump(out int binder, out int page)
    { binder=page=0; if (!int.TryParse(JumpBinderInput, out binder) || binder<1) return false; if (!int.TryParse(JumpPageInput, out page) || page<1 || page>PagesPerBinder) return false; return true; }

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
        var parser = new BinderFileParser(_binderTheme, _metadataResolver, log => ResolveLocalBackImagePath(log), IsCacheComplete);
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
                try { EnrichQuantitiesFromCollection(); AdjustMfcQuantities(); } catch (Exception ex) { Debug.WriteLine($"[Collection] Enrichment failed: {ex.Message}"); }
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
                if (_collection.IsLoaded) EnrichQuantitiesFromCollection();
                if (_collection.IsLoaded) AdjustMfcQuantities();
                BuildOrderedFaces();
                RebuildViews();
                Refresh();
                Status = $"All metadata loaded ({_cards.Count} faces).";
                _metadataResolver.PersistMetadataCache(_currentFileHash, _cards);
                _metadataResolver.MarkCacheComplete(_currentFileHash);
            });
        });
    }

    private void EnrichQuantitiesFromCollection()
    {
        if (_collection.Quantities.Count == 0) return;
        int updated = 0;
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            if (c.IsBackFace && string.Equals(c.Set, "__BACK__", StringComparison.OrdinalIgnoreCase)) continue; // skip only placeholder backs
            if (string.IsNullOrEmpty(c.Set) || string.IsNullOrEmpty(c.Number)) continue;
            // Authoritative variant path: WAR star-number (Japanese alternate planeswalkers)
            if (string.Equals(c.Set, "WAR", StringComparison.OrdinalIgnoreCase) && c.Number.Contains('★'))
            {
                string starBaseRaw = c.Number.Replace("★", string.Empty);
                string starTrim = starBaseRaw.TrimStart('0'); if (starTrim.Length == 0) starTrim = "0";
                int qtyVariant = 0; // default 0 even if not present
                bool variantFound = false;
                if (int.TryParse(starBaseRaw, out _))
                {
                    // Try both JP and ART JP variant buckets flexibly
                    if (_collection.TryGetVariantQuantityFlexible(c.Set, starBaseRaw, "Art JP", out var artQty) ||
                        _collection.TryGetVariantQuantityFlexible(c.Set, starTrim, "Art JP", out artQty) ||
                        _collection.TryGetVariantQuantityFlexible(c.Set, starBaseRaw, "JP", out artQty) ||
                        _collection.TryGetVariantQuantityFlexible(c.Set, starTrim, "JP", out artQty))
                    {
                        qtyVariant = artQty;
                        variantFound = true;
                    }
                }
                if (Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1")
                {
                    if (variantFound)
                        Debug.WriteLine($"[Collection][VARIANT] WAR star authoritative {c.Number} -> base={starBaseRaw}/{starTrim} JP qty={qtyVariant}");
                    else
                        Debug.WriteLine($"[Collection][VARIANT-MISS] WAR star authoritative {c.Number} attempted base={starBaseRaw}/{starTrim} JP (flex) defaulting 0");
                }
                if (c.Quantity != qtyVariant)
                {
                    _cards[i] = c with { Quantity = qtyVariant };
                    updated++;
                }
                continue; // skip base fallback entirely for star variants
            }
            // For display numbers like n(m) we only lookup n (first segment before '(')
            string numTokenCard = c.Number.Split('/')[0];
            int parenIndex = numTokenCard.IndexOf('(');
            if (parenIndex > 0)
            {
                numTokenCard = numTokenCard.Substring(0, parenIndex);
            }
            string baseNum = numTokenCard;
            string trimmed = baseNum.TrimStart('0');
            if (trimmed.Length == 0) trimmed = "0";
            var setLower = c.Set.ToLowerInvariant();
            int qty;
            bool found = _collection.Quantities.TryGetValue((setLower, baseNum), out qty);
            if (!found && !string.Equals(trimmed, baseNum, StringComparison.Ordinal))
                found = _collection.Quantities.TryGetValue((setLower, trimmed), out qty);
            // If still not found and number contains letters (e.g., 270Borderless), try stripping trailing non-digits progressively
            if (!found)
            {
                string candidate = baseNum;
                while (candidate.Length > 0 && !found && !char.IsDigit(candidate[^1]))
                {
                    candidate = candidate.Substring(0, candidate.Length -1);
                    if (candidate.Length == 0) break;
                    found = _collection.Quantities.TryGetValue((setLower, candidate), out qty);
                }
            }
            if (!found) {
                if (Environment.GetEnvironmentVariable("ENFOLDERER_QTY_DEBUG") == "1")
                {
                    try
                    {
                        var sampleKeys = string.Join(", ", _collection.Quantities.Keys.Where(k => k.Item1 == setLower).Take(25).Select(k => k.Item1+":"+k.Item2));
                        Debug.WriteLine($"[Collection][MISS] {c.Set} {baseNum} (trim {trimmed}) not found. Sample keys for set: {sampleKeys}");
                    }
                    catch { }
                }
                // Not found -> treat as zero (may have decreased since last refresh)
                if (c.Quantity != 0)
                {
                    _cards[i] = c with { Quantity = 0 };
                    updated++;
                }
                continue;
            }
            if (qty >= 0 && c.Quantity != qty)
            {
                _cards[i] = c with { Quantity = qty };
                updated++;
            }
        }
        if (updated > 0)
            Debug.WriteLine($"[Collection] Quantities applied to {updated} faces");
    }

    // Adjust quantities for modal double-faced (MFC) cards so display follows rule:
    // Q=0  => front 0, back 0
    // Q=1  => front 1, back 0
    // Q>=2 => front 2, back 2 (cap at 2 for display purposes)
    private void AdjustMfcQuantities()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var front = _cards[i];
            if (!front.IsModalDoubleFaced || front.IsBackFace) continue; // only process front faces (real MFC logic retained)
            int q = front.Quantity;
            if (q < 0) continue; // not yet populated
            int frontDisplay, backDisplay;
            if (q <= 0) { frontDisplay = 0; backDisplay = 0; }
            else if (q == 1) { frontDisplay = 1; backDisplay = 0; }
            else { frontDisplay = 2; backDisplay = 2; }
            if (front.Quantity != frontDisplay) _cards[i] = front with { Quantity = frontDisplay };
            // locate matching back face (expected immediately next, but search fallback)
            int backIndex = -1;
            if (i + 1 < _cards.Count)
            {
                var candidate = _cards[i + 1];
                if (candidate.IsModalDoubleFaced && candidate.IsBackFace && candidate.Set == front.Set && candidate.Number == front.Number)
                    backIndex = i + 1;
            }
            if (backIndex == -1)
            {
                for (int j = i + 1; j < _cards.Count; j++)
                {
                    var cand = _cards[j];
                    if (cand.IsModalDoubleFaced && cand.IsBackFace && cand.Set == front.Set && cand.Number == front.Number)
                    { backIndex = j; break; }
                }
            }
            if (backIndex >= 0)
            {
                var back = _cards[backIndex];
                if (back.Quantity != backDisplay) _cards[backIndex] = back with { Quantity = backDisplay };
            }
        }
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
    var service = new FaceLayoutService();
    var ordered = service.BuildOrderedFaces(_cards, LayoutMode, SlotsPerPage, ColumnsPerPage, _explicitPairKeys);
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


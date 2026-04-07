using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using Enfolderer.App.Core;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Lands;

/// <summary>
/// A spread view represents what is shown on screen: an optional left page-side and an optional right page-side.
/// The first side of each binder is right-only; the last side is left-only; interior sides are paired.
/// </summary>
internal record SpreadView(int? LeftGlobalSide, int? RightGlobalSide, int BinderIndex);

public class LandsViewModel : INotifyPropertyChanged
{
    private List<LandEntry> _allLands = new();
    private LandsOwnershipStore? _ownership;
    private HttpClient? _client;

    private const int CardsPerSide = 9;          // 3×3 grid
    private const int SidesPerBinder = 40;        // 20 pages × 2 sides
    private const int CardsPerBinder = CardsPerSide * SidesPerBinder; // 360

    // Binder color codes in order: W, W, U, U, B, B, R, R, G, G
    private static readonly string[] BinderColors = { "W", "W", "U", "U", "B", "B", "R", "R", "G", "G" };

    private static readonly Dictionary<string, Color> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = Color.FromRgb(245, 235, 210),  // cream
        ["U"] = Color.FromRgb(20, 30, 55),     // deep blue
        ["B"] = Color.FromRgb(30, 25, 30),     // near-black purple
        ["R"] = Color.FromRgb(55, 20, 15),     // dark red
        ["G"] = Color.FromRgb(20, 45, 25),     // dark green
    };

    private static readonly Color DefaultBg = Color.FromRgb(0, 0, 0);

    // --- Spread navigation state (unfiltered mode) ---
    private List<SpreadView> _spreads = new();
    private int _currentSpreadIdx;

    // --- Flat paging state (filtered mode) ---
    private List<LandEntry> _filteredLands = new();
    private int _currentFlatPage;
    private int FlatTotalPages => Math.Max(1, (int)Math.Ceiling(_filteredLands.Count / (double)CardsPerSide));

    private int TotalViews => IsFiltered ? FlatTotalPages : Math.Max(1, _spreads.Count);

    public ObservableCollection<LandSlot> LeftSlots { get; } = new();
    public ObservableCollection<LandSlot> RightSlots { get; } = new();

    private string _pageDisplay = string.Empty;
    public string PageDisplay { get => _pageDisplay; private set { if (_pageDisplay != value) { _pageDisplay = value; OnPropertyChanged(); } } }

    private string _status = string.Empty;
    public string Status { get => _status; set { if (_status != value) { _status = value; OnPropertyChanged(); } } }

    private Brush _pageBrush = new SolidColorBrush(DefaultBg);
    public Brush PageBrush { get => _pageBrush; private set { if (_pageBrush != value) { _pageBrush = value; OnPropertyChanged(); } } }

    private string _filterSet = string.Empty;
    public string FilterSet
    {
        get => _filterSet;
        set
        {
            if (_filterSet != value)
            {
                _filterSet = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    private string _filterName = string.Empty;
    public string FilterName
    {
        get => _filterName;
        set
        {
            if (_filterName != value)
            {
                _filterName = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    private bool IsFiltered => !string.IsNullOrWhiteSpace(_filterSet) || !string.IsNullOrWhiteSpace(_filterName);

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand NextBinderCommand { get; }
    public ICommand PrevBinderCommand { get; }

    private int CurrentIndex
    {
        get => IsFiltered ? _currentFlatPage : _currentSpreadIdx;
        set { if (IsFiltered) _currentFlatPage = value; else _currentSpreadIdx = value; }
    }

    public LandsViewModel()
    {
        NextCommand = new RelayCommand(_ => { CurrentIndex++; _ = ShowCurrentAsync(); }, _ => CurrentIndex < TotalViews - 1);
        PrevCommand = new RelayCommand(_ => { CurrentIndex--; _ = ShowCurrentAsync(); }, _ => CurrentIndex > 0);
        FirstCommand = new RelayCommand(_ => { CurrentIndex = 0; _ = ShowCurrentAsync(); }, _ => CurrentIndex > 0);
        LastCommand = new RelayCommand(_ => { CurrentIndex = TotalViews - 1; _ = ShowCurrentAsync(); }, _ => CurrentIndex < TotalViews - 1);
        NextBinderCommand = new RelayCommand(_ => JumpBinder(1), _ => !IsFiltered && _spreads.Count > 0);
        PrevBinderCommand = new RelayCommand(_ => JumpBinder(-1), _ => !IsFiltered && _spreads.Count > 0);
    }

    private void JumpBinder(int direction)
    {
        if (_spreads.Count == 0) return;
        int curBinder = _spreads[_currentSpreadIdx].BinderIndex;
        int targetBinder = curBinder + direction;
        if (direction > 0)
        {
            for (int i = _currentSpreadIdx + 1; i < _spreads.Count; i++)
            {
                if (_spreads[i].BinderIndex >= targetBinder) { _currentSpreadIdx = i; _ = ShowCurrentAsync(); return; }
            }
            // Already at last binder — go to last spread
            _currentSpreadIdx = _spreads.Count - 1;
        }
        else
        {
            for (int i = _currentSpreadIdx - 1; i >= 0; i--)
            {
                if (_spreads[i].BinderIndex <= targetBinder) { _currentSpreadIdx = i; _ = ShowCurrentAsync(); return; }
            }
            _currentSpreadIdx = 0;
        }
        _ = ShowCurrentAsync();
    }

    public async Task LoadAsync(string csvPath)
    {
        Status = "Loading lands...";
        _allLands = await Task.Run(() => LandsCsvParser.Parse(csvPath));

        var ownershipPath = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "lands_owned.txt");
        _ownership = new LandsOwnershipStore(ownershipPath);

        _client = BinderViewModelHttpFactory.Create();

        BuildSpreads();
        _filteredLands = new List<LandEntry>(_allLands);
        _currentSpreadIdx = 0;
        _currentFlatPage = 0;
        Status = $"Loaded {_allLands.Count} lands. Owned: {_ownership.OwnedCount}";
        await ShowCurrentAsync();
    }

    /// <summary>
    /// Build the list of spread views from the unfiltered land list.
    /// Each binder of 360 cards has 40 page-sides. First side is right-only,
    /// last side is left-only, interior sides are paired left+right.
    /// </summary>
    private void BuildSpreads()
    {
        _spreads.Clear();
        int totalSides = (int)Math.Ceiling(_allLands.Count / (double)CardsPerSide);
        int totalBinders = (int)Math.Ceiling(totalSides / (double)SidesPerBinder);
        if (totalBinders == 0) totalBinders = 1;

        for (int b = 0; b < totalBinders; b++)
        {
            int firstSide = b * SidesPerBinder;
            int lastSide = Math.Min(firstSide + SidesPerBinder, totalSides) - 1;
            int sidesInBinder = lastSide - firstSide + 1;

            if (sidesInBinder <= 0) continue;

            // First side: right-only
            _spreads.Add(new SpreadView(null, firstSide, b));

            // Interior pairs
            for (int s = firstSide + 1; s <= lastSide; s += 2)
            {
                int left = s;
                int? right = (s + 1 <= lastSide) ? s + 1 : null;
                if (right == null)
                {
                    // Odd last side: left-only (this IS the last page)
                    _spreads.Add(new SpreadView(left, null, b));
                }
                else
                {
                    _spreads.Add(new SpreadView(left, right, b));
                }
            }

            // If even number of sides and > 1, the last was already paired or handled.
            // If we had an even count, the last pair covers the last side.
            // If odd count > 1, we already added the leftover as left-only above.
        }
    }

    private void ApplyFilter()
    {
        _filteredLands = _allLands.Where(l =>
        {
            if (!string.IsNullOrWhiteSpace(_filterSet) &&
                !l.Set.Contains(_filterSet, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(_filterName) &&
                !l.Name.Contains(_filterName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        _currentFlatPage = 0;
        _ = ShowCurrentAsync();
    }

    private List<LandSlot> BuildSlots(IEnumerable<LandEntry> entries)
    {
        var slots = new List<LandSlot>();
        foreach (var land in entries)
        {
            bool owned = _ownership?.IsOwned(land.Set, land.CollectorNumber) ?? false;
            slots.Add(new LandSlot(land, owned));
        }
        return slots;
    }

    private List<LandEntry> GetSideEntries(int globalSideIndex)
    {
        int start = globalSideIndex * CardsPerSide;
        return _allLands.Skip(start).Take(CardsPerSide).ToList();
    }

    private static string ColorName(string code) => code switch
    {
        "W" => "White", "U" => "Blue", "B" => "Black", "R" => "Red", "G" => "Green", _ => ""
    };

    private static (int PhysicalPage, string Side) SideToPageInfo(int sideInBinder)
    {
        int page = (sideInBinder / 2) + 1;          // 1–20
        string side = (sideInBinder % 2 == 0) ? "Front" : "Back";
        return (page, side);
    }

    public async Task ShowCurrentAsync()
    {
        LeftSlots.Clear();
        RightSlots.Clear();

        if (IsFiltered)
        {
            // Flat mode: show 9 cards per page in the right panel, left empty
            var entries = _filteredLands.Skip(_currentFlatPage * CardsPerSide).Take(CardsPerSide).ToList();
            foreach (var s in BuildSlots(entries)) RightSlots.Add(s);
            PageDisplay = $"Page {_currentFlatPage + 1} / {FlatTotalPages}  ({_filteredLands.Count} lands)";
            PageBrush = MakeBrush(DefaultBg);
        }
        else if (_spreads.Count > 0)
        {
            var spread = _spreads[_currentSpreadIdx];
            int binderIdx = spread.BinderIndex;
            string colorCode = binderIdx < BinderColors.Length ? BinderColors[binderIdx] : "";

            if (spread.LeftGlobalSide.HasValue)
                foreach (var s in BuildSlots(GetSideEntries(spread.LeftGlobalSide.Value))) LeftSlots.Add(s);
            if (spread.RightGlobalSide.HasValue)
                foreach (var s in BuildSlots(GetSideEntries(spread.RightGlobalSide.Value))) RightSlots.Add(s);

            // Build page display
            string binderLabel = $"Binder {binderIdx + 1} ({ColorName(colorCode)})";
            string pageLabel;
            if (spread.LeftGlobalSide.HasValue && spread.RightGlobalSide.HasValue)
            {
                int leftInBinder = spread.LeftGlobalSide.Value - binderIdx * SidesPerBinder;
                int rightInBinder = spread.RightGlobalSide.Value - binderIdx * SidesPerBinder;
                var (lp, ls) = SideToPageInfo(leftInBinder);
                var (rp, rs) = SideToPageInfo(rightInBinder);
                pageLabel = $"Page {lp} {ls}  |  Page {rp} {rs}";
            }
            else if (spread.RightGlobalSide.HasValue)
            {
                int sideInBinder = spread.RightGlobalSide.Value - binderIdx * SidesPerBinder;
                var (p, sd) = SideToPageInfo(sideInBinder);
                pageLabel = $"Page {p} {sd} (Front Cover)";
            }
            else
            {
                int sideInBinder = spread.LeftGlobalSide!.Value - binderIdx * SidesPerBinder;
                var (p, sd) = SideToPageInfo(sideInBinder);
                pageLabel = $"Page {p} {sd} (Back Cover)";
            }

            PageDisplay = $"{binderLabel}  —  {pageLabel}   [{_currentSpreadIdx + 1}/{_spreads.Count}]";

            var bgColor = ColorMap.TryGetValue(colorCode, out var c) ? c : DefaultBg;
            PageBrush = MakeBrush(bgColor);
        }

        // Load images
        if (_client != null)
        {
            var allSlots = LeftSlots.Concat(RightSlots).ToArray();
            var tasks = allSlots.Select(s => s.TryLoadImageAsync(_client)).ToArray();
            await Task.WhenAll(tasks);
        }
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    public void ToggleOwnership(LandSlot slot)
    {
        if (_ownership == null) return;
        bool nowOwned = _ownership.Toggle(slot.Set, slot.CollectorNumber);
        slot.IsOwned = nowOwned;
        Status = $"Owned: {_ownership.OwnedCount} / {_allLands.Count}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

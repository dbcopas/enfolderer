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
using Enfolderer.App.Imaging;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Tokens;

internal record TokenSpreadView(int? LeftGlobalSide, int? RightGlobalSide, int BinderIndex);

public class TokensViewModel : INotifyPropertyChanged
{
    private List<TokenEntry> _allTokens = new();
    private TokensOwnershipStore? _ownership;
    private HttpClient? _client;

    private const int CardsPerSide = 9;          // 3×3 grid
    private const int SidesPerBinder = 40;        // 20 pages × 2 sides

    private static readonly Color DefaultBg = Color.FromRgb(30, 30, 40);

    // --- Spread navigation state (unfiltered mode) ---
    private List<TokenSpreadView> _spreads = new();
    private int _currentSpreadIdx;

    // --- Flat paging state (filtered mode) ---
    private List<TokenEntry> _filteredTokens = new();
    private int _currentFlatPage;
    private int FlatTotalPages => Math.Max(1, (int)Math.Ceiling(_filteredTokens.Count / (double)CardsPerSide));

    private int TotalViews => IsFiltered ? FlatTotalPages : Math.Max(1, _spreads.Count);

    public ObservableCollection<TokenSlot> LeftSlots { get; } = new();
    public ObservableCollection<TokenSlot> RightSlots { get; } = new();

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

    public TokensViewModel()
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
        Status = "Loading tokens...";
        _allTokens = await Task.Run(() => TokensCsvParser.Parse(csvPath));

        var ownershipPath = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "tokens_owned.txt");
        _ownership = new TokensOwnershipStore(ownershipPath);

        _client = BinderViewModelHttpFactory.Create();

        BuildSpreads();
        _filteredTokens = new List<TokenEntry>(_allTokens);
        _currentSpreadIdx = 0;
        _currentFlatPage = 0;
        Status = $"Loaded {_allTokens.Count} tokens. Owned: {_ownership.OwnedCount}";
        await ShowCurrentAsync();
    }

    private void BuildSpreads()
    {
        _spreads.Clear();
        int totalSides = (int)Math.Ceiling(_allTokens.Count / (double)CardsPerSide);
        int totalBinders = Math.Max(1, (int)Math.Ceiling(totalSides / (double)SidesPerBinder));

        for (int b = 0; b < totalBinders; b++)
        {
            int firstSide = b * SidesPerBinder;
            int lastSide = Math.Min(firstSide + SidesPerBinder, totalSides) - 1;
            int sidesInBinder = lastSide - firstSide + 1;

            if (sidesInBinder <= 0) continue;

            _spreads.Add(new TokenSpreadView(null, firstSide, b));

            for (int s = firstSide + 1; s <= lastSide; s += 2)
            {
                int left = s;
                int? right = (s + 1 <= lastSide) ? s + 1 : null;
                if (right == null)
                    _spreads.Add(new TokenSpreadView(left, null, b));
                else
                    _spreads.Add(new TokenSpreadView(left, right, b));
            }
        }
    }

    private void ApplyFilter()
    {
        _filteredTokens = _allTokens.Where(t =>
        {
            if (!string.IsNullOrWhiteSpace(_filterSet) &&
                !t.Set.Contains(_filterSet, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(_filterName) &&
                !t.Name.Contains(_filterName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        _currentFlatPage = 0;
        _ = ShowCurrentAsync();
    }

    private List<TokenSlot> BuildSlots(IEnumerable<TokenEntry> entries)
    {
        var slots = new List<TokenSlot>();
        foreach (var token in entries)
        {
            bool owned = _ownership?.IsOwned(token.Set, token.CollectorNumber) ?? false;
            slots.Add(new TokenSlot(token, owned));
        }
        return slots;
    }

    private List<TokenEntry> GetSideEntries(int globalSideIndex)
    {
        int start = globalSideIndex * CardsPerSide;
        return _allTokens.Skip(start).Take(CardsPerSide).ToList();
    }

    private static (int PhysicalPage, string Side) SideToPageInfo(int sideInBinder)
    {
        int page = (sideInBinder / 2) + 1;
        string side = (sideInBinder % 2 == 0) ? "Front" : "Back";
        return (page, side);
    }

    public async Task ShowCurrentAsync()
    {
        LeftSlots.Clear();
        RightSlots.Clear();

        if (IsFiltered)
        {
            var entries = _filteredTokens.Skip(_currentFlatPage * CardsPerSide).Take(CardsPerSide).ToList();
            foreach (var s in BuildSlots(entries)) RightSlots.Add(s);
            PageDisplay = $"Page {_currentFlatPage + 1} / {FlatTotalPages}  ({_filteredTokens.Count} tokens)";
            PageBrush = MakeBrush(DefaultBg);
        }
        else if (_spreads.Count > 0)
        {
            var spread = _spreads[_currentSpreadIdx];
            int binderIdx = spread.BinderIndex;

            if (spread.LeftGlobalSide.HasValue)
                foreach (var s in BuildSlots(GetSideEntries(spread.LeftGlobalSide.Value))) LeftSlots.Add(s);
            if (spread.RightGlobalSide.HasValue)
                foreach (var s in BuildSlots(GetSideEntries(spread.RightGlobalSide.Value))) RightSlots.Add(s);

            string binderLabel = $"Binder {binderIdx + 1}";
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
            PageBrush = MakeBrush(DefaultBg);
        }

        if (_client != null)
        {
            var allSlots = LeftSlots.Concat(RightSlots).ToArray();
            var tasks = allSlots.Select(s => s.TryLoadImageAsync(_client)).ToArray();
            await Task.WhenAll(tasks);
            CardImageUrlStore.SaveToDisk();
        }
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    public void ToggleOwnership(TokenSlot slot)
    {
        if (_ownership == null) return;
        bool nowOwned = _ownership.Toggle(slot.Set, slot.CollectorNumber);
        slot.IsOwned = nowOwned;
        Status = $"Owned: {_ownership.OwnedCount} / {_allTokens.Count}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

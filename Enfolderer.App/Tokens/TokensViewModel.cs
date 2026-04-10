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
    private const int SidesPerBinder = 20;        // 10 pages × 2 sides

    private static readonly Color DefaultBg = Color.FromRgb(30, 30, 40);

    // --- Spread navigation state (unfiltered mode) ---
    private List<TokenSpreadView> _spreads = new();
    private int _currentSpreadIdx;

    // --- Filter-match navigation state ---
    private List<int> _matchSpreadIndices = new();
    private int _currentMatchIdx;

    private int TotalViews => Math.Max(1, _spreads.Count);

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

    private bool IsFiltered => _matchSpreadIndices.Count > 0;

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand NextBinderCommand { get; }
    public ICommand PrevBinderCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PrevMatchCommand { get; }

    public TokensViewModel()
    {
        NextCommand = new RelayCommand(_ => { _currentSpreadIdx++; _ = ShowCurrentAsync(); }, _ => _currentSpreadIdx < TotalViews - 1);
        PrevCommand = new RelayCommand(_ => { _currentSpreadIdx--; _ = ShowCurrentAsync(); }, _ => _currentSpreadIdx > 0);
        FirstCommand = new RelayCommand(_ => { _currentSpreadIdx = 0; _ = ShowCurrentAsync(); }, _ => _currentSpreadIdx > 0);
        LastCommand = new RelayCommand(_ => { _currentSpreadIdx = TotalViews - 1; _ = ShowCurrentAsync(); }, _ => _currentSpreadIdx < TotalViews - 1);
        NextBinderCommand = new RelayCommand(_ => JumpBinder(1), _ => _spreads.Count > 0);
        PrevBinderCommand = new RelayCommand(_ => JumpBinder(-1), _ => _spreads.Count > 0);
        NextMatchCommand = new RelayCommand(_ => JumpMatch(1), _ => _matchSpreadIndices.Count > 0 && _currentMatchIdx < _matchSpreadIndices.Count - 1);
        PrevMatchCommand = new RelayCommand(_ => JumpMatch(-1), _ => _matchSpreadIndices.Count > 0 && _currentMatchIdx > 0);
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
        _matchSpreadIndices = new List<int>();
        _currentSpreadIdx = 0;
        _currentMatchIdx = 0;
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
        _matchSpreadIndices.Clear();
        _currentMatchIdx = 0;

        bool hasSetFilter = !string.IsNullOrWhiteSpace(_filterSet);
        bool hasNameFilter = !string.IsNullOrWhiteSpace(_filterName);

        if (!hasSetFilter && !hasNameFilter)
        {
            _ = ShowCurrentAsync();
            return;
        }

        // Find which global token indices match
        var matchingTokenIndices = new HashSet<int>();
        for (int i = 0; i < _allTokens.Count; i++)
        {
            var t = _allTokens[i];
            if (hasSetFilter && !t.Set.Contains(_filterSet, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hasNameFilter && !t.Name.Contains(_filterName, StringComparison.OrdinalIgnoreCase))
                continue;
            matchingTokenIndices.Add(i);
        }

        if (matchingTokenIndices.Count == 0)
        {
            Status = "No matches found.";
            _ = ShowCurrentAsync();
            return;
        }

        // Map matching token indices to the spread indices that contain them
        for (int si = 0; si < _spreads.Count; si++)
        {
            var spread = _spreads[si];
            if (SpreadContainsAnyMatch(spread.LeftGlobalSide, matchingTokenIndices) ||
                SpreadContainsAnyMatch(spread.RightGlobalSide, matchingTokenIndices))
            {
                _matchSpreadIndices.Add(si);
            }
        }

        if (_matchSpreadIndices.Count > 0)
        {
            _currentMatchIdx = 0;
            _currentSpreadIdx = _matchSpreadIndices[0];
        }

        _ = ShowCurrentAsync();
    }

    private bool SpreadContainsAnyMatch(int? globalSide, HashSet<int> matchingTokenIndices)
    {
        if (!globalSide.HasValue) return false;
        int start = globalSide.Value * CardsPerSide;
        int end = Math.Min(start + CardsPerSide, _allTokens.Count);
        for (int i = start; i < end; i++)
            if (matchingTokenIndices.Contains(i)) return true;
        return false;
    }

    private void JumpMatch(int direction)
    {
        if (_matchSpreadIndices.Count == 0) return;
        _currentMatchIdx = Math.Clamp(_currentMatchIdx + direction, 0, _matchSpreadIndices.Count - 1);
        _currentSpreadIdx = _matchSpreadIndices[_currentMatchIdx];
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

        if (_spreads.Count > 0)
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

            string matchSuffix = IsFiltered
                ? $"  🔍 Match {_currentMatchIdx + 1}/{_matchSpreadIndices.Count}"
                : string.Empty;
            PageDisplay = $"{binderLabel}  —  {pageLabel}   [{_currentSpreadIdx + 1}/{_spreads.Count}]{matchSuffix}";
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

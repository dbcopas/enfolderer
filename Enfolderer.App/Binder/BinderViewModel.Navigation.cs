using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Enfolderer.App.Layout;
using Enfolderer.App.Core;

namespace Enfolderer.App;

/// <summary>
/// BinderViewModel partial: Navigation commands, search logic, and highlight management.
/// Handles user navigation (next/prev page, set jumping, binder navigation) and card search functionality.
/// </summary>
public partial class BinderViewModel
{
    public ICommand NextCommand { get; private set; } = default!;
    public ICommand PrevCommand { get; private set; } = default!;
    public ICommand FirstCommand { get; private set; } = default!;
    public ICommand LastCommand { get; private set; } = default!;
    public ICommand NextBinderCommand { get; private set; } = default!;
    public ICommand PrevBinderCommand { get; private set; } = default!;
    public ICommand JumpToPageCommand { get; private set; } = default!;
    public ICommand NextSetCommand { get; private set; } = default!;
    public ICommand PrevSetCommand { get; private set; } = default!;
    public ICommand SearchNextCommand { get; private set; } = default!;

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
                int slotsPerPage = SlotsPerPage;
                int targetPage = (i / slotsPerPage) + 1;
                int binderIndex = (targetPage - 1) / PagesPerBinder;
                int binderOneBased = binderIndex + 1;
                int pageWithinBinder = ((targetPage - 1) % PagesPerBinder) + 1;
                if (_nav.CanJumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder))
                    _nav.JumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder);
                RequestHighlight(i);
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
                // Navigate to page containing this face first, then highlight
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
                // Apply highlight after navigation so rebuilt slots are in place
                RequestHighlight(idx);
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
                    int slotsPerPage = SlotsPerPage;
                    int targetPage = (i / slotsPerPage) + 1;
                    int binderIndex = (targetPage - 1) / PagesPerBinder;
                    int binderOneBased = binderIndex + 1;
                    int pageWithinBinder = ((targetPage - 1) % PagesPerBinder) + 1;
                    if (_nav.CanJumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder))
                        _nav.JumpToPage(binderOneBased, pageWithinBinder, PagesPerBinder);
                    RequestHighlight(i);
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
}

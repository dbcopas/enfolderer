using System;
using System.Collections.Generic;

namespace Enfolderer.App;

/// <summary>
/// Encapsulates binder navigation (page view construction + current view index operations) independent of UI.
/// </summary>
public class NavigationService
{
    public record PageView(int? LeftPage, int? RightPage, int BinderIndex);

    private readonly List<PageView> _views;
    public IReadOnlyList<PageView> Views => _views;
    public int CurrentIndex { get; private set; } = 0;

    public event Action? ViewChanged;

    public NavigationService(List<PageView>? backingList = null)
    { _views = backingList ?? new List<PageView>(); }

    public void ResetIndex() { CurrentIndex = 0; OnChanged(); }

    public void Rebuild(int totalFaces, int slotsPerPage, int pagesPerBinder)
    {
        _views.Clear();
        if (slotsPerPage <=0) slotsPerPage = 12;
        if (pagesPerBinder <=0) pagesPerBinder = 40;
        int totalPages = (int)Math.Ceiling(totalFaces / (double)slotsPerPage);
        if (totalPages == 0) totalPages = 1;
        int remaining = totalPages;
        int globalPage = 1;
        int binderIndex = 0;
        while (remaining > 0)
        {
            int pagesInBinder = Math.Min(pagesPerBinder, remaining);
            _views.Add(new PageView(null, globalPage, binderIndex));
            int binderStartGlobal = globalPage;
            for (int local = 2; local <= pagesInBinder - 1; local += 2)
            {
                int leftPageNum = binderStartGlobal + (local -1);
                int rightPageNum = leftPageNum + 1;
                if (rightPageNum > binderStartGlobal + pagesInBinder -1) break;
                _views.Add(new PageView(leftPageNum, rightPageNum, binderIndex));
            }
            if (pagesInBinder > 1)
            {
                int lastPageGlobal = binderStartGlobal + pagesInBinder -1;
                _views.Add(new PageView(lastPageGlobal, null, binderIndex));
            }
            globalPage += pagesInBinder; remaining -= pagesInBinder; binderIndex++;
        }
        if (CurrentIndex >= _views.Count) CurrentIndex = _views.Count -1;
        if (CurrentIndex < 0) CurrentIndex = 0;
        OnChanged();
    }

    public bool CanNext => CurrentIndex < _views.Count -1;
    public bool CanPrev => CurrentIndex > 0;
    public bool CanFirst => CurrentIndex != 0;
    public bool CanLast => _views.Count > 0 && CurrentIndex != _views.Count -1;
    public void Next() { if (CanNext) { CurrentIndex++; OnChanged(); } }
    public void Prev() { if (CanPrev) { CurrentIndex--; OnChanged(); } }
    public void First() { if (CanFirst) { CurrentIndex = 0; OnChanged(); } }
    public void Last() { if (CanLast) { CurrentIndex = _views.Count -1; OnChanged(); } }


    public bool CanJumpBinder(int delta)
    { if (_views.Count==0) return false; int targetBinder = _views[CurrentIndex].BinderIndex + delta; if (targetBinder <0) return false; int maxBinder = _views[^1].BinderIndex; return targetBinder <= maxBinder; }
    public void JumpBinder(int delta)
    { if (!CanJumpBinder(delta)) return; int targetBinder = _views[CurrentIndex].BinderIndex + delta; int idx = _views.FindIndex(v => v.BinderIndex==targetBinder); if (idx>=0) { CurrentIndex = idx; OnChanged(); } }

    public bool CanJumpToPage(int binderOneBased, int pageOneBased, int pagesPerBinder)
    { if (binderOneBased <1 || pageOneBased <1 || pageOneBased>pagesPerBinder) return false; int maxBinder = _views.Count==0?0:_views[^1].BinderIndex +1; if (binderOneBased>maxBinder) return false; return true; }
    public void JumpToPage(int binderOneBased, int pageOneBased, int pagesPerBinder)
    { if (!CanJumpToPage(binderOneBased, pageOneBased, pagesPerBinder)) return; int binderIndex = binderOneBased -1; int globalPage = binderIndex * pagesPerBinder + pageOneBased; int idx = _views.FindIndex(v => (v.LeftPage==globalPage) || (v.RightPage==globalPage)); if (idx>=0) { CurrentIndex = idx; OnChanged(); } }

    public bool CanJumpSet(bool forward, Func<int> facesCount, Func<int,int?> pageForFaceIndex)
    { if (_views.Count==0 || facesCount()==0) return false; if (forward) return CurrentIndex < _views.Count -1; else return CurrentIndex > 0; }
    public void JumpSet(bool forward, IReadOnlyList<object> orderedFaces, int slotsPerPage, Func<object,int?> faceIndexLookup, Func<object,string?> setGetter)
    {
        if (_views.Count==0 || orderedFaces.Count==0) return;
        var view = _views[CurrentIndex];
        List<int> faceIndices = new();
        if (view.LeftPage.HasValue)
        {
            int lp = view.LeftPage.Value; int start = (lp -1) * slotsPerPage; for (int i=0;i<slotsPerPage;i++){ int idx = start + i; if (idx < orderedFaces.Count) faceIndices.Add(idx); }
        }
        if (view.RightPage.HasValue)
        {
            int rp = view.RightPage.Value; int start = (rp -1) * slotsPerPage; for (int i=0;i<slotsPerPage;i++){ int idx = start + i; if (idx < orderedFaces.Count) faceIndices.Add(idx); }
        }
        if (faceIndices.Count==0) return;
        if (forward)
        {
            int anchorIdx = faceIndices[^1]; var anchorSet = setGetter(orderedFaces[anchorIdx]); if (anchorSet==null) return; for (int i=anchorIdx+1;i<orderedFaces.Count;i++){ if (!string.Equals(setGetter(orderedFaces[i]), anchorSet, StringComparison.OrdinalIgnoreCase)) { int targetPage = (i / slotsPerPage)+1; int viewIdx = _views.FindIndex(v => v.LeftPage==targetPage || v.RightPage==targetPage); if (viewIdx>=0) { CurrentIndex = viewIdx; OnChanged(); } return; } }
        }
        else
        {
            int anchorIdx = faceIndices[0]; var anchorSet = setGetter(orderedFaces[anchorIdx]); if (anchorSet==null) return; for (int i=anchorIdx-1;i>=0;i--){ if (!string.Equals(setGetter(orderedFaces[i]), anchorSet, StringComparison.OrdinalIgnoreCase)) { int runStart = i; string prevSet = setGetter(orderedFaces[i])??""; while (runStart-1>=0 && string.Equals(setGetter(orderedFaces[runStart-1]), prevSet, StringComparison.OrdinalIgnoreCase)) runStart--; int targetPage = (runStart / slotsPerPage)+1; int viewIdx = _views.FindIndex(v => v.LeftPage==targetPage || v.RightPage==targetPage); if (viewIdx>=0) { CurrentIndex = viewIdx; OnChanged(); } return; } }
        }
    }

    // Convenience overloads for simpler callers
    public bool CanJumpSet(bool forward, int facesCount) => _views.Count!=0 && facesCount>0 && (forward ? CanNext : CanPrev);

    public void JumpSet<T>(bool forward, IReadOnlyList<T> orderedFaces, int slotsPerPage, Func<T,string?> setGetter)
    {
        if (_views.Count==0 || orderedFaces.Count==0) return;
        var view = _views[CurrentIndex];
        List<int> faceIndices = new();
        void Collect(int? page)
        {
            if (!page.HasValue) return; int start = (page.Value -1)*slotsPerPage; for (int i=0;i<slotsPerPage;i++){ int idx = start + i; if (idx < orderedFaces.Count) faceIndices.Add(idx);} }
        Collect(view.LeftPage); Collect(view.RightPage);
        if (faceIndices.Count==0) return;
        if (forward)
        {
            int anchorIdx = faceIndices[^1]; var anchorSet = setGetter(orderedFaces[anchorIdx]); if (anchorSet==null) return; for (int i=anchorIdx+1;i<orderedFaces.Count;i++){ if (!string.Equals(setGetter(orderedFaces[i]), anchorSet, StringComparison.OrdinalIgnoreCase)) { int targetPage = (i / slotsPerPage)+1; int viewIdx = _views.FindIndex(v => v.LeftPage==targetPage || v.RightPage==targetPage); if (viewIdx>=0){ CurrentIndex = viewIdx; OnChanged(); } return; } }
        }
        else
        {
            int anchorIdx = faceIndices[0]; var anchorSet = setGetter(orderedFaces[anchorIdx]); if (anchorSet==null) return; for (int i=anchorIdx-1;i>=0;i--){ if (!string.Equals(setGetter(orderedFaces[i]), anchorSet, StringComparison.OrdinalIgnoreCase)) { int runStart = i; string prevSet = setGetter(orderedFaces[i])??""; while (runStart-1>=0 && string.Equals(setGetter(orderedFaces[runStart-1]), prevSet, StringComparison.OrdinalIgnoreCase)) runStart--; int targetPage = (runStart / slotsPerPage)+1; int viewIdx = _views.FindIndex(v => v.LeftPage==targetPage || v.RightPage==targetPage); if (viewIdx>=0){ CurrentIndex = viewIdx; OnChanged(); } return; } }
        }
    }

    private void OnChanged() => ViewChanged?.Invoke();
}

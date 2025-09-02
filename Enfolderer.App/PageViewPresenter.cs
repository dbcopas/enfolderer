using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;

namespace Enfolderer.App;

/// <summary>
/// Presents the current navigation view by building left/right page slots and computing display + background.
/// Side-effects limited to populating provided slot collections.
/// </summary>
public class PageViewPresenter
{
    private readonly PageSlotBuilder _slotBuilder = new();

    public record Result(string PageDisplay, Brush BinderBackground);

    public Result Present(
        NavigationService nav,
        ObservableCollection<CardSlot> leftSlots,
        ObservableCollection<CardSlot> rightSlots,
        IReadOnlyList<CardEntry> orderedFaces,
        int slotsPerPage,
        int pagesPerBinder,
        HttpClient http,
        BinderThemeService binderTheme)
    {
        leftSlots.Clear(); rightSlots.Clear();
        if (nav.Views.Count == 0)
            return new Result("No pages", binderTheme.CreateBinderBackground(0));
        var view = nav.Views[nav.CurrentIndex];
        if (view.LeftPage.HasValue)
            foreach (var s in _slotBuilder.BuildPageSlots(orderedFaces, view.LeftPage.Value, slotsPerPage, http)) leftSlots.Add(s);
        if (view.RightPage.HasValue)
            foreach (var s in _slotBuilder.BuildPageSlots(orderedFaces, view.RightPage.Value, slotsPerPage, http)) rightSlots.Add(s);
        var display = _slotBuilder.BuildPageDisplay(view, pagesPerBinder);
        var brush = binderTheme.CreateBinderBackground(view.BinderIndex);
        return new Result(display, brush);
    }
}

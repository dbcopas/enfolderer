using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using Enfolderer.App.Infrastructure;
using Enfolderer.App.Core;
using Enfolderer.App.Layout;
using Enfolderer.App.Binder;

namespace Enfolderer.App.Tests;

public static class PageViewPresenterTests
{
    public static int RunAll()
    {
        int failures=0; void Check(bool c){ if(!c) failures++; }
        var nav = new NavigationService();
        var ordered = new List<CardEntry>{ new CardEntry("A","1","S",false), new CardEntry("B","2","S",false)};
        nav.Rebuild(totalFaces: ordered.Count, slotsPerPage: 9, pagesPerBinder: 40);
        var left = new ObservableCollection<CardSlot>();
        var right = new ObservableCollection<CardSlot>();
        var presenter = new PageViewPresenter();
        var http = new HttpClient(new HttpClientHandler());
        var theme = new BinderThemeService();
        var result = presenter.Present(nav,left,right,ordered,9,40,http,theme);
        Check(left.Count>0 || right.Count>0);
        Check(!string.IsNullOrEmpty(result.PageDisplay));
    Check(AppRuntimeFlags.DisableImageFetching == true);
        return failures;
    }
}

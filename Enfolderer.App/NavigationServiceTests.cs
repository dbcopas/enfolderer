using System.Linq;
using Enfolderer.App.Layout;

namespace Enfolderer.App.Tests;

public static class NavigationServiceTests
{
    public static int RunAll()
    {
        int failures = 0; void Check(bool c){ if(!c) failures++; }
        var nav = new NavigationService();
        nav.Rebuild(totalFaces: 50, slotsPerPage: 12, pagesPerBinder: 40);
        Check(nav.Views.Count > 0);
        int initial = nav.CurrentIndex;
        if (nav.CanNext) nav.Next();
        Check(nav.CurrentIndex == initial + (nav.Views.Count>1?1:0));
        nav.First(); Check(nav.CurrentIndex==0);
        nav.Last(); Check(nav.CurrentIndex == nav.Views.Count-1);
        var lastBinder = nav.Views[^1].BinderIndex;
        nav.JumpBinder(-1); Check(nav.Views[nav.CurrentIndex].BinderIndex <= lastBinder);
        // Jump to a page
        nav.First();
        if (nav.CanJumpToPage(1,1,40)) nav.JumpToPage(1,1,40);
        Check(nav.CurrentIndex==0);
        return failures;
    }
}

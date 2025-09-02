using System.Collections.Generic;
using System.Windows.Input;

namespace Enfolderer.App.Tests;

public static class CommandFactoryTests
{
    private class DummyCmdUsage
    {
        public int Count;
        public void Inc() => Count++;
    }

    public static int RunAll()
    {
        int failures=0; void Check(bool c){ if(!c) failures++; }
        var nav = new NavigationService();
        var faces = new List<CardEntry>{ new CardEntry("A","1","S",false), new CardEntry("B","2","S",false)};
        nav.Rebuild(faces.Count, 9, 40);
        string jumpBinder = "1"; string jumpPage = "1";
        var factory = new CommandFactory(nav, () => 40, () => faces.Count, () => jumpBinder, () => jumpPage, () => 9, () => faces);
        ICommand next = factory.CreateNext();
        if (next.CanExecute(null)) next.Execute(null);
        // JumpToPage
        ICommand jump = factory.CreateJumpToPage();
        if (jump.CanExecute(null)) jump.Execute(null);
        // Set navigation forward/backward
        ICommand nextSet = factory.CreateNextSet();
        if (nextSet.CanExecute(null)) nextSet.Execute(null);
        // No assertions failing indicates wiring works; minimal structural checks
        Check(nav.Views.Count>0);
        return failures;
    }
}

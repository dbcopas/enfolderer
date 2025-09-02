using System.Collections.Generic;
using System.Net.Http;

namespace Enfolderer.App.Tests;

public static class PageSlotBuilderTests
{
    public static int RunAll()
    {
        int failures=0; void Check(bool c){ if(!c) failures++; }
        var builder = new PageSlotBuilder();
        var http = new HttpClient(new HttpClientHandler());
        var faces = new List<CardEntry>{ new CardEntry("A","1","S",false), new CardEntry("B","2","S",false)};
        var slots = builder.BuildPageSlots(faces, 1, 9, http);
        Check(slots.Count==9);
        Check(slots[0].Name=="A" && slots[1].Name=="B");
        var display = builder.BuildPageDisplay(new NavigationService.PageView(1,2,0), 40);
        Check(display.Contains("Pages"));
        return failures;
    }
}

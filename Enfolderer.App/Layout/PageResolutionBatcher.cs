using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Enfolderer.App.Core;
using Enfolderer.App.Metadata;
using System.Threading.Tasks;
using System.Windows;
using System.Net.Http;
using System.Collections.ObjectModel;
using Enfolderer.App.Layout;

namespace Enfolderer.App;

public class PageResolutionBatcher
{
    public void Trigger(
        int[] pageNumbers,
        int slotsPerPage,
        List<CardSpec> specs,
        ConcurrentDictionary<int, CardEntry> mfcBacks,
        List<CardEntry> orderedFaces,
        Dictionary<string,string> explicitVariantPairKeys,
        SpecResolutionService specResolutionService,
        Func<string> getStatus,
        Action<string> setStatus,
        Action rebuildCardList,
        Action buildOrderedFaces,
        Action rebuildViews,
        NavigationService nav,
        IReadOnlyList<NavigationService.PageView> views,
        ObservableCollection<CardSlot> leftSlots,
    ObservableCollection<CardSlot> rightSlots,
    System.Net.Http.HttpClient http)
    {
        if (pageNumbers == null || pageNumbers.Length == 0) return;
        var neededSpecs = new HashSet<int>();
        int preFaceCount = orderedFaces.Count;
        foreach (var p in pageNumbers)
        {
            if (p <= 0) continue;
            int startFace = (p - 1) * slotsPerPage;
            int endFace = startFace + slotsPerPage * 2; // lookahead one page
            int faceCounter = 0;
            for (int si = 0; si < specs.Count && faceCounter < endFace; si++)
            {
                if (faceCounter >= startFace && faceCounter < endFace && specs[si].Resolved == null && !specs[si].explicitEntry)
                    neededSpecs.Add(si);
                faceCounter++;
                if (mfcBacks.ContainsKey(si)) faceCounter++;
            }
        }
        if (neededSpecs.Count == 0) return;
        var quickList = new List<(string setCode,string number,string? nameOverride,int specIndex)>();
        foreach (var si in neededSpecs)
        {
            var s = specs[si];
            quickList.Add((s.setCode, s.number, s.overrideName, si));
        }
        _ = Task.Run(async () =>
        {
            await specResolutionService.ResolveAsync(quickList, neededSpecs, 3, specs, mfcBacks, s => setStatus(s));
            Application.Current.Dispatcher.Invoke(() =>
            {
                rebuildCardList();
                buildOrderedFaces();
                bool faceCountChanged = orderedFaces.Count != preFaceCount;
                if (faceCountChanged)
                {
                    rebuildViews();
                }
                if (nav.CurrentIndex < views.Count)
                {
                    var v = views[nav.CurrentIndex];
                    leftSlots.Clear(); rightSlots.Clear();
                    var slotBuilder = new PageSlotBuilder();
                    if (v.LeftPage.HasValue)
                        foreach (var slot in slotBuilder.BuildPageSlots(orderedFaces, v.LeftPage.Value, slotsPerPage, http)) leftSlots.Add(slot);
                    if (v.RightPage.HasValue)
                        foreach (var slot in slotBuilder.BuildPageSlots(orderedFaces, v.RightPage.Value, slotsPerPage, http)) rightSlots.Add(slot);
                }
            });
        });
    }
}

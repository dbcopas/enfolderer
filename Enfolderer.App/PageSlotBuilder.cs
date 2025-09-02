using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Enfolderer.App;

/// <summary>
/// Builds page slot contents (CardSlot collections) for a given page number and computes page display text.
/// Responsible only for translating ordered faces into UI slot objects and kicking off image loads.
/// </summary>
public class PageSlotBuilder
{
    public List<CardSlot> BuildPageSlots(IReadOnlyList<CardEntry> orderedFaces, int pageNumber, int slotsPerPage, HttpClient http)
    {
        var result = new List<CardSlot>(slotsPerPage);
        if (pageNumber <= 0)
            return result;
        int startIndex = (pageNumber - 1) * slotsPerPage;
        var tasks = new List<Task>();
        for (int i = 0; i < slotsPerPage; i++)
        {
            int gi = startIndex + i;
            if (gi < orderedFaces.Count)
            {
                var face = orderedFaces[gi];
                var slot = new CardSlot(face, gi);
                result.Add(slot);
                // Always invoke image load, including for placeholder backfaces.
                // The CardSlot logic itself suppresses external HTTP for "__BACK__" and will
                // load the embedded or local resource instead. Previously we skipped these, which
                // prevented the back image from ever appearing.
                tasks.Add(slot.TryLoadImageAsync(http, face.Set ?? string.Empty, face.Number, face.IsBackFace));
            }
            else
            {
                result.Add(new CardSlot("(Empty)", gi));
            }
        }
        _ = Task.WhenAll(tasks); // fire and forget
        return result;
    }

    public string BuildPageDisplay(NavigationService.PageView view, int pagesPerBinder)
    {
        int binderNumber = view.BinderIndex + 1;
        if (view.LeftPage.HasValue && view.RightPage.HasValue)
        {
            int leftLocal = ((view.LeftPage.Value - 1) % pagesPerBinder) + 1;
            int rightLocal = ((view.RightPage.Value - 1) % pagesPerBinder) + 1;
            return $"Binder {binderNumber}: Pages {leftLocal}-{rightLocal}";
        }
        if (view.RightPage.HasValue)
        {
            int local = ((view.RightPage.Value - 1) % pagesPerBinder) + 1;
            return $"Binder {binderNumber}: Page {local} (Front Cover)";
        }
        if (view.LeftPage.HasValue)
        {
            int local = ((view.LeftPage.Value - 1) % pagesPerBinder) + 1;
            return $"Binder {binderNumber}: Page {local} (Back Cover)";
        }
        return "No pages";
    }
}

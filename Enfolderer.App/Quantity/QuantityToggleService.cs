using System;
using System.Collections.Generic;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Quantity;

public class QuantityToggleService : IQuantityToggleService
{
    private readonly IQuantityService _quantityService;
    private readonly ICollectionRepository _collectionRepo;
    private readonly CardCollectionData _collection;
    public QuantityToggleService(IQuantityService qty, ICollectionRepository repo, CardCollectionData collection)
    { _quantityService = qty; _collectionRepo = repo; _collection = collection; }

    public void Toggle(CardSlot slot,
        string? currentCollectionDir, // retained for signature compatibility but ignored now
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        Func<string,string,string,int?> resolveCardId,
        Action<string> setStatus)
    {
        if (slot == null) return;
        if (slot.IsPlaceholderBack) { setStatus("Back face placeholder"); return; }
        if (string.IsNullOrEmpty(slot.Set) || string.IsNullOrEmpty(slot.Number)) { setStatus("No set/number"); return; }
        // Always operate on executable directory for databases now.
        string exeDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        _collectionRepo.EnsureLoaded(exeDir);
        if (!_collection.IsLoaded) { setStatus("Collection not loaded"); return; }
        _quantityService.ToggleQuantity(slot, exeDir, _collection, cards, orderedFaces, resolveCardId, setStatus);
    }
}

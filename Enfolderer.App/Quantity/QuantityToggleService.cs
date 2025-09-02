using System;
using System.Collections.Generic;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Quantity;

public class QuantityToggleService
{
    private readonly CardQuantityService _quantityService;
    private readonly CollectionRepository _collectionRepo;
    private readonly CardCollectionData _collection;
    public QuantityToggleService(CardQuantityService qty, CollectionRepository repo, CardCollectionData collection)
    { _quantityService = qty; _collectionRepo = repo; _collection = collection; }

    public void Toggle(CardSlot slot,
        string? currentCollectionDir,
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        Func<string,string,string,int?> resolveCardId,
        Action<string> setStatus)
    {
        if (slot == null) return;
        if (slot.IsPlaceholderBack) { setStatus("Back face placeholder"); return; }
        if (string.IsNullOrEmpty(slot.Set) || string.IsNullOrEmpty(slot.Number)) { setStatus("No set/number"); return; }
        if (string.IsNullOrEmpty(currentCollectionDir)) { setStatus("No collection loaded"); return; }
        _collectionRepo.EnsureLoaded(currentCollectionDir);
        if (!_collection.IsLoaded) { setStatus("Collection not loaded"); return; }
    _quantityService.ToggleQuantity(slot, currentCollectionDir, _collection, cards, orderedFaces, resolveCardId, setStatus);
    }
}

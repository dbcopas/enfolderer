using System;
using System.Collections.Generic;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Thin abstraction for the UI action of toggling quantity for a selected slot.
/// </summary>
public interface IQuantityToggleService
{
    void Toggle(
        CardSlot slot,
        string? currentCollectionDir,
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        Func<string,string,string,int?> resolveCardId,
        Action<string> setStatus);
}

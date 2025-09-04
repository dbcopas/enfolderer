namespace Enfolderer.App.Core.Abstractions;

using System.Collections.Generic;
using Enfolderer.App.Core;

/// <summary>
/// Abstraction for adjusting displayed quantities for modal double-faced cards (MFC) and related heuristics.
/// Extracted from CardQuantityService (Phase 3 cleanup) so logic can be evolved independently.
/// </summary>
public interface IMfcQuantityAdjustmentService
{
    void Adjust(List<CardEntry> cards);
}
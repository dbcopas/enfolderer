using System;
using System.Collections.Generic;
using System.Diagnostics;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Core.Logging;

namespace Enfolderer.App.Quantity;

public sealed class MfcQuantityAdjustmentService : IMfcQuantityAdjustmentService
{
    private readonly IRuntimeFlags _flags;
    private readonly ILogSink? _log;
    public MfcQuantityAdjustmentService(IRuntimeFlags? flags = null, ILogSink? log = null)
    { _flags = flags ?? RuntimeFlags.Default; _log = log; }

    public void Adjust(List<CardEntry> cards)
    {
        if (cards == null || cards.Count == 0) return;
        // Original logic migrated from CardQuantityService.AdjustMfcQuantities
        for (int i = 0; i < cards.Count; i++)
        {
            var front = cards[i];
            if (!front.IsModalDoubleFaced || front.IsBackFace) continue;
            int q = front.Quantity; if (q < 0) continue;
            int frontDisplay, backDisplay;
            if (q <= 0) { frontDisplay = 0; backDisplay = 0; }
            else if (q == 1) { frontDisplay = 1; backDisplay = 0; }
            else { frontDisplay = 2; backDisplay = 2; }
            if (front.Quantity != frontDisplay) cards[i] = front with { Quantity = frontDisplay };
            int backIndex = -1;
            if (i + 1 < cards.Count)
            {
                var candidate = cards[i + 1];
                if (candidate.IsModalDoubleFaced && candidate.IsBackFace && candidate.Set == front.Set && candidate.Number == front.Number)
                    backIndex = i + 1;
            }
            if (backIndex == -1)
            {
                for (int j = i + 1; j < cards.Count; j++)
                {
                    var cand = cards[j];
                    if (cand.IsModalDoubleFaced && cand.IsBackFace && cand.Set == front.Set && cand.Number == front.Number)
                    { backIndex = j; break; }
                }
            }
            if (backIndex >= 0)
            {
                var back = cards[backIndex];
                if (back.Quantity != backDisplay) cards[backIndex] = back with { Quantity = backDisplay };
            }
        }

        var modalGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < cards.Count; gi++)
        {
            var c = cards[gi];
            bool treatAsModal = c.IsModalDoubleFaced || (!c.IsBackFace && !string.IsNullOrEmpty(c.FrontRaw) && !string.IsNullOrEmpty(c.BackRaw));
            if (!treatAsModal) continue;
            string setKey = c.Set ?? string.Empty;
            string numKey = c.Number?.Split('/')[0] ?? string.Empty;
            string composite = setKey + "|" + numKey;
            if (!modalGroups.TryGetValue(composite, out var list)) { list = new List<int>(); modalGroups[composite] = list; }
            list.Add(gi);
        }
        foreach (var list in modalGroups.Values)
        {
            if (list.Count != 2) continue;
            var a = cards[list[0]]; var b = cards[list[1]];
            if (a.IsBackFace || b.IsBackFace) continue;
            int qLogical = Math.Max(a.Quantity, b.Quantity);
            int frontDisplay, backDisplay;
            if (qLogical <= 0) { frontDisplay = 0; backDisplay = 0; }
            else if (qLogical == 1) { frontDisplay = 1; backDisplay = 0; }
            else { frontDisplay = 2; backDisplay = 2; }
            if (a.Quantity != frontDisplay) cards[list[0]] = a with { Quantity = frontDisplay };
            if (b.Quantity != backDisplay) cards[list[1]] = b with { Quantity = backDisplay };
            if (_flags.QtyDebug)
                _log?.Log($"FallbackAdjust heuristic split set={a.Set} num={a.Number} q={qLogical} front={frontDisplay} back={backDisplay} flags aMfc={a.IsModalDoubleFaced} bMfc={b.IsModalDoubleFaced}", LogCategories.Mfc);
        }
    }
}
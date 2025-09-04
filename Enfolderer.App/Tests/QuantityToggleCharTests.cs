using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Quantity;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization tests for quantity toggle behavior (baseline logical cycle + WAR star variant propagation).
/// </summary>
public static class QuantityToggleCharTests
{
    private static void MarkLoaded(CardCollectionData collection)
    {
        // Reflection to set _loadedFolder so IsLoaded returns true without constructing real DB files.
        try
        {
            var f = typeof(CardCollectionData).GetField("_loadedFolder", BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(collection, "test");
        }
        catch { }
    }

    private static void Log(bool ok, ref int failures, string msg)
    { if(!ok){ failures++; try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_qtytoggle_char.txt"), "FAIL "+msg+"\n"); } catch {} } else { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_qtytoggle_char.txt"), "OK "+msg+"\n"); } catch {} } }

    public static int RunAll()
    {
        int failures = 0;
        // Scenario 1: Basic toggle 0 -> 1 -> 0 updates collection quantities.
        {
            var collection = new CardCollectionData();
            MarkLoaded(collection);
            collection.MainIndex[("set","7")] = (101, null);
            var faces = new List<CardEntry>{ new CardEntry("Sample","7","SET", false) };
            var ordered = new List<CardEntry>(faces);
            var slot = new CardSlot(faces[0], 0);
            var svc = new CardQuantityService();
            int q1 = svc.ToggleQuantity(slot, "", collection, faces, ordered, (s,b,t)=>null, _=>{});
            Log(q1==1 && slot.Quantity==1 && collection.Quantities.ContainsKey(("set","7")), ref failures, "Basic toggle 0->1");
            int q2 = svc.ToggleQuantity(slot, "", collection, faces, ordered, (s,b,t)=>null, _=>{});
            Log(q2==0 && slot.Quantity==0 && !collection.Quantities.ContainsKey(("set","7")), ref failures, "Basic toggle 1->0");
        }
        // Scenario 2: WAR star variant updates variant quantities.
        {
            var collection = new CardCollectionData();
            MarkLoaded(collection);
            collection.MainIndex[("war","7")] = (201, null); // star-stripped index
            var faces = new List<CardEntry>{ new CardEntry("WarStar","7★","WAR", false) };
            var ordered = new List<CardEntry>(faces);
            var slot = new CardSlot(faces[0], 0);
            var svc = new CardQuantityService();
            int q1 = svc.ToggleQuantity(slot, "", collection, faces, ordered, (s,b,t)=>null, _=>{});
            bool variantUpdated = collection.VariantQuantities.ContainsKey(("war","7","art jp")) || collection.VariantQuantities.ContainsKey(("war","7","jp"));
            Log(q1==1 && slot.Quantity==1 && variantUpdated, ref failures, "WAR star variant toggle propagation");
        }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_qtytoggle_char.txt"), $"DONE failures={failures}\n"); } catch {}
        return failures;
    }
}

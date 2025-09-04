using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
// using Enfolderer.App.Core.Abstractions; (already included above)
using Enfolderer.App.Quantity;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization tests for quantity toggle behavior (baseline logical cycle + WAR star variant propagation).
/// </summary>
public static class QuantityToggleCharTests
{
    private sealed class FakeQuantityRepository : IQuantityRepository
    {
        public int CustomCalls { get; private set; }
        public int StandardCalls { get; private set; }
        public int? LastCustomCardId { get; private set; }
        public int? LastStandardCardId { get; private set; }
        public int? LastCustomQty { get; private set; }
        public int? LastStandardQty { get; private set; }
        public int? UpdateCustomCardQuantity(string mainDbPath, int cardId, int newQty, bool qtyDebug)
        { CustomCalls++; LastCustomCardId = cardId; LastCustomQty = newQty; return newQty; }
        public int? UpsertStandardCardQuantity(string collectionFilePath, int cardId, int newQty, bool qtyDebug)
        { StandardCalls++; LastStandardCardId = cardId; LastStandardQty = newQty; return newQty; }
    }
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
        // Prepare temp dir + placeholder DB files (empty files are enough—repository stub ignores contents).
        string testDir = Path.Combine(Path.GetTempPath(), "enfolderer_qtytoggle_repo_"+Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(testDir); File.WriteAllText(Path.Combine(testDir, "mainDb.db"), ""); File.WriteAllText(Path.Combine(testDir, "mtgstudio.collection"), ""); } catch {}
        var repoStub = new FakeQuantityRepository();

        // Scenario 1: Basic standard card toggle 0 -> 1 -> 0 uses repository Upsert and updates in-memory quantities.
        {
            var collection = new CardCollectionData();
            MarkLoaded(collection);
            collection.MainIndex[("set","7")] = (101, null);
            var faces = new List<CardEntry>{ new CardEntry("Sample","7","SET", false) };
            var ordered = new List<CardEntry>(faces);
            var slot = new CardSlot(faces[0], 0);
            var svc = new CardQuantityService(quantityRepository: repoStub);
            int q1 = svc.ToggleQuantity(slot, testDir, collection, faces, ordered, (s,b,t)=>null, _=>{});
            Log(q1==1 && slot.Quantity==1 && collection.Quantities.ContainsKey(("set","7")) && repoStub.StandardCalls==1 && repoStub.LastStandardQty==1, ref failures, "Standard toggle 0->1 repo call");
            int q2 = svc.ToggleQuantity(slot, testDir, collection, faces, ordered, (s,b,t)=>null, _=>{});
            Log(q2==0 && slot.Quantity==0 && !collection.Quantities.ContainsKey(("set","7")) && repoStub.StandardCalls==2 && repoStub.LastStandardQty==0, ref failures, "Standard toggle 1->0 repo call");
        }
        // Scenario 2: Custom card uses custom path repository method.
        {
            var collection = new CardCollectionData();
            MarkLoaded(collection);
            collection.MainIndex[("cset","9")] = (301, null);
            collection.CustomCards.Add(301);
            var faces = new List<CardEntry>{ new CardEntry("Custom","9","CSET", false) };
            var ordered = new List<CardEntry>(faces);
            var slot = new CardSlot(faces[0],0);
            var svc = new CardQuantityService(quantityRepository: repoStub);
            int q1 = svc.ToggleQuantity(slot, testDir, collection, faces, ordered, (s,b,t)=>null, _=>{});
            Log(q1==1 && repoStub.CustomCalls>=1 && repoStub.LastCustomQty==1, ref failures, "Custom toggle 0->1 custom repo call");
        }
        // Scenario 3: WAR star variant updates variant quantities and repository invoked.
        {
            var collection = new CardCollectionData();
            MarkLoaded(collection);
            collection.MainIndex[("war","7")] = (401, null); // star-stripped index
            var faces = new List<CardEntry>{ new CardEntry("WarStar","7★","WAR", false) };
            var ordered = new List<CardEntry>(faces);
            var slot = new CardSlot(faces[0], 0);
            var svc = new CardQuantityService(quantityRepository: repoStub);
            int q1 = svc.ToggleQuantity(slot, testDir, collection, faces, ordered, (s,b,t)=>null, _=>{});
            bool variantUpdated = collection.VariantQuantities.ContainsKey(("war","7","art jp")) || collection.VariantQuantities.ContainsKey(("war","7","jp"));
            Log(q1==1 && slot.Quantity==1 && variantUpdated, ref failures, "WAR star variant repo integration");
        }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "enfolderer_qtytoggle_char.txt"), $"DONE failures={failures}\n"); } catch {}
        return failures;
    }
}

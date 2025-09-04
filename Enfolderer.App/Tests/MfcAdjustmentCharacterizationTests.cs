using System.Collections.Generic;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Quantity;
using Enfolderer.App.Collection;

namespace Enfolderer.App.Tests;

public static class MfcAdjustmentCharacterizationTests
{
    public static int Run()
    {
        int failures = 0;
        failures += TwoFacePairLogic();
        failures += MixedOrderStillAligns();
        failures += MissingBackFaceGraceful();
    failures += DisplaySplitMapping();
        return failures;
    }

    private static int TwoFacePairLogic()
    {
        var flags = RuntimeFlags.Default;
    var svc = new CardQuantityService(flags, mfcAdjustment: new MfcQuantityAdjustmentService(flags));
        var cards = new List<CardEntry>
        {
            new CardEntry("Front A","001","SET", true, false, null, null, null, 3),
            new CardEntry("Back A","001","SET", true, true, null, null, null, 0),
        };
    // For pure MFC logic tests, call adjustment directly to avoid enrichment side-effects
    svc.AdjustMfcQuantities(cards);
        return (cards[0].Quantity==2 && cards[1].Quantity==2) ? 0 : 1;
    }

    private static int MixedOrderStillAligns()
    {
        var flags = RuntimeFlags.Default;
    var svc = new CardQuantityService(flags, mfcAdjustment: new MfcQuantityAdjustmentService(flags));
        var cards = new List<CardEntry>
        {
            new CardEntry("Front B1","002","SET", true, false, null, null, null, 2),
            new CardEntry("Front C1","003","SET", true, false, null, null, null, 1),
            new CardEntry("Back B1","002","SET", true, true, null, null, null, 0),
            new CardEntry("Back C1","003","SET", true, true, null, null, null, 0),
        };
    svc.AdjustMfcQuantities(cards);
        return (cards[0].Quantity==2 && cards[2].Quantity==2 && cards[1].Quantity==1 && cards[3].Quantity==0) ? 0 : 1;
    }

    private static int MissingBackFaceGraceful()
    {
        var flags = RuntimeFlags.Default;
    var svc = new CardQuantityService(flags, mfcAdjustment: new MfcQuantityAdjustmentService(flags));
        var cards = new List<CardEntry>
        {
            new CardEntry("Front Solo","010","SET", true, false, null, null, null, 1)
        };
    svc.AdjustMfcQuantities(cards);
        return cards[0].Quantity==1 ? 0 : 1;
    }

    // New: Verify logical -> display mapping for MFC front/back: 0 => 0/0, 1 => 1/0, 2 => 2/2, 3 => 2/2.
    private static int DisplaySplitMapping()
    {
        int failures = 0;
        var flags = RuntimeFlags.Default;
        var svc = new CardQuantityService(flags, mfcAdjustment: new MfcQuantityAdjustmentService(flags));
        int[] logicals = {0,1,2,3};
        foreach (var logical in logicals)
        {
            var cards = new List<CardEntry>
            {
                new CardEntry("Front","020","SET", true, false, null, null, null, logical),
                new CardEntry("Back","020","SET", true, true, null, null, null, 0)
            };
            svc.AdjustMfcQuantities(cards);
            int expectedFront = logical <=0 ? 0 : (logical==1?1:2);
            int expectedBack = logical >=2 ? 2 : 0;
            if (cards[0].Quantity != expectedFront || cards[1].Quantity != expectedBack)
                failures++;
        }
        // Simulate toggle path sequences: 0->1->2->0 using service ToggleQuantity to ensure integration aligns
        var collection = new CardCollectionData();
        // Mark loaded (reflection hack used elsewhere) to allow ToggleQuantity to proceed
        try { var f = typeof(CardCollectionData).GetField("_loadedFolder", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance); f?.SetValue(collection, "test"); } catch {}
        collection.MainIndex[("set","021")] = (9001, null);
        var facesToggle = new List<CardEntry>{ new CardEntry("FrontT","021","SET", true, false, null, null, null, 0), new CardEntry("BackT","021","SET", true, true, null, null, null, 0)};
        var orderedToggle = new List<CardEntry>(facesToggle);
        var slotFront = new CardSlot(facesToggle[0],0);
        int step1 = svc.ToggleQuantity(slotFront, "test", collection, facesToggle, orderedToggle, (a,b,c)=>9001, _=>{}); // 0->1
        svc.AdjustMfcQuantities(facesToggle); // ensure display rule
        if (facesToggle[0].Quantity !=1 || facesToggle[1].Quantity!=0) failures++;
        int step2 = svc.ToggleQuantity(slotFront, "test", collection, facesToggle, orderedToggle, (a,b,c)=>9001, _=>{}); // 1->2
        svc.AdjustMfcQuantities(facesToggle);
        if (facesToggle[0].Quantity !=2 || facesToggle[1].Quantity!=2) failures++;
        int step3 = svc.ToggleQuantity(slotFront, "test", collection, facesToggle, orderedToggle, (a,b,c)=>9001, _=>{}); // 2->0
        svc.AdjustMfcQuantities(facesToggle);
        if (facesToggle[0].Quantity !=0 || facesToggle[1].Quantity!=0) failures++;
        return failures;
    }
}

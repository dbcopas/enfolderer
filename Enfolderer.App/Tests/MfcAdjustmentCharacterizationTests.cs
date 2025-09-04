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
}

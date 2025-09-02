using System.Collections.Generic;

namespace Enfolderer.App.Tests;

public static class QuantityServicesTests
{
    public static int RunAll()
    {
        int failures=0; void Check(bool c){ if(!c) failures++; }
        var qtyService = new CardQuantityService();
        var collection = new CardCollectionData();
        collection.Quantities[("set", "1")] = 3;
        var faces = new List<CardEntry>{ new CardEntry("Alpha","1","SET",false) };
        qtyService.EnrichQuantities(collection, faces);
        Check(faces[0].Quantity==3);
    // Simulate mainDb-only custom card quantity (custom cards store quantity directly in Quantities via load)
    var mainOnlyCollection = new CardCollectionData();
    mainOnlyCollection.Quantities[("custom", "5")] = 2; // mimic loader effect
    var customFaces = new List<CardEntry>{ new CardEntry("Custom Card","5","CUSTOM",false) };
    qtyService.EnrichQuantities(mainOnlyCollection, customFaces);
    Check(customFaces[0].Quantity==2);
        // MFC adjust
        var mfcFaces = new List<CardEntry>{ new CardEntry("Front/Back|MFC","10","SET",true,false,"Front","Back"), new CardEntry("Front/Back|MFC","10","SET",true,true,"Front","Back") };
        // Assign quantity manually to front then adjust
        mfcFaces[0] = mfcFaces[0] with { Quantity = 2 };
        qtyService.AdjustMfcQuantities(mfcFaces);
        Check(mfcFaces[0].Quantity==2 && mfcFaces[1].Quantity==2);
        var enrichment = new QuantityEnrichmentService(qtyService);
        enrichment.Enrich(collection, faces); // should not throw
    // Alt suffix fallback test
    var suffixCollection = new CardCollectionData();
    suffixCollection.Quantities[("set", "12a")] = 4;
    var suffixFaces = new List<CardEntry>{ new CardEntry("Suffix Card","12a","SET",false) };
    qtyService.EnrichQuantities(suffixCollection, suffixFaces);
    Check(suffixFaces[0].Quantity==4);
    // Leading zero trim alias test
    var trimCollection = new CardCollectionData();
    trimCollection.Quantities[("set", "7")] = 5;
    var trimFaces = new List<CardEntry>{ new CardEntry("Trim Card","007","SET",false) };
    qtyService.EnrichQuantities(trimCollection, trimFaces);
    Check(trimFaces[0].Quantity==5);
        return failures;
    }
}

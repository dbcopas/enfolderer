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
        // MFC adjust
        var mfcFaces = new List<CardEntry>{ new CardEntry("Front/Back|MFC","10","SET",true,false,"Front","Back"), new CardEntry("Front/Back|MFC","10","SET",true,true,"Front","Back") };
        // Assign quantity manually to front then adjust
        mfcFaces[0] = mfcFaces[0] with { Quantity = 2 };
        qtyService.AdjustMfcQuantities(mfcFaces);
        Check(mfcFaces[0].Quantity==2 && mfcFaces[1].Quantity==2);
        var enrichment = new QuantityEnrichmentService(qtyService);
        enrichment.Enrich(collection, faces); // should not throw
        return failures;
    }
}

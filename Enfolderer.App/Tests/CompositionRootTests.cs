using System;
using System.Collections.Generic;
using System.IO;
using Enfolderer.App.Core.Composition;
using Enfolderer.App.Binder;
using Enfolderer.App.Imaging;
using Enfolderer.App.Metadata;
using Enfolderer.App.Quantity;
using Enfolderer.App.Core;

namespace Enfolderer.App.Tests;

/// <summary>
/// Lightweight smoke tests for CompositionRoot wiring (characterization style).
/// </summary>
public static class CompositionRootTests
{
    public static int RunAll()
    {
        int failures = 0; void Check(bool c){ if(!c) failures++; }
        try
        {
            var theme = new BinderThemeService();
            var qtySvc = new CardQuantityService();
            var backImg = new CardBackImageService();
            var resolver = new CardMetadataResolver(ImageCacheStore.CacheRoot, new[]{"transform"}, 5);
            bool IsMetaComplete(string h) => true; // deterministic stub
            var repo = new Enfolderer.App.Collection.CollectionRepository(new Enfolderer.App.Collection.CardCollectionData());
            var collection = new Enfolderer.App.Collection.CardCollectionData();
            var graph = CompositionRoot.BuildExisting(theme, qtySvc, backImg, resolver, IsMetaComplete, () => new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler()), repo, collection);
            Check(graph.BinderLoad != null);
            Check(graph.Orchestrator != null);
            Check(graph.SpecResolution != null);
            Check(graph.ResolverAdapter != null);
            // Quantity toggle service optional; if absent ensure default instantiation works
            var toggle = graph.QuantityToggleService ?? new QuantityToggleService(qtySvc, repo, collection);
            Check(toggle != null);
        }
        catch { failures++; }
        return failures;
    }
}

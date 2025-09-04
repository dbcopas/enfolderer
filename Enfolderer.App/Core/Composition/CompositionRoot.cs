using System;
using Enfolderer.App.Binder;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Metadata;
using Enfolderer.App.Quantity;
using Enfolderer.App.Imaging;

namespace Enfolderer.App.Core.Composition;

/// <summary>
/// Centralized wiring for core services so UI layer isn't responsible for manual new chains.
/// Non-invasive: can be removed after fuller DI framework if desired.
/// </summary>
public static class CompositionRoot
{
    public sealed record AppServiceGraph(
        CardMetadataResolver Resolver,
        ICardMetadataResolver ResolverAdapter,
        IBinderFileParser Parser,
        IBinderLoadService BinderLoad,
        SpecResolutionService SpecResolution,
        MetadataLoadOrchestrator Orchestrator,
        IQuantityService QuantityService,
        IQuantityToggleService? QuantityToggleService);

    /// <summary>
    /// Build graph using an existing concrete resolver (so existing readonly field can stay).
    /// </summary>
    public static AppServiceGraph BuildExisting(
        BinderThemeService binderTheme,
        CardQuantityService quantityService,
        CardBackImageService backImageService,
        CardMetadataResolver resolver,
        Func<string,bool> isCacheComplete,
        Enfolderer.App.Collection.CollectionRepository? collectionRepo = null,
        Enfolderer.App.Collection.CardCollectionData? collectionData = null)
    {
        var adapter = new CardMetadataResolverAdapter(resolver);
        // Construct concrete parser + adapter stack but expose only IBinderFileParser outward.
        var concreteParser = new BinderFileParser(binderTheme, resolver, _ => backImageService.Resolve(null, _), isCacheComplete);
        IBinderFileParser parserAdapter = new BinderFileParserAdapter(concreteParser);
        var binderLoad = new BinderLoadService(binderTheme, parserAdapter);
        var specResolution = new SpecResolutionService(adapter);
        var orchestrator = new MetadataLoadOrchestrator(specResolution, quantityService, adapter);
        IQuantityToggleService? qtyToggle = null;
        if (collectionRepo != null && collectionData != null)
            qtyToggle = new Enfolderer.App.Quantity.QuantityToggleService(quantityService, collectionRepo, collectionData);
        return new AppServiceGraph(resolver, adapter, parserAdapter, binderLoad, specResolution, orchestrator, quantityService, qtyToggle);
    }
}

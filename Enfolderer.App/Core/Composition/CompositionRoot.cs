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
        BinderLoadService BinderLoad,
        SpecResolutionService SpecResolution,
        MetadataLoadOrchestrator Orchestrator);

    /// <summary>
    /// Build graph using an existing concrete resolver (so existing readonly field can stay).
    /// </summary>
    public static AppServiceGraph BuildExisting(
        BinderThemeService binderTheme,
        CardQuantityService quantityService,
        CardBackImageService backImageService,
        CardMetadataResolver resolver,
        Func<string,bool> isCacheComplete)
    {
        var adapter = new CardMetadataResolverAdapter(resolver);
        var binderLoad = new BinderLoadService(binderTheme, adapter, backImageService, isCacheComplete);
        var specResolution = new SpecResolutionService(adapter);
        var orchestrator = new MetadataLoadOrchestrator(specResolution, quantityService, adapter);
        return new AppServiceGraph(resolver, adapter, binderLoad, specResolution, orchestrator);
    }
}

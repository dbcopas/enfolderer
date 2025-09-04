using Enfolderer.App.Collection;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Imaging;
using Enfolderer.App.Metadata;
using Enfolderer.App.Quantity;
using Enfolderer.App.Binder;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Core.Composition;

/// <summary>
/// Central application bootstrapper to create core service graph and ancillary infrastructure without UI concerns.
/// </summary>
public static class AppBootstrapper
{
    public sealed record AppRuntimeServices(
        CardCollectionData Collection,
        CollectionRepository CollectionRepo,
        CardQuantityService QuantityService,
        QuantityEnrichmentService QuantityEnrichment,
        IQuantityToggleService QuantityToggle,
        CardBackImageService BackImageService,
        CachePathService CachePaths,
        StatusPanelService StatusPanel,
        TelemetryService Telemetry,
        IHttpClientFactoryService HttpFactory,
        CompositionRoot.AppServiceGraph CoreGraph,
        IMetadataCachePersistence CachePersistence);

    public static AppRuntimeServices Build(string cacheRoot, BinderThemeService binderTheme, System.Func<string,bool> isMetaComplete)
    {
        var collection = new CardCollectionData();
        var repo = new CollectionRepository(collection);
        var qtySvc = new CardQuantityService();
        var qtyEnrichment = new QuantityEnrichmentService(qtySvc);
    var backImg = new CardBackImageService();
    var cachePaths = new CachePathService(cacheRoot);
    var statusPanel = new StatusPanelService(_ => { });
    var telemetry = new TelemetryService(s => { }, debugEnabled:false);
    var httpFactory = new HttpClientFactoryService(telemetry);
    // Inline constants (keep in sync with MainWindow): schemaVersion=5, physically two-sided layouts list
    var resolver = new CardMetadataResolver(cacheRoot, new[]{"transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"}, 5);
        var coreGraph = CompositionRoot.BuildExisting(binderTheme, qtySvc, backImg, resolver, isMetaComplete, repo, collection);
        var cachePersistence = new MetadataCachePersistenceAdapter(coreGraph.ResolverAdapter);
    IQuantityToggleService qtyToggle = coreGraph.QuantityToggleService ?? new QuantityToggleService(qtySvc, repo, collection);
        return new AppRuntimeServices(collection, repo, qtySvc, qtyEnrichment, qtyToggle, backImg, cachePaths, statusPanel, telemetry, httpFactory, coreGraph, cachePersistence);
    }
}

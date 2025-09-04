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
    IQuantityOrchestrator QuantityOrchestrator,
        QuantityEnrichmentCoordinator QuantityCoordinator,
        IQuantityToggleService QuantityToggle,
        CardBackImageService BackImageService,
        CachePathService CachePaths,
        StatusPanelService StatusPanel,
        TelemetryService Telemetry,
        IHttpClientFactoryService HttpFactory,
        CompositionRoot.AppServiceGraph CoreGraph,
    IMetadataCachePersistence CachePersistence,
    Enfolderer.App.Layout.PageViewPresenter PagePresenter,
    Enfolderer.App.PageResolutionBatcher PageBatcher,
    Enfolderer.App.Core.Abstractions.ICardArrangementService ArrangementService,
    Enfolderer.App.Core.Abstractions.IImportService ImportService,
    Enfolderer.App.Core.Abstractions.IMetadataProvider MetadataProvider);

    public static AppRuntimeServices Build(
        string cacheRoot,
        BinderThemeService binderTheme,
        System.Func<string,bool> isMetaComplete,
        CardCollectionData? existingCollection = null,
        CollectionRepository? existingRepository = null,
    Enfolderer.App.Core.Abstractions.ILogSink? existingLog = null,
        IMfcQuantityAdjustmentService? existingMfcAdjust = null)
    {
        var collection = existingCollection ?? new CardCollectionData();
    var log = existingLog as Enfolderer.App.Core.Abstractions.ILogSink ?? new DebugLogSink();
        var repo = existingRepository ?? new CollectionRepository(collection, log);
        Enfolderer.App.Core.Logging.LogHost.Sink = log;
        var mfcAdjust = existingMfcAdjust ?? new MfcQuantityAdjustmentService(log: log);
    var flagSvc = new RuntimeFlagService(RuntimeFlags.Default);
    var qtySvc = new CardQuantityService(quantityRepository: repo, log: log, mfcAdjustment: mfcAdjust, flagService: flagSvc);
    var qtyCoordinator = new QuantityEnrichmentCoordinator();
    var backImg = new CardBackImageService();
    var cachePaths = new CachePathService(cacheRoot);
    var statusPanel = new StatusPanelService(_ => { });
    var telemetry = new TelemetryService(s => { }, debugEnabled:false);
    var httpFactory = new HttpClientFactoryService(telemetry);
    // Inline constants (keep in sync with MainWindow): schemaVersion=5, physically two-sided layouts list
    var resolver = new CardMetadataResolver(cacheRoot, new[]{"transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"}, 5, log);
    var coreGraph = CompositionRoot.BuildExisting(binderTheme, qtySvc, backImg, resolver, isMetaComplete, () => httpFactory.Client, repo, collection);
    var cachePersistence = new MetadataCachePersistenceAdapter(coreGraph.ResolverAdapter);
    IQuantityToggleService qtyToggle = coreGraph.QuantityToggleService ?? new QuantityToggleService(qtySvc, repo, collection);
    var pagePresenter = new Enfolderer.App.Layout.PageViewPresenter();
    var pageBatcher = new Enfolderer.App.PageResolutionBatcher();
    var orchestrator = new QuantityOrchestrator(qtySvc);
    return new AppRuntimeServices(collection, repo, qtySvc, orchestrator, qtyCoordinator, qtyToggle, backImg, cachePaths, statusPanel, telemetry, httpFactory, coreGraph, cachePersistence, pagePresenter, pageBatcher, coreGraph.ArrangementService, coreGraph.ImportService, coreGraph.MetadataProvider);
    }
}

using Azure.Core;
using Azure.Storage.Queues;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Worker;
using Enfolderer.Ai.Worker.Agents;
using Enfolderer.Ai.Worker.Pipeline;
using Enfolderer.Ai.Worker.Queueing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddScanPlatform(builder.Configuration, registerUploadUrlIssuer: false);

var pipeline = new ScanPipelineOptions();
builder.Configuration.GetSection(ScanPipelineOptions.SectionName).Bind(pipeline);
builder.Services.AddSingleton(pipeline);

builder.Services.AddHttpClient();

// Queue consumer, matching whichever queue the API is configured to publish to.
builder.Services.AddSingleton<IJobQueueConsumer>(sp =>
{
    var platform = sp.GetRequiredService<ScanPlatformOptions>();
    if (!platform.UsesAzureQueue)
        return new LocalDirectoryQueueConsumer(platform.LocalStorageRoot, sp.GetRequiredService<ILogger<LocalDirectoryQueueConsumer>>());

    var queueService = new QueueServiceClient(new Uri(platform.QueueAccountUrl!), sp.GetRequiredService<TokenCredential>());
    return new StorageQueueConsumer(queueService.GetQueueClient(platform.QueueName), sp.GetRequiredService<ILogger<StorageQueueConsumer>>());
});

// Team A: the geometry project gets its own client, so a missing RBAC assignment fails here and
// nowhere else.
builder.Services.AddSingleton<ICardBoundaryAgent>(sp =>
{
    if (!pipeline.UsesFoundryGeometry)
    {
        return new StubCardBoundaryAgent(
            sp.GetRequiredService<IScanImageStore>(),
            sp.GetRequiredService<ILogger<StubCardBoundaryAgent>>());
    }

    var client = CreateFoundryClient(sp, pipeline.GeometryProjectEndpoint!);
    return new FoundryCardBoundaryAgent(client, pipeline.BoundaryAgentId, sp.GetRequiredService<ILogger<FoundryCardBoundaryAgent>>());
});

// Team B: one identification agent per game that has a deployed agent id.
foreach (var profile in GameAgentProfile.Live)
{
    var captured = profile;
    builder.Services.AddSingleton<ICardIdentificationAgent>(sp =>
    {
        if (!pipeline.UsesFoundryIdentification ||
            !pipeline.IdentificationAgentIds.TryGetValue(captured.Game, out var agentId) ||
            string.IsNullOrWhiteSpace(agentId))
        {
            return new StubCardIdentificationAgent(captured.Game);
        }

        var client = CreateFoundryClient(sp, pipeline.IdentificationProjectEndpoint!);
        return new FoundryCardIdentificationAgent(
            client, captured, agentId, sp.GetRequiredService<ILogger<FoundryCardIdentificationAgent>>());
    });
}

builder.Services.AddSingleton<ScanJobProcessor>();
builder.Services.AddHostedService<ScanJobWorker>();

var host = builder.Build();
host.Run();

static FoundryAgentClient CreateFoundryClient(IServiceProvider sp, string endpoint) => new(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FoundryAgentClient)),
    sp.GetRequiredService<TokenCredential>(),
    sp.GetRequiredService<ILogger<FoundryAgentClient>>(),
    endpoint,
    sp.GetRequiredService<ScanPipelineOptions>().FoundryApiVersion,
    sp.GetRequiredService<ScanPipelineOptions>().AgentRunTimeout,
    sp.GetRequiredService<ScanPipelineOptions>().AgentPollInterval);

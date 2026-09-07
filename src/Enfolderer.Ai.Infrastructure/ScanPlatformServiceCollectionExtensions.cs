using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Infrastructure;

/// <summary>
/// Registers the storage, queue and job-store services shared by the API and the worker, selecting
/// the Azure implementation when configured and the local development fallback otherwise.
/// </summary>
public static class ScanPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddScanPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        bool registerUploadUrlIssuer)
    {
        var options = new ScanPlatformOptions();
        configuration.GetSection(ScanPlatformOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // DefaultAzureCredential picks up the managed identity in Azure and the developer's
        // az/VS login locally. No secrets are ever read from configuration.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton<IScanImageStore>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ScanPlatformServiceCollectionExtensions));
            if (!options.UsesAzureStorage)
            {
                log.LogWarning("No StorageAccountUrl configured; using local image store at {Root}.", options.LocalStorageRoot);
                return new LocalDirectoryScanImageStore(options.LocalStorageRoot);
            }
            return new BlobScanImageStore(CreateBlobServiceClient(sp, options));
        });

        services.AddSingleton<IJobStore>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ScanPlatformServiceCollectionExtensions));
            if (!options.UsesCosmos)
            {
                log.LogWarning("No CosmosEndpoint configured; job state is in-memory and will not survive a restart.");
                return new InMemoryJobStore();
            }

            var cosmos = new CosmosClient(
                options.CosmosEndpoint,
                sp.GetRequiredService<TokenCredential>(),
                new CosmosClientOptions { Serializer = new SystemTextJsonCosmosSerializer() });
            var container = cosmos.GetContainer(options.CosmosDatabase, options.CosmosContainer);
            return new CosmosJobStore(container);
        });

        services.AddSingleton<IJobQueue>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ScanPlatformServiceCollectionExtensions));
            if (!options.UsesAzureQueue)
            {
                log.LogWarning("No QueueAccountUrl configured; using local file queue under {Root}.", options.LocalStorageRoot);
                return new LocalDirectoryJobQueue(options.LocalStorageRoot);
            }
            var queueService = new QueueServiceClient(new Uri(options.QueueAccountUrl!), sp.GetRequiredService<TokenCredential>());
            return new StorageQueueJobQueue(queueService.GetQueueClient(options.QueueName));
        });

        services.AddSingleton<IReadUrlProvider>(sp =>
        {
            if (!options.UsesAzureStorage)
                return new LocalReadUrlProvider(options.LocalStorageRoot);
            return new BlobReadUrlProvider(CreateBlobServiceClient(sp, options));
        });

        if (registerUploadUrlIssuer)
        {
            services.AddSingleton<IUploadUrlIssuer>(sp =>
            {
                if (!options.UsesAzureStorage)
                    return new LocalUploadUrlIssuer(options.LocalApiBaseUrl, options.UploadUrlLifetime);
                return new BlobSasUploadUrlIssuer(CreateBlobServiceClient(sp, options), options.UploadUrlLifetime);
            });
        }

        return services;
    }

    private static BlobServiceClient CreateBlobServiceClient(IServiceProvider sp, ScanPlatformOptions options) =>
        new(new Uri(options.StorageAccountUrl!), sp.GetRequiredService<TokenCredential>());
}

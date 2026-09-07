namespace Enfolderer.Ai.Infrastructure;

/// <summary>
/// Configuration shared by the API and the worker. Every value is optional: when a resource is not
/// configured the corresponding local development fallback is used, so the whole upload/poll loop
/// can be demonstrated without deploying anything.
/// </summary>
public sealed class ScanPlatformOptions
{
    public const string SectionName = "ScanPlatform";

    /// <summary>Blob endpoint, e.g. <c>https://mystorage.blob.core.windows.net</c>.</summary>
    public string? StorageAccountUrl { get; set; }

    /// <summary>Queue endpoint, e.g. <c>https://mystorage.queue.core.windows.net</c>.</summary>
    public string? QueueAccountUrl { get; set; }

    /// <summary>Name of the queue carrying <c>ScanJobMessage</c> payloads.</summary>
    public string QueueName { get; set; } = "scan-jobs";

    /// <summary>Cosmos DB account endpoint, e.g. <c>https://mycosmos.documents.azure.com:443/</c>.</summary>
    public string? CosmosEndpoint { get; set; }

    public string CosmosDatabase { get; set; } = "enfolderer";

    public string CosmosContainer { get; set; } = "jobs";

    /// <summary>Lifetime of the write-only upload SAS handed to the desktop client.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Root directory used by every local development fallback (image store and file queue).
    /// The API and the worker must point at the same path.
    /// </summary>
    public string LocalStorageRoot { get; set; } =
        Path.Combine(Path.GetTempPath(), "enfolderer-scan-dev");

    /// <summary>Base URL the API advertises to clients when issuing local upload URLs.</summary>
    public string LocalApiBaseUrl { get; set; } = "http://localhost:5080";

    public bool UsesAzureStorage => !string.IsNullOrWhiteSpace(StorageAccountUrl);

    public bool UsesAzureQueue => !string.IsNullOrWhiteSpace(QueueAccountUrl);

    public bool UsesCosmos => !string.IsNullOrWhiteSpace(CosmosEndpoint);
}

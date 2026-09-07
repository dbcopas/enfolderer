using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Queueing;

/// <summary>Message handed from the API to the worker when a job is submitted.</summary>
public sealed record ScanJobMessage(string JobId, string BlobPath, string? GameHint);

/// <summary>Decouples the API from the worker so the API never calls Foundry directly.</summary>
public interface IJobQueue
{
    Task EnqueueAsync(ScanJobMessage message, CancellationToken ct = default);
}

/// <summary>Azure Storage Queue implementation, authenticated with the API's managed identity.</summary>
public sealed class StorageQueueJobQueue : IJobQueue
{
    private readonly QueueClient _queue;

    public StorageQueueJobQueue(QueueClient queue) => _queue = queue;

    public async Task EnqueueAsync(ScanJobMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, ScanJson.Options);
        // Storage queues require Base64 when the consumer uses the default message encoding.
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        await _queue.SendMessageAsync(payload, ct);
    }
}

/// <summary>
/// Development fallback that writes the message as a file into the shared local root so a locally
/// running worker can pick it up without any Azure resources.
/// </summary>
public sealed class LocalDirectoryJobQueue : IJobQueue
{
    public const string QueueFolder = "queue";

    private readonly string _root;

    public LocalDirectoryJobQueue(string root)
    {
        _root = Path.Combine(Path.GetFullPath(root), QueueFolder);
        Directory.CreateDirectory(_root);
    }

    public async Task EnqueueAsync(ScanJobMessage message, CancellationToken ct = default)
    {
        var name = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{ScanBlobPaths.SanitizeSegment(message.JobId)}.json";
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(message, ScanJson.Options), ct);
    }
}

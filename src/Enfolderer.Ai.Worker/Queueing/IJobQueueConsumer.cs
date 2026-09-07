using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Queueing;

/// <summary>Pull side of the job queue. Mirrors <see cref="IJobQueue"/>.</summary>
public interface IJobQueueConsumer
{
    /// <summary>
    /// Receives the next message, invoking <paramref name="handler"/> before deleting it. Returns
    /// false when the queue was empty.
    /// </summary>
    Task<bool> TryDequeueAsync(Func<ScanJobMessage, CancellationToken, Task> handler, CancellationToken ct);
}

/// <summary>Azure Storage Queue consumer.</summary>
public sealed class StorageQueueConsumer : IJobQueueConsumer
{
    private readonly QueueClient _queue;
    private readonly ILogger<StorageQueueConsumer> _log;

    public StorageQueueConsumer(QueueClient queue, ILogger<StorageQueueConsumer> log)
    {
        _queue = queue;
        _log = log;
    }

    public async Task<bool> TryDequeueAsync(Func<ScanJobMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        QueueMessage? message = await _queue.ReceiveMessageAsync(TimeSpan.FromMinutes(10), ct);
        if (message is null) return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(message.MessageText));
            var payload = JsonSerializer.Deserialize<ScanJobMessage>(json, ScanJson.Options)
                ?? throw new InvalidOperationException("Queue message did not deserialize to a scan job.");
            await handler(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The processor already records job-level failures; anything reaching here is a poison
            // message, so drop it rather than looping on it forever.
            _log.LogError(ex, "Discarding unprocessable queue message {MessageId}.", message.MessageId);
        }

        await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
        return true;
    }
}

/// <summary>Local development consumer reading the file queue written by the API.</summary>
public sealed class LocalDirectoryQueueConsumer : IJobQueueConsumer
{
    private readonly string _root;
    private readonly ILogger<LocalDirectoryQueueConsumer> _log;

    public LocalDirectoryQueueConsumer(string root, ILogger<LocalDirectoryQueueConsumer> log)
    {
        _root = Path.Combine(Path.GetFullPath(root), LocalDirectoryJobQueue.QueueFolder);
        Directory.CreateDirectory(_root);
        _log = log;
    }

    public async Task<bool> TryDequeueAsync(Func<ScanJobMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        var file = Directory.EnumerateFiles(_root, "*.json").Order().FirstOrDefault();
        if (file is null) return false;

        try
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var payload = JsonSerializer.Deserialize<ScanJobMessage>(json, ScanJson.Options)
                ?? throw new InvalidOperationException("Queue file did not deserialize to a scan job.");
            await handler(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Discarding unprocessable queue file {File}.", file);
        }

        try { File.Delete(file); } catch (IOException ex) { _log.LogWarning(ex, "Could not delete {File}.", file); }
        return true;
    }
}

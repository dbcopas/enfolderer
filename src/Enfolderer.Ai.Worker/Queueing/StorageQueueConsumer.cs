using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Queueing;

/// <summary>Azure Storage Queue consumer.</summary>
public sealed class StorageQueueConsumer : IJobQueueConsumer
{
    private readonly QueueClient _queue;
    private readonly TimeSpan _visibilityTimeout;
    private readonly ILogger<StorageQueueConsumer> _log;

    public StorageQueueConsumer(QueueClient queue, TimeSpan visibilityTimeout, ILogger<StorageQueueConsumer> log)
    {
        _queue = queue;
        // Azure Storage Queues cap the visibility timeout at seven days.
        _visibilityTimeout = visibilityTimeout < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : visibilityTimeout > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : visibilityTimeout;
        _log = log;
    }

    public async Task<bool> TryDequeueAsync(Func<ScanJobMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        QueueMessage? message = await _queue.ReceiveMessageAsync(_visibilityTimeout, ct);
        if (message is null) return false;

        // The lease is renewed while the handler runs, so a long job (many cards, one agent run
        // each) never becomes visible again and gets picked up by a second worker.
        using var renewal = new CancellationTokenSource();
        var lease = new MessageLease(message.PopReceipt);
        var renewalLoop = RenewLeaseAsync(message, lease, renewal.Token);

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
        finally
        {
            renewal.Cancel();
            try { await renewalLoop; } catch (OperationCanceledException) { }
        }

        await _queue.DeleteMessageAsync(message.MessageId, lease.PopReceipt, ct);
        return true;
    }

    /// <summary>Latest pop receipt for an in-flight message; each dequeue gets its own.</summary>
    private sealed class MessageLease(string popReceipt)
    {
        private volatile string _popReceipt = popReceipt;

        /// <summary>Written by the renewal task and read by the delete path.</summary>
        public string PopReceipt
        {
            get => _popReceipt;
            set => _popReceipt = value;
        }
    }

    /// <summary>Extends the message lease until the handler finishes.</summary>
    private async Task RenewLeaseAsync(QueueMessage message, MessageLease lease, CancellationToken ct)
    {
        var interval = _visibilityTimeout / 2;
        try
        {
            while (true)
            {
                await Task.Delay(interval, ct);
                var updated = await _queue.UpdateMessageAsync(
                    message.MessageId, lease.PopReceipt, visibilityTimeout: _visibilityTimeout, cancellationToken: ct);
                lease.PopReceipt = updated.Value.PopReceipt;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not extend the lease on queue message {MessageId}.", message.MessageId);
        }
    }
}

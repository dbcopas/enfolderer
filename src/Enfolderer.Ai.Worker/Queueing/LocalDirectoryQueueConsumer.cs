using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Queueing;

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

using System.Collections.Concurrent;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// In-memory job store used for local development and for the desktop end-to-end loop before any
/// Azure resources exist. Selected when no Cosmos endpoint is configured.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, ScanJobDocument> _jobs = new(StringComparer.Ordinal);

    public Task<ScanJobDocument> CreateAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        if (!_jobs.TryAdd(job.Id, job))
            throw new InvalidOperationException($"Job '{job.Id}' already exists.");
        return Task.FromResult(job);
    }

    public Task<ScanJobDocument?> GetAsync(string jobId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);

    public Task<ScanJobDocument> UpsertAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        var stored = job with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[stored.Id] = stored;
        return Task.FromResult(stored);
    }
}

using System.Collections.Concurrent;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// In-memory job store used for local development and for the desktop end-to-end loop before any
/// Azure resources exist. Selected when no Cosmos endpoint is configured.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    public Task MarkUploadedAsync(string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        while (_jobs.TryGetValue(jobId, out var job) && job.Status == ScanJobStatus.Pending)
        {
            if (_jobs.TryUpdate(jobId, job with { Status = ScanJobStatus.Uploaded, UpdatedAt = DateTimeOffset.UtcNow }, job))
                break;
        }
        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, ScanJobDocument> _jobs = new(StringComparer.Ordinal);

    public Task<ScanJobDocument> CreateAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        if (!_jobs.TryAdd(job.Id, job))
            throw new InvalidOperationException($"Job '{job.Id}' already exists.");
        return Task.FromResult(job);
    }

    public Task<ScanJobDocument?> GetAsync(string jobId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);

    public Task<IReadOnlyList<ScanJobDocument>> FindRecentAsync(string tenantId, IReadOnlyList<string> owners,
        DateTimeOffset since, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScanJobDocument>>(_jobs.Values
            .Where(j => j.OwnerTenantId == tenantId && owners.Contains(j.OwnerObjectId)
                && j.CreatedAt >= since && j.Status != ScanJobStatus.Pending)
            .OrderByDescending(j => j.CreatedAt).Take(limit).ToArray());

    public Task<ScanJobDocument> UpsertAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        var stored = job with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[stored.Id] = stored;
        return Task.FromResult(stored);
    }
}

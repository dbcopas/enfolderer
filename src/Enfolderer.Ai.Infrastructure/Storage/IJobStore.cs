using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Persistence for scan job state. The API identity is the only identity with read/write access —
/// neither Foundry project can reach the job store directly.
/// </summary>
public interface IJobStore
{
    Task<ScanJobDocument> CreateAsync(ScanJobDocument job, CancellationToken ct = default);

    Task<ScanJobDocument?> GetAsync(string jobId, CancellationToken ct = default);

    /// <summary>Creates or replaces the job document. Returns the stored document.</summary>
    Task<ScanJobDocument> UpsertAsync(ScanJobDocument job, CancellationToken ct = default);
}

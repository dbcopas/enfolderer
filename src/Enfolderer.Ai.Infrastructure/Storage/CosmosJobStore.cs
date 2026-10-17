using System.Net;
using Enfolderer.Ai.Contracts;
using Microsoft.Azure.Cosmos;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Cosmos DB backed job store. Container <c>jobs</c>, partition key <c>/jobId</c>.
/// Authenticated with the API's managed identity — no keys.
/// </summary>
public sealed class CosmosJobStore : IJobStore
{
    private readonly Container _container;

    public CosmosJobStore(Container container) => _container = container;

    public async Task<ScanJobDocument> CreateAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(job, new PartitionKey(job.JobId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<ScanJobDocument?> GetAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<ScanJobDocument>(jobId, new PartitionKey(jobId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ScanJobDocument> UpsertAsync(ScanJobDocument job, CancellationToken ct = default)
    {
        var updated = job with { UpdatedAt = DateTimeOffset.UtcNow };
        var response = await _container.UpsertItemAsync(updated, new PartitionKey(updated.JobId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<IReadOnlyList<ScanJobDocument>> FindRecentAsync(string tenantId, IReadOnlyList<string> owners,
        DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var query = new QueryDefinition("""
            SELECT TOP @limit * FROM c
            WHERE c.ownerTenantId = @tenant AND ARRAY_CONTAINS(@owners, c.ownerObjectId)
                AND c.createdAt >= @since AND c.status != 'Pending'
            ORDER BY c.createdAt DESC
            """).WithParameter("@limit", limit).WithParameter("@tenant", tenantId)
            .WithParameter("@owners", owners).WithParameter("@since", since.ToString("O"));
        using var iterator = _container.GetItemQueryIterator<ScanJobDocument>(query);
        var jobs = new List<ScanJobDocument>();
        while (iterator.HasMoreResults)
            jobs.AddRange(await iterator.ReadNextAsync(ct));
        return jobs;
    }
}

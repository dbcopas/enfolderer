using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

public interface IScannedCollectionStore
{
    Task<bool> AddAsync(string tenantId, string objectId, ScannedCollectionAddition addition, CancellationToken ct = default);
    Task<bool> ContainsAsync(string tenantId, string objectId, string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<ScannedCollectionPrinting>> ReadAsync(string tenantId, string objectId, CancellationToken ct = default);
}

public sealed class BlobScannedCollectionStore(BlobServiceClient blobs) : IScannedCollectionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Prefix(string tenantId, string objectId) =>
        $"{Guid.Parse(tenantId):D}/{Guid.Parse(objectId):D}/scanned/";

    public static string BlobName(string tenantId, string objectId, string jobId) =>
        Prefix(tenantId, objectId) + $"{Guid.ParseExact(jobId, "N"):N}.json";

    public async Task<bool> AddAsync(string tenantId, string objectId, ScannedCollectionAddition addition, CancellationToken ct = default)
    {
        addition.Validate();
        var blob = blobs.GetBlobContainerClient("collections").GetBlobClient(BlobName(tenantId, objectId, addition.JobId));
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(addition, Json));
        try
        {
            // An immutable scan receipt makes retries and simultaneous additions safe across API replicas.
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet")
        {
            return false;
        }
    }

    public async Task<bool> ContainsAsync(string tenantId, string objectId, string jobId, CancellationToken ct = default) =>
        await blobs.GetBlobContainerClient("collections").GetBlobClient(BlobName(tenantId, objectId, jobId)).ExistsAsync(ct);

    public async Task<IReadOnlyList<ScannedCollectionPrinting>> ReadAsync(string tenantId, string objectId, CancellationToken ct = default)
    {
        var container = blobs.GetBlobContainerClient("collections");
        var printings = new List<ScannedCollectionPrinting>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None,
            Prefix(tenantId, objectId), ct))
        {
            using var stream = await container.GetBlobClient(blob.Name).OpenReadAsync(cancellationToken: ct);
            var addition = await JsonSerializer.DeserializeAsync<ScannedCollectionAddition>(stream, Json, ct)
                ?? throw new InvalidDataException("Stored scanned collection addition is empty.");
            addition.Validate();
            printings.AddRange(addition.Printings);
        }
        return ScannedCollectionAddition.Combine(printings);
    }
}

public sealed class InMemoryScannedCollectionStore : IScannedCollectionStore
{
    private readonly ConcurrentDictionary<string, ScannedCollectionAddition> _additions = new();

    public Task<bool> AddAsync(string tenantId, string objectId, ScannedCollectionAddition addition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        addition.Validate();
        return Task.FromResult(_additions.TryAdd(BlobScannedCollectionStore.BlobName(tenantId, objectId, addition.JobId), addition));
    }

    public Task<bool> ContainsAsync(string tenantId, string objectId, string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_additions.ContainsKey(BlobScannedCollectionStore.BlobName(tenantId, objectId, jobId)));
    }

    public Task<IReadOnlyList<ScannedCollectionPrinting>> ReadAsync(string tenantId, string objectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var prefix = BlobScannedCollectionStore.Prefix(tenantId, objectId);
        return Task.FromResult(ScannedCollectionAddition.Combine(
            _additions.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(p => p.Value.Printings)));
    }
}

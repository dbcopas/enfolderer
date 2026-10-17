using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

public interface ICollectionSnapshotStore
{
    Task<CollectionSnapshot?> ReadAsync(string tenantId, string objectId, CancellationToken ct = default);
    Task WriteAsync(string tenantId, string objectId, CollectionSnapshot snapshot, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string objectId, CancellationToken ct = default);
}

public sealed class BlobCollectionSnapshotStore(BlobServiceClient blobs) : ICollectionSnapshotStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string BlobName(string tenantId, string objectId) =>
        $"{Guid.Parse(tenantId):D}/{Guid.Parse(objectId):D}/collection.json";

    private BlobClient Blob(string tenantId, string objectId) =>
        blobs.GetBlobContainerClient("collections").GetBlobClient(BlobName(tenantId, objectId));

    public async Task<CollectionSnapshot?> ReadAsync(string tenantId, string objectId, CancellationToken ct = default)
    {
        try
        {
            using var stream = await Blob(tenantId, objectId).OpenReadAsync(cancellationToken: ct);
            var snapshot = await JsonSerializer.DeserializeAsync<CollectionSnapshot>(stream, Json, ct)
                ?? throw new InvalidDataException("Stored collection snapshot is empty.");
            snapshot.Validate();
            return snapshot;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task WriteAsync(string tenantId, string objectId, CollectionSnapshot snapshot, CancellationToken ct = default)
    {
        snapshot.Validate();
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
        await Blob(tenantId, objectId).UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        }, ct);
    }

    public async Task DeleteAsync(string tenantId, string objectId, CancellationToken ct = default) =>
        await Blob(tenantId, objectId).DeleteIfExistsAsync(cancellationToken: ct);
}

public sealed class InMemoryCollectionSnapshotStore : ICollectionSnapshotStore
{
    private readonly ConcurrentDictionary<string, CollectionSnapshot> _snapshots = new();
    public Task<CollectionSnapshot?> ReadAsync(string tenantId, string objectId, CancellationToken ct = default) =>
        Task.FromResult(_snapshots.GetValueOrDefault(BlobCollectionSnapshotStore.BlobName(tenantId, objectId)));
    public Task WriteAsync(string tenantId, string objectId, CollectionSnapshot snapshot, CancellationToken ct = default)
    {
        snapshot.Validate();
        _snapshots[BlobCollectionSnapshotStore.BlobName(tenantId, objectId)] = snapshot;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string tenantId, string objectId, CancellationToken ct = default)
    {
        _snapshots.TryRemove(BlobCollectionSnapshotStore.BlobName(tenantId, objectId), out _);
        return Task.CompletedTask;
    }
}

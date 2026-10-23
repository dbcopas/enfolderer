using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Enfolderer.Ai.Infrastructure.Storage;

public sealed record AccessRequest(string Id, string Name, string Email, string Note, DateTimeOffset CreatedAt)
{
    public static string EmailId(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();
}

public interface IAccessRequestStore
{
    Task AddAsync(AccessRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AccessRequest>> ReadAsync(CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class BlobAccessRequestStore(BlobServiceClient blobs) : IAccessRequestStore
{
    public const string Prefix = "_access-requests/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private BlobContainerClient Container => blobs.GetBlobContainerClient("collections");
    private static string BlobName(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit))
            throw new ArgumentException("An access request ID must be a SHA-256 hash.", nameof(id));
        return Prefix + id + ".json";
    }

    public async Task AddAsync(AccessRequest request, CancellationToken ct = default)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, Json));
        try
        {
            await Container.GetBlobClient(BlobName(request.Id)).UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet")
        {
            // Repeated requests cannot overwrite a pending request or reveal whether an email is already saved.
        }
    }

    public async Task<IReadOnlyList<AccessRequest>> ReadAsync(CancellationToken ct = default)
    {
        var requests = new List<AccessRequest>();
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, Prefix, ct))
        {
            using var stream = await Container.GetBlobClient(blob.Name).OpenReadAsync(cancellationToken: ct);
            requests.Add(await JsonSerializer.DeserializeAsync<AccessRequest>(stream, Json, ct)
                ?? throw new InvalidDataException("Stored access request is empty."));
        }
        return requests.OrderByDescending(r => r.CreatedAt).ToArray();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default) =>
        await Container.GetBlobClient(BlobName(id)).DeleteIfExistsAsync(cancellationToken: ct);
}

public sealed class InMemoryAccessRequestStore : IAccessRequestStore
{
    private readonly ConcurrentDictionary<string, AccessRequest> _requests = new();
    public Task AddAsync(AccessRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _requests.TryAdd(request.Id, request);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<AccessRequest>> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AccessRequest>>(_requests.Values.OrderByDescending(r => r.CreatedAt).ToArray());
    }
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _requests.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>Where the client should PUT the image, and for how long that grant is valid.</summary>
public sealed record UploadTarget(string UploadUrl, string BlobPath, DateTimeOffset ExpiresAt);

/// <summary>
/// Decides where the client should send the image for a single job, and for how long that offer
/// stands. The desktop client never sees a storage account key or a blob URL: storage is private,
/// so the only implementation points the client back at the API, which relays the bytes.
/// </summary>
public interface IUploadUrlIssuer
{
    Task<UploadTarget> IssueAsync(string jobId, string fileName, CancellationToken ct = default);
}

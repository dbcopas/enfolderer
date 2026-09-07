namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>Where the client should PUT the image, and for how long that grant is valid.</summary>
public sealed record UploadTarget(string UploadUrl, string BlobPath, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues a short-lived, write-only upload grant for a single blob. The desktop client never sees
/// a storage account key: the API holds only <c>Storage Blob Delegator</c> plus write access and
/// mints a user-delegation SAS scoped to one blob path.
/// </summary>
public interface IUploadUrlIssuer
{
    Task<UploadTarget> IssueAsync(string jobId, string fileName, CancellationToken ct = default);
}

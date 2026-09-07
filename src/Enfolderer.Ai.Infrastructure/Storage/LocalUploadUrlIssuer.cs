using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Development fallback: instead of a blob SAS the client is told to PUT the image back to the API
/// itself, which stores it in the shared local directory. Never selected when a storage account is
/// configured.
/// </summary>
public sealed class LocalUploadUrlIssuer : IUploadUrlIssuer
{
    private readonly string _apiBaseUrl;
    private readonly TimeSpan _lifetime;

    public LocalUploadUrlIssuer(string apiBaseUrl, TimeSpan lifetime)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromMinutes(15);
    }

    public Task<UploadTarget> IssueAsync(string jobId, string fileName, CancellationToken ct = default)
    {
        var blobName = ScanBlobPaths.BuildScanBlobName(jobId, fileName);
        var target = new UploadTarget(
            $"{_apiBaseUrl}/jobs/{jobId}/content",
            $"{ScanBlobPaths.ScansContainer}/{blobName}",
            DateTimeOffset.UtcNow.Add(_lifetime));
        return Task.FromResult(target);
    }
}

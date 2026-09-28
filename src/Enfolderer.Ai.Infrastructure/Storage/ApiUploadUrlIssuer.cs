using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Tells the client to PUT the image back to the API, which writes it to the scans container on the
/// client's behalf.
/// <para>
/// This is the only upload path. The storage account has no public endpoint, so a blob SAS would be
/// useless to a client that is not inside the VNet — and the client is required to run anywhere.
/// Routing the bytes through the API keeps the public surface to one host.
/// </para>
/// <para>
/// The URL is relative so that it resolves against whatever base address the client already uses.
/// The API does not need to be told its own public hostname, which it cannot reliably discover from
/// behind App Service's front end anyway.
/// </para>
/// </summary>
public sealed class ApiUploadUrlIssuer : IUploadUrlIssuer
{
    private readonly TimeSpan _lifetime;

    public ApiUploadUrlIssuer(TimeSpan lifetime)
    {
        _lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromMinutes(15);
    }

    public Task<UploadTarget> IssueAsync(string jobId, string fileName, CancellationToken ct = default)
    {
        var blobName = ScanBlobPaths.BuildScanBlobName(jobId, fileName);
        var target = new UploadTarget(
            $"jobs/{jobId}/content",
            $"{ScanBlobPaths.ScansContainer}/{blobName}",
            DateTimeOffset.UtcNow.Add(_lifetime));
        return Task.FromResult(target);
    }
}

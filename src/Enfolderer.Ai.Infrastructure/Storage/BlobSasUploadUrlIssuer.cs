using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Mints a user-delegation SAS (managed identity, no account key) granting create+write on exactly
/// one blob for a few minutes. Read is deliberately excluded so the grant cannot be replayed to
/// enumerate or download other jobs' images.
/// </summary>
public sealed class BlobSasUploadUrlIssuer : IUploadUrlIssuer
{
    private readonly BlobServiceClient _blobService;
    private readonly TimeSpan _lifetime;

    public BlobSasUploadUrlIssuer(BlobServiceClient blobService, TimeSpan lifetime)
    {
        _blobService = blobService;
        _lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromMinutes(15);
    }

    public async Task<UploadTarget> IssueAsync(string jobId, string fileName, CancellationToken ct = default)
    {
        var blobName = ScanBlobPaths.BuildScanBlobName(jobId, fileName);
        var container = _blobService.GetBlobContainerClient(ScanBlobPaths.ScansContainer);
        var blob = container.GetBlobClient(blobName);

        // Allow a little clock skew on the start time; user-delegation keys are account-scoped but
        // the SAS itself is restricted to this single blob.
        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresOn = DateTimeOffset.UtcNow.Add(_lifetime);

        var delegationKey = await _blobService.GetUserDelegationKeyAsync(startsOn, expiresOn, ct);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = ScanBlobPaths.ScansContainer,
            BlobName = blobName,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            Protocol = SasProtocol.Https
        };
        builder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var sas = builder.ToSasQueryParameters(delegationKey.Value, _blobService.AccountName).ToString();
        var uploadUrl = $"{blob.Uri}?{sas}";

        return new UploadTarget(uploadUrl, $"{ScanBlobPaths.ScansContainer}/{blobName}", expiresOn);
    }
}

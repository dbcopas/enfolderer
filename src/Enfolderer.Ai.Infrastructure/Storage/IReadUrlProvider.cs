using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Produces short-lived read URLs for blobs. Agents are handed one of these instead of any storage
/// credential, so an agent can only ever see the single image it was asked about.
/// </summary>
public interface IReadUrlProvider
{
    Task<Uri> GetReadUrlAsync(string blobPath, TimeSpan lifetime, CancellationToken ct = default);
}

/// <summary>User-delegation read SAS scoped to one blob.</summary>
public sealed class BlobReadUrlProvider : IReadUrlProvider
{
    private readonly BlobServiceClient _blobService;

    public BlobReadUrlProvider(BlobServiceClient blobService) => _blobService = blobService;

    public async Task<Uri> GetReadUrlAsync(string blobPath, TimeSpan lifetime, CancellationToken ct = default)
    {
        var separator = blobPath.IndexOf('/');
        if (separator <= 0 || separator == blobPath.Length - 1)
            throw new ArgumentException($"Blob path must be 'container/name', got '{blobPath}'.", nameof(blobPath));

        var containerName = blobPath[..separator];
        var blobName = blobPath[(separator + 1)..];
        var blob = _blobService.GetBlobContainerClient(containerName).GetBlobClient(blobName);

        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromMinutes(30));

        var delegationKey = await _blobService.GetUserDelegationKeyAsync(startsOn, expiresOn, ct);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            Protocol = SasProtocol.Https
        };
        builder.SetPermissions(BlobSasPermissions.Read);

        var sas = builder.ToSasQueryParameters(delegationKey.Value, _blobService.AccountName).ToString();
        return new Uri($"{blob.Uri}?{sas}");
    }
}

/// <summary>
/// Development fallback returning a <c>file://</c> URL into the local image root. Real Foundry
/// agents cannot fetch these, so it is only useful together with the offline stub agents.
/// </summary>
public sealed class LocalReadUrlProvider : IReadUrlProvider
{
    private readonly string _root;

    public LocalReadUrlProvider(string root) => _root = Path.GetFullPath(root);

    public Task<Uri> GetReadUrlAsync(string blobPath, TimeSpan lifetime, CancellationToken ct = default)
    {
        var segments = blobPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new ArgumentException($"Invalid blob path '{blobPath}'.", nameof(blobPath));
        }
        return Task.FromResult(new Uri(Path.Combine(_root, Path.Combine(segments))));
    }
}

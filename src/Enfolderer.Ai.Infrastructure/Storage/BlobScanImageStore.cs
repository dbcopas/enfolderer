using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>Azure Blob Storage implementation of <see cref="IScanImageStore"/>.</summary>
public sealed class BlobScanImageStore : IScanImageStore
{
    private readonly BlobServiceClient _blobService;

    public BlobScanImageStore(BlobServiceClient blobService) => _blobService = blobService;

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = Resolve(blobPath);
        return await blob.OpenReadAsync(cancellationToken: ct);
    }

    public async Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        var blob = Resolve(blobPath);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            ct);
    }

    public async Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default) =>
        await Resolve(blobPath).ExistsAsync(ct);

    private BlobClient Resolve(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            throw new ArgumentException("Blob path is required.", nameof(blobPath));

        var separator = blobPath.IndexOf('/');
        if (separator <= 0 || separator == blobPath.Length - 1)
            throw new ArgumentException($"Blob path must be 'container/name', got '{blobPath}'.", nameof(blobPath));

        var container = blobPath[..separator];
        var name = blobPath[(separator + 1)..];
        return _blobService.GetBlobContainerClient(container).GetBlobClient(name);
    }
}

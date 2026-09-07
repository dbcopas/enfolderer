using System.ComponentModel;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure.Storage;
using ModelContextProtocol.Server;

namespace Enfolderer.Ai.Mcp.Imaging;

/// <summary>
/// Imaging tools owned by Team A (they pair with the geometry skill): perspective-correct cropping
/// and read-URL minting. Storage access is limited by the RBAC of whatever identity runs this
/// server — in the demo that identity can read <c>scans</c> and write <c>crops</c>, and has no
/// Cosmos access at all.
/// </summary>
[McpServerToolType]
public sealed class ImagingTools
{
    private readonly IScanImageStore _images;
    private readonly IReadUrlProvider _readUrls;

    public ImagingTools(IScanImageStore images, IReadUrlProvider readUrls)
    {
        _images = images;
        _readUrls = readUrls;
    }

    [McpServerTool(Name = "crop_quad")]
    [Description("Perspective-correct crop of a quadrilateral out of a stored image, written as a PNG blob. Use for cards photographed at an angle.")]
    public async Task<string> CropQuadAsync(
        [Description("Source blob path as 'container/name', e.g. 'scans/<jobId>/page1.jpg'.")] string sourceBlobPath,
        [Description("Destination blob path as 'container/name', e.g. 'crops/<jobId>/card-000.png'.")] string destinationBlobPath,
        [Description("The four corners as JSON: [{\"x\":0,\"y\":0},...] ordered top-left, top-right, bottom-right, bottom-left.")] string quadPointsJson,
        [Description("Height in pixels of the produced crop.")] int outputHeight = PerspectiveCropper.DefaultOutputHeight,
        CancellationToken ct = default)
    {
        var quad = ParseQuad(quadPointsJson);

        await using var source = await _images.OpenReadAsync(sourceBlobPath, ct);
        using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, ct);
        buffered.Position = 0;

        using var crop = new MemoryStream();
        var size = PerspectiveCropper.CropToPng(buffered, quad, crop, outputHeight);
        crop.Position = 0;
        await _images.WriteAsync(destinationBlobPath, crop, "image/png", ct);

        return JsonSerializer.Serialize(new
        {
            blobPath = destinationBlobPath,
            width = size.Width,
            height = size.Height
        });
    }

    [McpServerTool(Name = "get_image_sas")]
    [Description("Mint a short-lived read-only URL for a stored image so a vision model can fetch it without any storage credential.")]
    public async Task<string> GetImageSasAsync(
        [Description("Blob path as 'container/name'.")] string blobPath,
        [Description("Lifetime of the URL in minutes (1-120).")] int lifetimeMinutes = 30,
        CancellationToken ct = default)
    {
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(lifetimeMinutes, 1, 120));
        var url = await _readUrls.GetReadUrlAsync(blobPath, lifetime, ct);
        return JsonSerializer.Serialize(new
        {
            url = url.ToString(),
            expiresAt = DateTimeOffset.UtcNow.Add(lifetime)
        });
    }

    /// <summary>Parses the tool's quad argument. Internal so the contract can be tested.</summary>
    internal static CardQuad ParseQuad(string quadPointsJson)
    {
        using var document = JsonDocument.Parse(quadPointsJson);
        var root = document.RootElement;

        // Accept both a bare array and {"points":[...]} so callers can pass a CardQuad directly.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("points", out var wrapped))
            root = wrapped;

        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Quad points must be a JSON array.", nameof(quadPointsJson));

        var points = new List<ImagePoint>(CardQuad.RequiredPointCount);
        foreach (var element in root.EnumerateArray())
        {
            if (!element.TryGetProperty("x", out var x) || !element.TryGetProperty("y", out var y) ||
                !x.TryGetDouble(out var xv) || !y.TryGetDouble(out var yv))
                throw new ArgumentException("Each quad point must have numeric 'x' and 'y'.", nameof(quadPointsJson));
            points.Add(new ImagePoint(xv, yv));
        }

        if (points.Count != CardQuad.RequiredPointCount)
            throw new ArgumentException($"A quad must have exactly {CardQuad.RequiredPointCount} points.", nameof(quadPointsJson));

        return new CardQuad(points);
    }
}

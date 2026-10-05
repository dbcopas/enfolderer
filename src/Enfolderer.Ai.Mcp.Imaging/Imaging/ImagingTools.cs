using System.ComponentModel;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure.Storage;
using ModelContextProtocol.Server;

namespace Enfolderer.Ai.Mcp.Imaging;

/// <summary>
/// Imaging tools owned by Team A (they pair with the geometry skill). Storage access is limited by
/// the RBAC of whatever identity runs this server — in the demo that identity can read
/// <c>scans</c>, and has no Cosmos access at all.
/// <para>
/// <c>detect_cards</c> is the capability the boundary agent exists to sell: locating cards is
/// measurement, which a chat model cannot do, so the agent calls out to this code and spends its
/// own judgement on which results are really cards. Team B consumes it through the agent and never
/// sees this server, its identity, or its resource group.
/// </para>
/// <para>
/// There is deliberately no tool for minting a read URL. The storage account is private, so a URL
/// is not something a Foundry-hosted model can fetch; images reach an agent as uploaded file
/// content instead.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ImagingTools
{
    private readonly IScanImageStore _images;

    public ImagingTools(IScanImageStore images) => _images = images;

    [McpServerTool(Name = "detect_cards")]
    [Description("Locates every trading card in a stored photograph and returns their corners. Measured by edge detection, not estimated, so it is reliable for cards at an oblique angle or rotated out of alignment.")]
    public async Task<string> DetectCardsAsync(
        [Description("Source blob path as 'container/name', e.g. 'scans/<jobId>/page1.jpg'.")] string sourceBlobPath,
        [Description("Pockets down a binder page, when the photo is of one. 3 suits a standard nine-pocket page. Pass 0 for loose cards on a table.")] int pageRows = 3,
        [Description("Pockets across a binder page. 3 suits a standard nine-pocket page. Pass 0 for loose cards on a table.")] int pageColumns = 3,
        CancellationToken ct = default)
    {
        await using var source = await _images.OpenReadAsync(sourceBlobPath, ct);
        using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, ct);
        buffered.Position = 0;

        var size = PerspectiveCropper.ReadDimensions(buffered);
        buffered.Position = 0;

        // A page whose grid does not fit the photo falls through to the loose-card pipeline on its
        // own, so asking for one costs nothing when the guess is wrong.
        var options = new CardDetectorOptions
        {
            Page = pageRows > 0 && pageColumns > 0
                ? new PageGridOptions { Rows = pageRows, Columns = pageColumns }
                : null
        };

        var detected = CardDetector.Detect(buffered, options);

        return JsonSerializer.Serialize(new
        {
            imageWidth = size.Width,
            imageHeight = size.Height,
            // Fractions of the image, matching what the agent is asked to return, so a corner can
            // be passed straight through without the model doing arithmetic on it.
            cards = detected.Select(card => new
            {
                confidence = card.Confidence,
                points = card.Quad.Points.Select(p => new
                {
                    x = Math.Round(p.X / Math.Max(1, size.Width), 4),
                    y = Math.Round(p.Y / Math.Max(1, size.Height), 4)
                })
            })
        });
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

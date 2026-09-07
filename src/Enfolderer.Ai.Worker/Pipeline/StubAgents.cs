using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Pipeline;

/// <summary>
/// Offline stand-in for Team A's boundary agent. It reports a single quad covering the middle of
/// the image so the whole upload → poll → result loop can be demonstrated with no Azure resources.
/// Selected only when no geometry project endpoint is configured.
/// </summary>
public sealed class StubCardBoundaryAgent : ICardBoundaryAgent
{
    private readonly IScanImageStore _images;
    private readonly ILogger<StubCardBoundaryAgent> _log;

    public StubCardBoundaryAgent(IScanImageStore images, ILogger<StubCardBoundaryAgent> log)
    {
        _images = images;
        _log = log;
    }

    public string AgentId => "stub/CardBoundaryAgent";

    public async Task<IReadOnlyList<DetectedBoundary>> DetectAsync(Uri imageSasUrl, CancellationToken ct = default)
    {
        _log.LogWarning("Using the stub boundary agent; configure ScanPipeline:GeometryProjectEndpoint for real detection.");

        var dimensions = await TryReadDimensionsAsync(imageSasUrl, ct) ?? new ImageDimensions(1000, 1400);

        // Inset by 10% so the quad is obviously a placeholder rather than the whole frame.
        double insetX = dimensions.Width * 0.1, insetY = dimensions.Height * 0.1;
        double right = dimensions.Width - insetX, bottom = dimensions.Height - insetY;

        var quad = new CardQuad([
            new ImagePoint(insetX, insetY),
            new ImagePoint(right, insetY),
            new ImagePoint(right, bottom),
            new ImagePoint(insetX, bottom)
        ]);

        return [new DetectedBoundary(quad, 0.1, CardGames.Unknown)];
    }

    private async Task<ImageDimensions?> TryReadDimensionsAsync(Uri imageUrl, CancellationToken ct)
    {
        try
        {
            // The local development read-url provider hands back a file:// URL under the image root.
            if (!imageUrl.IsFile) return null;
            await using var stream = File.OpenRead(imageUrl.LocalPath);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return PerspectiveCropper.ReadDimensions(buffer);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stub boundary agent could not read image dimensions from {Url}.", imageUrl);
            return null;
        }
    }
}

/// <summary>
/// Offline stand-in for a Team B identification agent: it returns a per-card error rather than
/// inventing card data, so a stubbed run is never mistaken for a real identification.
/// </summary>
public sealed class StubCardIdentificationAgent : ICardIdentificationAgent
{
    public StubCardIdentificationAgent(string game) => Game = game;

    public string Game { get; }

    public string AgentId => $"stub/{Game}CardIdAgent";

    public Task<IdentifiedCard> IdentifyAsync(CardCrop crop, CancellationToken ct = default) =>
        Task.FromResult(new IdentifiedCard
        {
            Index = crop.Index,
            Quad = crop.Quad,
            Game = Game,
            Agent = AgentId,
            Error = "No Foundry identification project is configured; configure ScanPipeline:IdentificationProjectEndpoint."
        });
}

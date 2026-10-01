using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Pipeline;

/// <summary>
/// Offline stand-in for Team A's boundary agent. It runs the same <see cref="CardDetector"/> that
/// sits behind Team A's <c>detect_cards</c> MCP tool, so the whole upload → poll → result loop can
/// be demonstrated — with real geometry — and no Azure resources at all. What it cannot stand in
/// for is the agent's judgement over those results. Selected only when no geometry project endpoint
/// is configured.
/// </summary>
public sealed class StubCardBoundaryAgent : ICardBoundaryAgent
{
    private readonly ILogger<StubCardBoundaryAgent> _log;

    public StubCardBoundaryAgent(ILogger<StubCardBoundaryAgent> log) => _log = log;

    public string AgentId => "stub/CardBoundaryAgent";

    public Task<IReadOnlyList<DetectedBoundary>> DetectAsync(
        AgentImage image, string scanBlobPath, int width, int height, CancellationToken ct = default)
    {
        _log.LogWarning("Using the stub boundary agent; configure ScanPipeline:GeometryProjectEndpoint for real detection.");

        // The caller has already decoded the image, so its size is preferred; the fallback keeps
        // the stub usable from a test that does not have real bytes to hand.
        var dimensions = width > 0 && height > 0
            ? new ImageDimensions(width, height)
            : TryReadDimensions(image) ?? new ImageDimensions(1000, 1400);

        var detected = TryDetect(image);
        if (detected.Count > 0)
        {
            _log.LogInformation("Stub boundary agent measured {Count} card(s) locally.", detected.Count);
            return Task.FromResult<IReadOnlyList<DetectedBoundary>>(
                detected.Select(d => new DetectedBoundary(d.Quad, d.Confidence, CardGames.Unknown)).ToList());
        }

        // Inset by 10% so the quad is obviously a placeholder rather than the whole frame.
        double insetX = dimensions.Width * 0.1, insetY = dimensions.Height * 0.1;
        double right = dimensions.Width - insetX, bottom = dimensions.Height - insetY;

        var quad = new CardQuad([
            new ImagePoint(insetX, insetY),
            new ImagePoint(right, insetY),
            new ImagePoint(right, bottom),
            new ImagePoint(insetX, bottom)
        ]);

        return Task.FromResult<IReadOnlyList<DetectedBoundary>>([new DetectedBoundary(quad, 0.1, CardGames.Unknown)]);
    }

    private IReadOnlyList<DetectedCard> TryDetect(AgentImage image)
    {
        try
        {
            using var buffer = new MemoryStream(image.Content.ToArray(), writable: false);
            return CardDetector.Detect(buffer);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local card detection failed; falling back to a placeholder quad.");
            return [];
        }
    }

    private ImageDimensions? TryReadDimensions(AgentImage image)
    {
        try
        {
            using var buffer = new MemoryStream(image.Content.ToArray(), writable: false);
            return PerspectiveCropper.ReadDimensions(buffer);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stub boundary agent could not read the dimensions of {FileName}.", image.FileName);
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

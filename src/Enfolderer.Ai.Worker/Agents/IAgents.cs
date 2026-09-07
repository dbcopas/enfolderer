using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>Result of a single boundary detection.</summary>
public sealed record DetectedBoundary(CardQuad Quad, double Confidence, string? GameHint);

/// <summary>
/// Team A's <c>CardBoundaryAgent</c> (Foundry project <c>cardgeo</c>).
/// It is the only component allowed to reason about card geometry, and it has no access to any
/// card catalogue or to the job store.
/// </summary>
public interface ICardBoundaryAgent
{
    /// <summary>Agent identifier recorded on each result row, e.g. <c>cardgeo/CardBoundaryAgent</c>.</summary>
    string AgentId { get; }

    /// <summary>
    /// Locates every collectible card in the image. The image is passed as a read SAS URL so the
    /// agent never receives storage credentials.
    /// </summary>
    Task<IReadOnlyList<DetectedBoundary>> DetectAsync(Uri imageSasUrl, CancellationToken ct = default);
}

/// <summary>A crop handed to an identification agent.</summary>
public sealed record CardCrop(int Index, Uri CropSasUrl, CardQuad Quad, string? GameHint);

/// <summary>
/// Team B's per-game identification agents (Foundry project <c>cardid</c>). Each implementation owns
/// its own catalogue MCP server and its own game-specific prompt knowledge.
/// </summary>
public interface ICardIdentificationAgent
{
    /// <summary>Normalized game this agent handles; see <see cref="CardGames"/>.</summary>
    string Game { get; }

    /// <summary>Agent identifier recorded on each result row, e.g. <c>cardid/MtgCardIdAgent</c>.</summary>
    string AgentId { get; }

    Task<IdentifiedCard> IdentifyAsync(CardCrop crop, CancellationToken ct = default);
}

using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Team A's boundary agent, reached through the <c>cardgeo</c> project endpoint.
/// The prompt is the agent's *skill contract*: it must find any collectible card — including
/// borderless, full-art, textured and other modern treatments where there is no printed border —
/// at any rotation and under perspective distortion.
/// </summary>
public sealed class FoundryCardBoundaryAgent : ICardBoundaryAgent
{
    internal const string Prompt = """
        Locate every collectible trading card visible in this photo.

        Cards may be at an oblique angle, rotated arbitrarily, partially overlapping, and misaligned
        with each other. Many modern printings are borderless, full-art, textured, foil-etched or
        have irregular frames, so do not rely on a printed border: use the physical card edges,
        including the rounded corners and the contrast against the surface behind them.

        Return ONLY a JSON object of the form:
        {"cards":[{"quad":{"points":[{"x":0,"y":0},{"x":0,"y":0},{"x":0,"y":0},{"x":0,"y":0}]},
                   "confidence":0.0,"gameHint":"mtg|pokemon|yugioh|lorcana|unknown"}]}

        Rules:
        - points are pixel coordinates in the source image.
        - Order the four points as the card's own top-left, top-right, bottom-right, bottom-left,
          so a card photographed upside down still reports its printed top-left first.
        - gameHint is your best guess of the game from the card back/frame; use "unknown" if unsure.
        - Emit no prose, no markdown fences, and no cards you are not confident are cards.
        """;

    private readonly FoundryAgentClient _client;
    private readonly string _agentId;
    private readonly ILogger<FoundryCardBoundaryAgent> _log;

    public FoundryCardBoundaryAgent(FoundryAgentClient client, string agentId, ILogger<FoundryCardBoundaryAgent> log)
    {
        _client = client;
        _agentId = agentId;
        _log = log;
    }

    public string AgentId => $"cardgeo/{_agentId}";

    public async Task<IReadOnlyList<DetectedBoundary>> DetectAsync(Uri imageSasUrl, CancellationToken ct = default)
    {
        var reply = await _client.RunAsync(_agentId, Prompt, imageSasUrl, ct);
        var boundaries = ParseBoundaries(reply);
        _log.LogInformation("Boundary agent {AgentId} returned {Count} card(s).", AgentId, boundaries.Count);
        return boundaries;
    }

    /// <summary>Parses the boundary agent reply. Internal so the contract can be self-tested.</summary>
    internal static IReadOnlyList<DetectedBoundary> ParseBoundaries(string reply)
    {
        using var doc = JsonDocument.Parse(AgentJson.ExtractJsonObject(reply));
        var results = new List<DetectedBoundary>();

        if (!doc.RootElement.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var card in cards.EnumerateArray())
        {
            if (!card.TryGetProperty("quad", out var quad) ||
                !quad.TryGetProperty("points", out var points) ||
                points.ValueKind != JsonValueKind.Array)
                continue;

            var parsed = new List<ImagePoint>(CardQuad.RequiredPointCount);
            foreach (var point in points.EnumerateArray())
            {
                if (point.TryGetProperty("x", out var x) && point.TryGetProperty("y", out var y) &&
                    x.TryGetDouble(out var xv) && y.TryGetDouble(out var yv))
                {
                    parsed.Add(new ImagePoint(xv, yv));
                }
            }

            if (parsed.Count != CardQuad.RequiredPointCount) continue;

            var confidence = card.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv) ? cv : 0d;
            var gameHint = card.TryGetProperty("gameHint", out var g) ? g.GetString() : null;

            results.Add(new DetectedBoundary(new CardQuad(parsed), Math.Clamp(confidence, 0d, 1d), CardGames.Normalize(gameHint)));
        }

        return results;
    }
}

using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Team A's boundary agent, reached through the <c>cardgeo</c> project endpoint.
/// The prompt is the agent's *skill contract*: it must find any collectible card — including
/// borderless, full-art, textured and other modern treatments where there is no printed border —
/// at any rotation and under perspective distortion. Shared local measurements are authoritative;
/// the model enriches their printed orientation and game, but cannot drop them.
/// </summary>
public sealed class FoundryCardBoundaryAgent : ICardBoundaryAgent
{
    /// <summary>
    /// The prompt, with the image's pixel size and storage path substituted in.
    /// <para>
    /// The agent is told to measure the corners with Team A's <c>detect_cards</c> tool rather than
    /// estimate them. Locating an object is the one thing a chat model genuinely cannot do: it has
    /// no detection head, and it never sees the photograph at its original resolution, so asked for
    /// coordinates it invents plausible ones. Edge detection measures them. What the model is good
    /// at — deciding which blobs are really cards, which way up they are, and which game they
    /// belong to — is what the rest of the prompt asks for.
    /// </para>
    /// <para>
    /// The scan's blob path is passed so the tool can open it. That is not a credential: the tool
    /// runs as Team A's own identity, which holds <c>Storage Blob Data Reader</c> on the
    /// <c>scans</c> container and nothing else, so naming a blob grants no access the team did not
    /// already have, and no access at all to <c>crops</c>, Cosmos or the queue.
    /// </para>
    /// <para>
    /// Corners are requested as <em>fractions of the image</em> rather than pixels, which is also
    /// the shape <c>detect_cards</c> returns, so a measured corner can be passed straight through
    /// without the model doing arithmetic on it. The caller, which decoded the image, scales them
    /// back.
    /// </para>
    /// </summary>
    internal static string BuildPrompt(int width, int height, string scanBlobPath) => $$"""
        Locate every collectible trading card visible in this photo.

        The photo is {{width}} pixels wide and {{height}} pixels high. The same photo is stored at
        the blob path {{scanBlobPath}}.

        FIRST, call the detect_cards tool with sourceBlobPath = {{scanBlobPath}}. It measures the
        card corners by edge detection and returns them as fractions of the image, already ordered
        and already filtered to card-shaped regions. Do not estimate coordinates yourself while that
        tool can measure them for you.

        THEN use the photo to judge what the tool could not:
        - Return every measured region exactly once. Do not drop regions or invent additional ones
          when the tool found regions. Identification will report uncertain or non-card crops.
        - Fix the rotation. A page photographed sideways puts every card on its side, and the tool
          reports those cards lying down. It cannot tell which end of a card is its top either,
          because that is printed on the face rather than being part of its shape; you can see it.
          Rotate the four points so the card's printed top-left comes first.
        - Set gameHint from the card frame or back.

        If the tool returns no cards, estimate every corner yourself in the same format.
        Do not conceal tool errors or substitute guessed corners for failed measurements.

        Cards may be at an oblique angle, rotated arbitrarily, partially overlapping, and misaligned
        with each other. Many modern printings are borderless, full-art, textured, foil-etched or
        have irregular frames, so do not rely on a printed border: use the physical card edges,
        including the rounded corners and the contrast against the surface behind them.

        Return ONLY a JSON object of the form:
        {"cards":[{"quad":{"points":[{"x":0.0,"y":0.0},{"x":0.0,"y":0.0},
                                     {"x":0.0,"y":0.0},{"x":0.0,"y":0.0}]},
                   "confidence":0.0,"gameHint":"mtg|pokemon|yugioh|lorcana|unknown"}]}

        Rules:
        - x and y are FRACTIONS of the image, between 0 and 1: x = 0 is the left edge, x = 1 the
          right edge, y = 0 the top edge, y = 1 the bottom edge. Do not return pixel values.
        - Give each corner to three decimal places.
        - Order the four points as the card's own top-left, top-right, bottom-right, bottom-left,
          so a card photographed upside down still reports its printed top-left first.
        - The four points must be the corners of one single card. Never return a box that contains
          several cards, and never return the whole image as one card.
        - gameHint is your best guess of the game from the card back/frame; use "unknown" if unsure.
        - Emit no prose or markdown fences. Preserve measured regions even when unsure of the game.
        """;

    /// <summary>The <c>server_label</c> Team A's imaging MCP server is attached under.</summary>
    private const string ImagingServerLabel = "imaging";

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

    public async Task<IReadOnlyList<DetectedBoundary>> DetectAsync(
        AgentImage image, string scanBlobPath, int width, int height, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var source = new MemoryStream(image.Content.ToArray(), writable: false);
        var measured = CardDetector.Detect(source, out var recoveryDiagnostic);
        if (recoveryDiagnostic is not null)
            _log.LogInformation("{GeometryRecovery}", recoveryDiagnostic);

        // "imaging" is the server_label card-boundary-agent.yaml gives Team A's own MCP server.
        // Foundry pauses the run at each tool call for approval, and the orchestrator approves only
        // this label — so an agent edited to reach for a catalogue server is refused here too.
        var reply = await _client.RunAsync(
            _agentId, BuildPrompt(width, height, scanBlobPath), WithoutDisplayRotation(image), ImagingServerLabel, ct,
            validateReply: text =>
            {
                var parsed = ParseBoundaries(text, width, height);
                if (parsed.Any(b =>
                {
                    var p = b.Quad.Points;
                    static double Length(ImagePoint a, ImagePoint b) =>
                        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    return Length(p[0], p[1]) > Length(p[1], p[2]) * 1.2;
                }))
                    _log.LogWarning("Boundary reply contains landscape quads; footer and art checks will inspect both quarter-turns.");
            });

        // Logged in full because a wrong quad is invisible downstream: the crop succeeds, the
        // identification agent sees a picture of a table, and nothing anywhere reports an error.
        _log.LogDebug("Boundary agent {AgentId} replied: {Reply}", AgentId, reply);

        var parsed = ParseBoundaries(reply, width, height);
        var boundaries = parsed.Where(b => IsPlausibleCard(b.Quad, width, height)).ToList();

        if (boundaries.Count != parsed.Count)
        {
            _log.LogWarning(
                "Boundary agent {AgentId} returned {Rejected} quad(s) that are not card-shaped or "
                + "lie outside the image; they were discarded. Reply: {Reply}",
                AgentId, parsed.Count - boundaries.Count, AgentJson.Summarize(reply, 1000));
        }

        _log.LogInformation("Boundary agent {AgentId} returned {Count} card(s).", AgentId, boundaries.Count);
        if (measured.Count == 0)
        {
            _log.LogWarning("No card regions were measured; using {Count} agent-estimated boundaries.", boundaries.Count);
            return boundaries;
        }

        var authoritative = PreserveMeasuredRegions(measured, boundaries);
        var unmatched = authoritative.Count(b => b.GameHint == CardGames.Unknown);
        _log.LogInformation("Measured geometry retained {Count} regions; agent returned {AgentCount}.",
            authoritative.Count, boundaries.Count);
        if (unmatched > 0 || boundaries.Count != measured.Count)
            _log.LogWarning("Measured regions are authoritative: {Count} retained, {AgentCount} agent boundaries; "
                + "{UnknownCount} regions have unknown game metadata. Unmatched agent geometry is not used.",
                measured.Count, boundaries.Count, unmatched);
        return authoritative;
    }

    internal static IReadOnlyList<DetectedBoundary> PreserveMeasuredRegions(
        IReadOnlyList<DetectedCard> measured, IReadOnlyList<DetectedBoundary> agentBoundaries)
    {
        var result = new List<DetectedBoundary>(measured.Count);
        foreach (var region in measured)
        {
            var matches = agentBoundaries.Where(b => Overlap(region.Quad, b.Quad) > 0.6).ToList();
            var match = matches.Count == 1
                && measured.Count(m => Overlap(m.Quad, matches[0].Quad) > 0.6) == 1
                    ? matches[0] : null;
            var quad = region.Quad;
            if (match is not null)
            {
                var offset = Enumerable.Range(0, CardQuad.RequiredPointCount).MinBy(rotation =>
                    Enumerable.Range(0, CardQuad.RequiredPointCount).Sum(i =>
                        Math.Pow(quad.Points[(i + rotation) % CardQuad.RequiredPointCount].X - match.Quad.Points[i].X, 2)
                        + Math.Pow(quad.Points[(i + rotation) % CardQuad.RequiredPointCount].Y - match.Quad.Points[i].Y, 2)));
                quad = new CardQuad(Enumerable.Range(0, CardQuad.RequiredPointCount)
                    .Select(i => region.Quad.Points[(i + offset) % CardQuad.RequiredPointCount]).ToList());
            }

            result.Add(new DetectedBoundary(quad, region.Confidence, match?.GameHint ?? CardGames.Unknown));
        }
        return result;
    }

    internal static AgentImage WithoutDisplayRotation(AgentImage image)
    {
        using var decoded = Image.Load(image.Content.Span);
        var orientation = decoded.Metadata.ExifProfile?.GetValue(ExifTag.Orientation)?.Value;
        if (orientation is null or 1) return image;

        // The detector, MCP tool and cropper all use raw pixel coordinates, not EXIF display axes.
        decoded.Metadata.ExifProfile = null;
        decoded.Metadata.XmpProfile = null;
        using var output = new MemoryStream();
        decoded.SaveAsPng(output);
        return new AgentImage(output.ToArray(), Path.GetFileNameWithoutExtension(image.FileName) + ".png", "image/png");
    }

    private static double Overlap(CardQuad first, CardQuad second)
    {
        var a = first.BoundingBox();
        var b = second.BoundingBox();
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }

    /// <summary>
    /// Rejects a quad that cannot be a card: degenerate, off the image, covering nearly all of it,
    /// or far from the 2.5 x 3.5 aspect ratio even allowing for perspective.
    /// <para>
    /// A model that mislocates a card produces a crop of the table, which identification then
    /// declines for reasons that read like a catalogue problem. Discarding the quad here turns that
    /// into one honest warning naming the real culprit.
    /// </para>
    /// </summary>
    internal static bool IsPlausibleCard(CardQuad quad, int width, int height)
    {
        if (!quad.IsValid || width <= 0 || height <= 0) return false;

        var (x, y, w, h) = quad.BoundingBox();

        // Wholly outside, or inverted.
        if (w <= 0 || h <= 0) return false;
        if (x + w <= 0 || y + h <= 0 || x >= width || y >= height) return false;

        // A card is a minority of a photo of several cards, and never a sliver.
        var area = w * h;
        var imageArea = (double)width * height;
        if (area < imageArea * 0.001 || area > imageArea * 0.95) return false;

        // Perspective can skew the ratio a long way, but not past these bounds for a card whose
        // true ratio is 2.5:3.5 either way up.
        var ratio = w / h;
        return ratio is >= 0.25 and <= 4.0;
    }

    /// <summary>
    /// Parses the boundary agent reply and scales it to pixels. Internal so the contract can be
    /// self-tested.
    /// <para>
    /// Coordinates are expected as fractions of the image, but a model that ignores the instruction
    /// and answers in pixels is common enough to be worth handling: any quad whose corners all
    /// exceed 1 is treated as already being in pixels.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DetectedBoundary> ParseBoundaries(string reply, int width, int height)
    {
        using var doc = JsonDocument.Parse(AgentJson.ExtractJsonObject(reply));
        var results = new List<DetectedBoundary>();

        if (!doc.RootElement.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Boundary reply must contain a cards array.");

        foreach (var card in cards.EnumerateArray())
        {
            if (card.TryGetProperty("quad", out var nested)
                && (nested.TryGetProperty("confidence", out _) || nested.TryGetProperty("gameHint", out _)))
                throw new InvalidOperationException("Boundary confidence and gameHint belong outside quad.");
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

            var scaled = LooksNormalized(parsed)
                ? parsed.Select(p => new ImagePoint(p.X * width, p.Y * height)).ToList()
                : parsed;

            var confidence = card.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv) ? cv : 0d;
            var gameHint = card.TryGetProperty("gameHint", out var g) ? g.GetString() : null;

            results.Add(new DetectedBoundary(new CardQuad(scaled), Math.Clamp(confidence, 0d, 1d), CardGames.Normalize(gameHint)));
        }

        return results;
    }

    /// <summary>
    /// True when every corner is within the unit square, meaning the reply is in fractions.
    /// <para>
    /// A pixel-coordinate quad can only be mistaken for this if the card occupies the top-left
    /// pixel or two of the image, which is not a card.
    /// </para>
    /// </summary>
    private static bool LooksNormalized(IReadOnlyList<ImagePoint> points) =>
        points.All(p => p.X is >= 0d and <= 1d && p.Y is >= 0d and <= 1d);
}

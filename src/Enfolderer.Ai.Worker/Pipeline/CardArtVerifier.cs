using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Pipeline;

/// <summary>
/// Checks an identification against the photograph it came from, by comparing the crop with the
/// catalogue's picture of each printing the card could be.
/// <para>
/// This runs in the orchestrator and nowhere else, because the orchestrator is the only component
/// that holds both halves. The identification agent is handed the crop as bytes and never learns
/// where it is stored — that isolation is deliberate and worth keeping — while the catalogue
/// server knows every printing but has never seen the photograph. Only here do the two meet.
/// </para>
/// <para>
/// It is a check, not a step: everything it can go wrong on ends in
/// <see cref="ArtVerdict.NotChecked"/> and leaves the card exactly as the agent returned it. An
/// unreachable image, a host that is not the catalogue's, a crop that will not decode — none of
/// these are reasons to lose a row that was otherwise read correctly.
/// </para>
/// </summary>
public sealed class CardArtVerifier
{
    /// <summary>
    /// Hosts whose images will be fetched.
    /// <para>
    /// The URLs come from an agent's reply, which is to say from a language model repeating what a
    /// tool told it. That makes every fetch here an outbound request to an address chosen by
    /// something upstream of us, and a model that hallucinates a URL — or an MCP server that has
    /// been tampered with — would otherwise have the worker's managed identity making requests
    /// wherever it liked. The list is short because the catalogue only ever serves images from one
    /// place; anything else is a mistake or an attack, and both deserve the same answer.
    /// </para>
    /// <para>
    /// Enforced on the parsed <see cref="Uri.Host"/>, not by substring: <c>scryfall.io.evil.test</c>
    /// contains the allowed name and must not pass.
    /// </para>
    /// </summary>
    private static readonly string[] AllowedImageHosts =
    [
        "cards.scryfall.io",
        "c1.scryfall.com",
        "c2.scryfall.com",
        "c3.scryfall.com"
    ];

    /// <summary>
    /// Largest catalogue image accepted, in bytes. A card face at <c>normal</c> size is a couple
    /// of hundred kilobytes; this is loose enough never to bind on a real one and tight enough
    /// that a wrong URL cannot stream indefinitely into the worker's memory.
    /// </summary>
    private const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How many printings are compared. Each one is a download and a hash, so the cost is linear
    /// in this, and a card with more printings than this is a staple whose printings almost
    /// certainly share an illustration — the case the comparison declines on anyway.
    /// </summary>
    private const int MaxCandidates = 12;

    private readonly HttpClient _http;
    private readonly ILogger<CardArtVerifier> _log;

    public CardArtVerifier(HttpClient http, ILogger<CardArtVerifier> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Compares <paramref name="cropBytes"/> against the card's candidate printings and returns
    /// the card, possibly moved to the printing the photograph actually shows.
    /// </summary>
    public async Task<IdentifiedCard> VerifyAsync(
        IdentifiedCard card, byte[] cropBytes, string jobId, CancellationToken ct)
    {
        if (!card.IsIdentified) return card;
        if (card.ArtReferences.Count == 0)
        {
            _log.LogWarning("Card {Index} of job {JobId}: art not checked, no candidate images supplied.",
                card.Index, jobId);
            return card with { ArtReason = "no_references" };
        }

        var references = card.ArtReferences
            .Where(r => IsFetchable(r.ImageUrl))
            .Take(MaxCandidates)
            .ToList();

        if (references.Count == 0)
        {
            _log.LogWarning(
                "Card {Index} of job {JobId} offered {Count} picture(s), none of them on the catalogue's "
                + "image hosts, so the art was not checked.",
                card.Index, jobId, card.ArtReferences.Count);
            return card with { ArtReason = "no_fetchable_references" };
        }

        IReadOnlyList<ulong[]> cropHashes;
        try
        {
            using var crop = new MemoryStream(cropBytes, writable: false);
            cropHashes = CardArtHash.ComputeAlignments(crop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Card {Index} of job {JobId}: the crop could not be hashed.", card.Index, jobId);
            return card with { ArtReason = "crop_unreadable" };
        }

        var measured = new List<ArtCandidate>(references.Count);
        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();

            var hash = await TryHashAsync(reference.ImageUrl, ct);
            if (hash is null)
            {
                _log.LogWarning("Card {Index} of job {JobId}: could not compare candidate {Set} {Number}.",
                    card.Index, jobId, reference.Set, reference.CollectorNumber);
                continue;
            }

            var distance = cropHashes.Min(cropHash => CardArtHash.NormalisedDistance(cropHash, hash));
            _log.LogInformation(
                "Card {Index} of job {JobId}: art candidate {Set} {Number}, aligned distance {Distance:0.000}.",
                card.Index, jobId, reference.Set, reference.CollectorNumber, distance);
            measured.Add(new ArtCandidate(
                reference.Set,
                reference.CollectorNumber,
                distance));
        }

        var identified = card.Set is { Length: > 0 } set && card.CollectorNumber is { Length: > 0 } number
            ? (set, number)
            : ((string, string)?)null;

        var comparison = ArtAdjudicator.Adjudicate(identified, measured);
        var reason = measured.Count == 0 ? "no_images_readable"
            : measured.Count != references.Count ? "missing_candidates"
            : comparison.Best!.Distance > ArtAdjudicator.MaxAgreeingDistance ? "distance_too_large"
            : measured.Count == 1 && comparison.Verdict == ArtVerdict.Inconclusive ? "insufficient_candidates"
            : measured.Count > 1 && comparison.Separation < ArtAdjudicator.MinSeparation ? "separation_too_small"
            : "match";

        _log.LogInformation(
            "Card {Index} of job {JobId}: art compared {Compared}/{Offered} candidates; "
            + "best {Set} {Number}, distance {Distance:0.000} (maximum {MaxDistance:0.000}), "
            + "separation {Separation:0.000} (minimum {MinSeparation:0.000}); reason {Reason}.",
            card.Index, jobId, measured.Count, references.Count,
            comparison.Best?.Set, comparison.Best?.CollectorNumber, comparison.Best?.Distance,
            ArtAdjudicator.MaxAgreeingDistance, comparison.Separation, ArtAdjudicator.MinSeparation, reason);

        card = card with { ArtReason = reason, ArtDistance = comparison.Best?.Distance };
        if (reason == "missing_candidates")
            return card with { ArtVerdict = "inconclusive", ArtMargin = comparison.Separation };

        return comparison.Verdict switch
        {
            ArtVerdict.NotChecked => card,

            ArtVerdict.Inconclusive => card with
            {
                ArtVerdict = "inconclusive",
                ArtMargin = comparison.Separation
            },

            ArtVerdict.Agrees => card with
            {
                ArtVerdict = "agrees",
                ArtMargin = comparison.Separation
            },

            // The set and number change; the name does not. The candidates are all printings of
            // one card, so a move can never rename it — which is what makes moving safe. The
            // previous printing is recorded rather than discarded, because a move is a claim and a
            // claim that cannot be audited is no better than a guess.
            ArtVerdict.Moved => card with
            {
                Set = comparison.Best!.Set,
                CollectorNumber = comparison.Best.CollectorNumber,
                ArtVerdict = "moved",
                ArtMargin = comparison.Separation,
                ArtMovedFrom = $"{card.Set} {card.CollectorNumber}"
            },

            _ => card
        };
    }

    /// <summary>
    /// Whether a URL is an absolute <c>https</c> address on one of the catalogue's image hosts.
    /// Internal so the host check can be asserted on directly: it is the whole of the protection
    /// around an outbound request whose address came from a model.
    /// </summary>
    internal static bool IsFetchable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return AllowedImageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<ulong[]?> TryHashAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Catalogue image {Url} returned HTTP {Status}.", url, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                _log.LogWarning("Catalogue image {Url} exceeds the {Limit} byte limit.", url, MaxImageBytes);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffered = new MemoryStream();

            // Copied with a cap rather than trusted to Content-Length, which is a claim the server
            // makes and need not keep.
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffered.Length + read > MaxImageBytes)
                {
                    _log.LogWarning("Catalogue image {Url} exceeds the {Limit} byte limit.", url, MaxImageBytes);
                    return null;
                }
                buffered.Write(chunk, 0, read);
            }

            buffered.Position = 0;
            return CardArtHash.Compute(buffered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Catalogue image {Url} could not be read for comparison.", url);
            return null;
        }
    }
}

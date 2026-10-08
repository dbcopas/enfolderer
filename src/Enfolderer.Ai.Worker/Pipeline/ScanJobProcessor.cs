using Azure;
using Azure.Core;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Pipeline;

/// <summary>
/// The <c>OrchestratorAgent</c> role, expressed as worker code: it owns the job, calls Team A's
/// boundary agent across the project boundary, performs the crops itself (so Team A never needs
/// write access to storage) and fans the crops out to Team B's per-game identification agents.
/// </summary>
public sealed class ScanJobProcessor
{
    public const string OrchestratorAgentId = "cardid/OrchestratorAgent";

    private readonly IJobStore _jobs;
    private readonly IScanImageStore _images;
    private readonly ICardBoundaryAgent _boundaryAgent;
    private readonly IReadOnlyDictionary<string, ICardIdentificationAgent> _idAgents;
    private readonly ScanPipelineOptions _options;
    private readonly TokenCredential _credential;
    private readonly ScanPlatformOptions _platform;
    private readonly CardArtVerifier? _artVerifier;
    private readonly ILogger<ScanJobProcessor> _log;

    public ScanJobProcessor(
        IJobStore jobs,
        IScanImageStore images,
        ICardBoundaryAgent boundaryAgent,
        IEnumerable<ICardIdentificationAgent> idAgents,
        ScanPipelineOptions options,
        TokenCredential credential,
        ScanPlatformOptions platform,
        ILogger<ScanJobProcessor> log,
        CardArtVerifier? artVerifier = null)
    {
        _jobs = jobs;
        _images = images;
        _boundaryAgent = boundaryAgent;
        _idAgents = idAgents.ToDictionary(a => a.Game, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _credential = credential;
        _platform = platform;
        // Optional so the offline pipeline, which has no network, runs unchanged: with no verifier
        // every card simply goes unchecked, which is what an unreachable catalogue would do too.
        _artVerifier = artVerifier;
        _log = log;
    }

    public async Task ProcessAsync(ScanJobMessage message, CancellationToken ct)
    {
        var job = await _jobs.GetAsync(message.JobId, ct);
        if (job is null)
        {
            _log.LogWarning("Job {JobId} is no longer in the store; dropping the message.", message.JobId);
            return;
        }

        try
        {
            job = await _jobs.UpsertAsync(job with { Status = ScanJobStatus.DetectingBoundaries }, ct);

            // Downloaded once and reused for the dimensions and every crop: a binder page can hold
            // eighteen cards, and re-fetching the photo per card multiplies blob egress.
            using var source = await BufferAsync(job.BlobPath, ct);
            var dimensions = PerspectiveCropper.ReadDimensions(source);

            // Team A gets the photograph and nothing else: no job id, no storage path, no hint of
            // what the customer wrote in the notes.
            var scanName = Path.GetFileName(job.BlobPath);
            var scan = new AgentImage(source.ToArray(), scanName, ScanBlobPaths.ContentTypeFor(scanName));
            var boundaries = await _boundaryAgent.DetectAsync(scan, job.BlobPath, dimensions.Width, dimensions.Height, ct);
            job = await _jobs.UpsertAsync(
                job with { Status = ScanJobStatus.Identifying, CardsDetected = boundaries.Count },
                ct);

            var cards = await IdentifyAllAsync(job, boundaries, source, ct);
            var identified = cards.Count(c => c.IsIdentified);

            var result = new ScanResultDocument
            {
                JobId = job.JobId,
                Status = ScanJobStatus.Completed,
                ImageWidth = dimensions.Width,
                ImageHeight = dimensions.Height,
                Cards = cards
            };

            await _jobs.UpsertAsync(
                job with
                {
                    Status = ScanJobStatus.Completed,
                    CardsIdentified = identified,
                    Result = result
                },
                ct);

            _log.LogInformation(
                "Job {JobId} completed: {Detected} card(s) detected, {Identified} identified.",
                job.JobId, boundaries.Count, identified);

            // Detecting cards and identifying none usually means the crops are wrong rather than
            // the agents: the rectified faces are in storage, so point at them by name.
            if (identified == 0 && boundaries.Count > 0)
            {
                _log.LogWarning(
                    "Job {JobId} identified none of its {Detected} card(s). The crops handed to the "
                    + "identification agents are in the '{Container}' container under '{Prefix}/'; "
                    + "open them to tell a bad crop from a refused identification.",
                    job.JobId, boundaries.Count, ScanBlobPaths.CropsContainer, job.JobId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A revoked cross-project connection lands here while the job is still in
            // DetectingBoundaries, which is exactly the first Foundry boundary demo.
            //
            // A storage refusal lands here too, and in the same state, because the first thing the
            // job does is read its own photograph. "403" on its own cannot be acted on: the useful
            // question is which principal was refused and for which blob, since the worker's role
            // is scoped to one container and a host can carry more than one identity.
            var error = ex switch
            {
                FoundryAccessException { IsAuthorizationFailure: true } access
                    => $"Foundry authorization failure ({access.StatusCode}). {access.Message}",
                RequestFailedException { Status: 401 or 403 } refused
                    => await DescribeStorageRefusalAsync(refused, job.BlobPath),
                _ => ex.Message
            };

            _log.LogError(ex, "Job {JobId} failed in state {Status}. {Error}", job.JobId, job.Status, error);
            await _jobs.UpsertAsync(job with { Status = ScanJobStatus.Failed, Error = error }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Turns a storage refusal into something that names the principal Azure actually refused.
    /// <para>
    /// Configuration says only which identity was <em>asked</em> for, so reading the object id back
    /// off the issued token is the only way to tell a missing role assignment from the app
    /// presenting a different identity than the one the role was granted to.
    /// </para>
    /// </summary>
    private async Task<string> DescribeStorageRefusalAsync(RequestFailedException ex, string blobPath)
    {
        var principal = await IdentityDiagnostics.DescribeAsync(
            _credential, "https://storage.azure.com/.default", _log);

        return $"Azure Storage returned {ex.Status} {ex.ErrorCode} reading {blobPath} as {principal} "
             + $"(configured client id {_platform.ManagedIdentityClientId ?? "(unset)"}). The worker's "
             + "identity needs Storage Blob Data Reader on the scans container and Storage Blob Data "
             + "Contributor on crops; compare the oid above with the worker identity's principal id.";
    }

    /// <summary>
    /// Records how one card's printing was settled, identified or not.
    /// <para>
    /// <c>corrected</c>, <c>fuzzy</c>, <c>unplaced</c> and <c>ambiguous</c> are warnings rather
    /// than information: the first means a number was misread, and a photograph that misreads one
    /// is likely misreading others; the second means the name itself only matched approximately;
    /// the third means nothing read off the card chose the printing, so the set and number are the
    /// catalogue's default; the fourth means two real printings could not be told apart, so the
    /// printing named is the likeliest rather than the known one.
    /// </para>
    /// <para>
    /// A card with no resolution at all is the one worth acting on, because it means no catalogue
    /// was consulted — the agent answered from the crop alone, or never called the tool.
    /// </para>
    /// </summary>
    private void LogResolution(int index, string jobId, string agentId, IdentifiedCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Resolution))
        {
            // Nothing to say about an unidentified card that the warning above has not said: with
            // no resolution and no printing there is no catalogue answer to report.
            if (!card.IsIdentified) return;

            _log.LogWarning(
                "Card {Index} of job {JobId} was identified by {Agent} as {Set} {Number} ({Name}) but said "
                + "nothing about how it was settled, which is what a reply looks like when the catalogue was "
                + "never asked. The number is the agent's own reading and nothing has checked it.",
                index, jobId, agentId, card.Set, card.CollectorNumber, card.Name);
            return;
        }

        // The catalogue answered and the card was still dropped, which now means the reply had no
        // name or no set in it at all — the number alone is not enough to name a card. Worth its
        // own line: it says the tool ran, so the fix is in what was read off the crop, not in how
        // the agent is wired.
        if (!card.IsIdentified)
        {
            _log.LogWarning(
                "Card {Index} of job {JobId} was dropped after the catalogue answered {Resolution}: the reply "
                + "carried neither a name nor a set. The tool ran, so this is a reading problem rather than "
                + "a wiring one.",
                index, jobId, card.Resolution);
            return;
        }

        var doubtful = card.Resolution is not null
                    && (card.Resolution.Equals("corrected", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("fuzzy", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("unplaced", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("relocated", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("ambiguous", StringComparison.OrdinalIgnoreCase));

        var corrected = string.Equals(card.Resolution, "corrected", StringComparison.OrdinalIgnoreCase);

        var moved = string.Equals(card.ArtVerdict, "moved", StringComparison.OrdinalIgnoreCase);

        _log.Log(doubtful || moved || !card.HasPrinting ? LogLevel.Warning : LogLevel.Information,
            "Card {Index} of job {JobId}: {Set} {Number} ({Name}, {Language}) resolved as {Resolution}{Read}{Art}.",
            index, jobId, card.Set, card.CollectorNumber ?? "(no number)", card.Name, card.Language, card.Resolution,
            corrected && !string.IsNullOrWhiteSpace(card.ReadCollectorNumber)
                ? $" — the number read off the card was '{card.ReadCollectorNumber}'"
                : !card.PrintingWasPlaced
                    ? " — no set code was read off the card and no number matched one, so that set and "
                      + "number are the catalogue's default printing of the name, not this card's. "
                      + "Check the art before believing them."
                    : string.Empty,
            DescribeArt(card));
    }

    /// <summary>
    /// What the picture comparison added to this card, as a clause for the resolution line.
    /// <para>
    /// The margin is what is reported, not the distance. A photograph through a sleeve is never
    /// close to a catalogue scan in absolute terms, so the distance alone would read as alarming
    /// on cards that are perfectly right; being clearly nearer one printing than every other is
    /// the entire claim being made, and the number that carries it.
    /// </para>
    /// <para>
    /// Silence where nothing was compared is deliberate. "Inconclusive" is said out loud because
    /// it means the check ran and declined — the usual outcome for a reprint that shares its
    /// illustration — and that is different from a check that never happened.
    /// </para>
    /// </summary>
    private static string DescribeArt(IdentifiedCard card) => card.ArtVerdict?.ToLowerInvariant() switch
    {
        "agrees" => $", and the art matches that printing (clearer than the next by {card.ArtMargin:0.00})",
        "moved" => $", then moved to {card.Set} {card.CollectorNumber} because the art matches it and not "
                 + $"{card.ArtMovedFrom} (clearer by {card.ArtMargin:0.00}). The set or number read off the "
                 + "card belongs to a different printing of it",
        "inconclusive" => ", and the art could not separate its printings, which is what a shared "
                        + "illustration looks like and is not a fault",
        _ => string.Empty
    };

    private async Task<List<IdentifiedCard>> IdentifyAllAsync(
        ScanJobDocument job,
        IReadOnlyList<DetectedBoundary> boundaries,
        MemoryStream source,
        CancellationToken ct)
    {
        var cards = new List<IdentifiedCard>(boundaries.Count);

        for (var index = 0; index < boundaries.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var boundary = boundaries[index];

            // The job-level hint wins over the boundary agent's guess: the user knows which binder
            // they photographed.
            var game = ResolveGame(job.GameHint, boundary.GameHint, _options.DefaultGame);

            if (!_idAgents.TryGetValue(game, out var agent))
            {
                cards.Add(new IdentifiedCard
                {
                    Index = index,
                    Quad = boundary.Quad,
                    Game = game,
                    Confidence = boundary.Confidence,
                    Agent = _boundaryAgent.AgentId,
                    Error = $"No identification agent is deployed for game '{game}'."
                });
                continue;
            }

            try
            {
                var cropPath = $"{ScanBlobPaths.CropsContainer}/{ScanBlobPaths.BuildCropBlobName(job.JobId, index)}";
                // Written to storage for the audit trail — you can open the container mid-demo and
                // see the rectified faces — and handed to the agent as bytes, because Team B cannot
                // reach the container either.
                var cropBytes = await WriteCropAsync(source, boundary.Quad, cropPath, ct);

                var image = new AgentImage(cropBytes, Path.GetFileName(cropPath), "image/png");
                var crop = new CardCrop(index, image, boundary.Quad, boundary.GameHint);
                var card = await agent.IdentifyAsync(crop, ct);

                // After the agent, not instead of it. The agent reads the card; this asks whether
                // the printing it settled on looks like the thing photographed, which is the one
                // question the text cannot answer — a name, a set and a number can all be real and
                // all agree, and still describe a different printing of the same card.
                if (_artVerifier is not null)
                {
                    card = await _artVerifier.VerifyAsync(card, cropBytes, job.JobId, ct);
                }

                // A card the agent declines to identify is not an exception: the run succeeded and
                // the JSON parsed. Logged here because otherwise the only trace is the final count,
                // and "0 identified" is indistinguishable from a pipeline that never called anyone.
                if (!card.IsIdentified)
                {
                    _log.LogWarning(
                        "Card {Index} of job {JobId} was not identified by {Agent}: {Error}",
                        index, job.JobId, agent.AgentId, card.Error ?? "(no reason given)");
                }

                // How the printing was settled, logged for every card — including the ones that
                // were dropped, which is the case that matters most. A row lost without a
                // resolution beside it leaves nothing to tell "the catalogue was never asked"
                // from "it was asked and found nothing", and those have opposite fixes.
                LogResolution(index, job.JobId, agent.AgentId, card);

                // Geometry always comes from Team A, whatever the identification agent echoed back.
                cards.Add(card with { Index = index, Quad = boundary.Quad, Game = game });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Identification failed for card {Index} of job {JobId}.", index, job.JobId);
                cards.Add(new IdentifiedCard
                {
                    Index = index,
                    Quad = boundary.Quad,
                    Game = game,
                    Confidence = boundary.Confidence,
                    Agent = agent.AgentId,
                    Error = ex.Message
                });
            }
        }

        return cards;
    }

    /// <summary>Chooses the identification agent for a card. Internal so it can be self-tested.</summary>
    internal static string ResolveGame(string? jobHint, string? boundaryHint, string defaultGame)
    {
        var job = CardGames.Normalize(jobHint);
        if (job != CardGames.Unknown) return job;

        var boundary = CardGames.Normalize(boundaryHint);
        if (boundary != CardGames.Unknown) return boundary;

        return CardGames.Normalize(defaultGame);
    }

    /// <summary>Reads the source image into memory so it can be decoded repeatedly.</summary>
    private async Task<MemoryStream> BufferAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await _images.OpenReadAsync(blobPath, ct);
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>Crops one card, stores it, and returns the same bytes for the agent call.</summary>
    private async Task<byte[]> WriteCropAsync(MemoryStream source, CardQuad quad, string cropBlobPath, CancellationToken ct)
    {
        source.Position = 0;

        using var crop = new MemoryStream();
        PerspectiveCropper.CropToPng(source, quad, crop, _options.CropHeight);
        crop.Position = 0;

        await _images.WriteAsync(cropBlobPath, crop, "image/png", ct);
        return crop.ToArray();
    }
}

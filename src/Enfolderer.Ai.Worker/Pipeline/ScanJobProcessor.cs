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
    private readonly ILogger<ScanJobProcessor> _log;

    public ScanJobProcessor(
        IJobStore jobs,
        IScanImageStore images,
        ICardBoundaryAgent boundaryAgent,
        IEnumerable<ICardIdentificationAgent> idAgents,
        ScanPipelineOptions options,
        TokenCredential credential,
        ScanPlatformOptions platform,
        ILogger<ScanJobProcessor> log)
    {
        _jobs = jobs;
        _images = images;
        _boundaryAgent = boundaryAgent;
        _idAgents = idAgents.ToDictionary(a => a.Game, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _credential = credential;
        _platform = platform;
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
            var boundaries = await _boundaryAgent.DetectAsync(scan, ct);
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

                // A card the agent declines to identify is not an exception: the run succeeded and
                // the JSON parsed. Logged here because otherwise the only trace is the final count,
                // and "0 identified" is indistinguishable from a pipeline that never called anyone.
                if (!card.IsIdentified)
                {
                    _log.LogWarning(
                        "Card {Index} of job {JobId} was not identified by {Agent}: {Error}",
                        index, job.JobId, agent.AgentId, card.Error ?? "(no reason given)");
                }

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

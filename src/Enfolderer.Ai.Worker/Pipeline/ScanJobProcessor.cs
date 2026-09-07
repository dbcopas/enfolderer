using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
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
    private readonly IReadUrlProvider _readUrls;
    private readonly ICardBoundaryAgent _boundaryAgent;
    private readonly IReadOnlyDictionary<string, ICardIdentificationAgent> _idAgents;
    private readonly ScanPipelineOptions _options;
    private readonly ILogger<ScanJobProcessor> _log;

    public ScanJobProcessor(
        IJobStore jobs,
        IScanImageStore images,
        IReadUrlProvider readUrls,
        ICardBoundaryAgent boundaryAgent,
        IEnumerable<ICardIdentificationAgent> idAgents,
        ScanPipelineOptions options,
        ILogger<ScanJobProcessor> log)
    {
        _jobs = jobs;
        _images = images;
        _readUrls = readUrls;
        _boundaryAgent = boundaryAgent;
        _idAgents = idAgents.ToDictionary(a => a.Game, StringComparer.OrdinalIgnoreCase);
        _options = options;
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

            var dimensions = await ReadDimensionsAsync(job.BlobPath, ct);
            var imageUrl = await _readUrls.GetReadUrlAsync(job.BlobPath, _options.ReadUrlLifetime, ct);

            var boundaries = await _boundaryAgent.DetectAsync(imageUrl, ct);
            job = await _jobs.UpsertAsync(
                job with { Status = ScanJobStatus.Identifying, CardsDetected = boundaries.Count },
                ct);

            var cards = await IdentifyAllAsync(job, boundaries, ct);
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A revoked cross-project connection lands here while the job is still in
            // DetectingBoundaries, which is exactly the first Foundry boundary demo.
            var error = ex is FoundryAccessException { IsAuthorizationFailure: true } access
                ? $"Foundry authorization failure ({access.StatusCode}). {access.Message}"
                : ex.Message;

            _log.LogError(ex, "Job {JobId} failed in state {Status}.", job.JobId, job.Status);
            await _jobs.UpsertAsync(job with { Status = ScanJobStatus.Failed, Error = error }, CancellationToken.None);
        }
    }

    private async Task<List<IdentifiedCard>> IdentifyAllAsync(
        ScanJobDocument job,
        IReadOnlyList<DetectedBoundary> boundaries,
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
                await WriteCropAsync(job.BlobPath, boundary.Quad, cropPath, ct);
                var cropUrl = await _readUrls.GetReadUrlAsync(cropPath, _options.ReadUrlLifetime, ct);

                var crop = new CardCrop(index, cropUrl, boundary.Quad, boundary.GameHint);
                var card = await agent.IdentifyAsync(crop, ct);

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

    private async Task<ImageDimensions> ReadDimensionsAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await _images.OpenReadAsync(blobPath, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return PerspectiveCropper.ReadDimensions(buffer);
    }

    private async Task WriteCropAsync(string sourceBlobPath, CardQuad quad, string cropBlobPath, CancellationToken ct)
    {
        await using var source = await _images.OpenReadAsync(sourceBlobPath, ct);
        using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, ct);
        buffered.Position = 0;

        using var crop = new MemoryStream();
        PerspectiveCropper.CropToPng(buffered, quad, crop, _options.CropHeight);
        crop.Position = 0;

        await _images.WriteAsync(cropBlobPath, crop, "image/png", ct);
    }
}

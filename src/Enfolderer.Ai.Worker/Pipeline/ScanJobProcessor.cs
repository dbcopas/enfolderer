using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    private readonly ScanAnnotationService? _annotation;
    private readonly ICollectionSnapshotStore? _collections;
    private readonly IScannedCollectionStore? _scannedCollections;
    private readonly ILogger<ScanJobProcessor> _log;
    private readonly IJobExecutionGate _executionGate;

    public ScanJobProcessor(
        IJobStore jobs,
        IScanImageStore images,
        ICardBoundaryAgent boundaryAgent,
        IEnumerable<ICardIdentificationAgent> idAgents,
        ScanPipelineOptions options,
        TokenCredential credential,
        ScanPlatformOptions platform,
        ILogger<ScanJobProcessor> log,
        CardArtVerifier? artVerifier = null,
        ScanAnnotationService? annotation = null,
        ICollectionSnapshotStore? collections = null,
        IJobExecutionGate? executionGate = null,
        IScannedCollectionStore? scannedCollections = null)
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
        _annotation = annotation;
        _collections = collections;
        _scannedCollections = scannedCollections;
        _log = log;
        _executionGate = executionGate ?? new LocalJobExecutionGate();
    }

    public async Task ProcessAsync(ScanJobMessage message, CancellationToken ct)
        => await _executionGate.RunAsync(message.JobId, token => ProcessOnceAsync(message, token), ct);

    private async Task ProcessOnceAsync(ScanJobMessage message, CancellationToken ct)
    {
        var job = await _jobs.GetAsync(message.JobId, ct);
        if (job is null)
        {
            _log.LogWarning("Job {JobId} is no longer in the store; dropping the message.", message.JobId);
            return;
        }
        if (job.Status is ScanJobStatus.Completed or ScanJobStatus.Failed)
        {
            _log.LogInformation("Job {JobId} is terminal; ignoring duplicate delivery.", job.JobId);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(job.GameHint) && !CardGames.IsSupported(CardGames.Normalize(job.GameHint)))
                throw new InvalidOperationException("Only Magic: The Gathering (mtg) scans are supported.");

            var totalTimer = Stopwatch.StartNew();
            var stageTimer = Stopwatch.StartNew();
            _log.LogInformation("Job {JobId} started: detecting boundaries.", job.JobId);
            job = await _jobs.UpsertAsync(job with
            {
                Status = ScanJobStatus.DetectingBoundaries, CardsProcessed = 0, CardsIdentified = 0,
                ProgressMessage = null
            }, ct);

            // Downloaded once and reused for the dimensions and every crop: a binder page can hold
            // eighteen cards, and re-fetching the photo per card multiplies blob egress.
            using var source = await BufferAsync(job.BlobPath, ct);
            LogTiming(job.JobId, "source_download", stageTimer);
            stageTimer.Restart();
            var dimensions = PerspectiveCropper.ReadDimensions(source);

            // Team A gets the photograph and nothing else: no job id, no storage path, no hint of
            // what the customer wrote in the notes.
            var scanName = Path.GetFileName(job.BlobPath);
            var scan = new AgentImage(source.ToArray(), scanName, ScanBlobPaths.ContentTypeFor(scanName));
            var boundaries = await _boundaryAgent.DetectAsync(scan, job.BlobPath, dimensions.Width, dimensions.Height, ct);
            LogTiming(job.JobId, "boundaries", stageTimer);
            job = await _jobs.UpsertAsync(
                job with { Status = ScanJobStatus.Identifying, CardsDetected = boundaries.Count },
                ct);

            stageTimer.Restart();
            var cards = await IdentifyAllAsync(job, boundaries, source, ct);
            LogTiming(job.JobId, "all_cards", stageTimer);
            var identified = cards.Count(c => c.IsIdentified);

            var result = new ScanResultDocument
            {
                JobId = job.JobId,
                Status = ScanJobStatus.Completed,
                ImageWidth = dimensions.Width,
                ImageHeight = dimensions.Height,
                Cards = cards
            };

            if (_annotation is not null)
            {
                job = await _jobs.UpsertAsync(job with
                {
                    CardsProcessed = cards.Count, CardsIdentified = identified,
                    ProgressMessage = "Fetching EUR prices and rendering the annotated image..."
                }, ct);
                CollectionSnapshot? snapshot = null;
                IReadOnlyList<ScannedCollectionPrinting>? scanned = null;
                string? ownershipError = null;
                if (job.UseCollection)
                {
                    try
                    {
                        if (_collections is null || _scannedCollections is null || job.OwnerTenantId is null || job.OwnerObjectId is null)
                            throw new InvalidOperationException("Collection storage or job ownership is unavailable.");
                        snapshot = await _collections.ReadAsync(job.OwnerTenantId, job.OwnerObjectId, ct);
                        scanned = await _scannedCollections.ReadAsync(job.OwnerTenantId, job.OwnerObjectId, ct, job.JobId);
                        if (snapshot is null && scanned.Count == 0)
                            throw new InvalidOperationException("No uploaded or previously scanned collection remains available.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(ex, "Job {JobId}: ownership unavailable; preserving scan results.", job.JobId);
                        ownershipError = "Collections could not be loaded. Ownership is unknown; check your collections and scan again.";
                    }
                }
                result = await _annotation.CreateAsync(result, source, ct, snapshot, ownershipError, job.BlobPrefix, scanned);
            }

            result = result with
            {
                CsvBlobPath = ScanBlobPaths.BuildCsvBlobPath(job),
                IdentificationBlobPath = ScanBlobPaths.BuildEvidenceBlobPath(job)
            };
            job = job with { Result = result };
            await WriteArchiveAsync(result.CsvBlobPath, Encoding.UTF8.GetBytes(ScanCsv.Format(result)), "text/csv; charset=utf-8", ct);
            await WriteEvidenceAsync(result.IdentificationBlobPath, new
            {
                schemaVersion = 1, jobId = job.JobId, createdAt = job.CreatedAt,
                evidenceKind = "Observable identification evidence, not private model reasoning.",
                result,
                cards = result.Cards.Select(card => new
                {
                    card.Index, card.Name, card.Set, card.CollectorNumber, card.Error,
                    evidenceBlobPath = ScanBlobPaths.BuildEvidenceBlobPath(job, card.Index)
                })
            }, ct);

            if (job.AutoAddToCollection && result.Cards.Any(ScannedCollectionAddition.CanAdd))
            {
                try
                {
                    if (_scannedCollections is null || job.OwnerTenantId is null || job.OwnerObjectId is null)
                        throw new InvalidOperationException("Scanned collection storage or job ownership is unavailable.");
                    await _scannedCollections.AddAsync(job.OwnerTenantId, job.OwnerObjectId,
                        ScannedCollectionAddition.FromResult(job.JobId, result), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Job {JobId}: automatic collection addition failed; preserving scan results.", job.JobId);
                    result = result with { CollectionAddError = "Automatic collection addition failed. Use Add identified cards to collection to retry." };
                }
            }

            await _jobs.UpsertAsync(
                job with
                {
                    Status = ScanJobStatus.Completed,
                    ProgressMessage = null,
                    CardsProcessed = cards.Count,
                    CardsIdentified = identified,
                    Result = result
                },
                ct);

            _log.LogInformation(
                "Job {JobId} completed: {Detected} card(s) detected, {Identified} identified.",
                job.JobId, boundaries.Count, identified);
            LogTiming(job.JobId, "total", totalTimer);

            // Detecting cards and identifying none usually means the crops are wrong rather than
            // the agents: the rectified faces are in storage, so point at them by name.
            if (identified == 0 && boundaries.Count > 0)
            {
                _log.LogWarning(
                    "Job {JobId} identified none of its {Detected} card(s). The crops handed to the "
                    + "identification agents are in the '{Container}' container under '{Prefix}/'; "
                    + "open them to tell a bad crop from a refused identification.",
                    job.JobId, boundaries.Count, ScanBlobPaths.CropsContainer, job.BlobPrefix ?? job.JobId);
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
    private async Task<string> DescribeStorageRefusalAsync(RequestFailedException ex, string blobPath, string operation = "reading")
    {
        var principal = await IdentityDiagnostics.DescribeAsync(
            _credential, "https://storage.azure.com/.default", _log);

        return $"Azure Storage returned {ex.Status} {ex.ErrorCode} {operation} {blobPath} as {principal} "
             + $"(configured client id {_platform.ManagedIdentityClientId ?? "(unset)"}). The worker's "
             + "identity needs Storage Blob Data Reader on the scans container and Storage Blob Data "
             + "Contributor on crops and outputs; compare the oid above with the worker identity's principal id.";
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
                + "was missing a name or a set. The tool ran, so this is a reading problem rather than "
                + "a wiring one.",
                index, jobId, card.Resolution);
            return;
        }

        var doubtful = card.Resolution is not null
                    && (card.Resolution.Equals("corrected", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("fuzzy", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("unplaced", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("unverified", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("relocated", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("ambiguous", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("assumed", StringComparison.OrdinalIgnoreCase)
                     || card.Resolution.Equals("unresolved", StringComparison.OrdinalIgnoreCase));

        var moved = string.Equals(card.ArtVerdict, "moved", StringComparison.OrdinalIgnoreCase);

        _log.Log(doubtful || moved || !card.HasPrinting ? LogLevel.Warning : LogLevel.Information,
            "Card {Index} of job {JobId}: {Set} {Number} ({Name}, {Language}) resolved as {Resolution}"
            + " (read set '{ReadSet}', read number '{ReadNumber}', read title '{ReadName}', "
            + "read language '{ReadLanguage}', confidence {Confidence:0.00}){Read}{Art}.",
            index, jobId, card.Set, card.CollectorNumber ?? "(no number)", card.Name, card.Language, card.Resolution,
            card.ReadSet ?? "(none)", card.ReadCollectorNumber ?? "(none)",
            card.ReadName ?? "(none)", card.ReadLanguage ?? "(none)", card.Confidence,
            DescribeResolution(card),
            DescribeArt(card));
    }

    internal static string DescribeResolution(IdentifiedCard card) => card.Resolution?.ToLowerInvariant() switch
    {
        "confirmed" => " - the catalogue reports agreement between the name and collector number",
        "corrected" => " - the name won over the number reading; the exported number is the catalogue's",
        "relocated" => " - the catalogue selected a printing outside the read set; the read set was not retained",
        "ambiguous" => " - multiple printings match the name; the available readings did not distinguish them. Check the printing",
        "assumed" => " - the name was not corroborated by the catalogue; the number lookup supplied the printing. Check identity and printing",
        "unverified" => " - the set reading alone selected this printing without a corroborating collector number. Check the printing",
        "unplaced" => " - the name was found but no reading placed its printing; the exported set and number are a catalogue default. Check the printing",
        "fuzzy" => " - the name matched approximately or without an exact set placement. Check the name and printing",
        "unresolved" => " - the catalogue could not resolve the reading; these are the agent's readings, not a confirmed printing",
        "named" => " - the catalogue placed the name without a collector-number reading",
        _ => " - unrecognized resolution; check the catalogue evidence before trusting the printing"
    };

    /// <summary>
    /// What the picture comparison added to this card, as a clause for the resolution line.
    /// <para>
    /// The margin is what is reported, not the distance. A photograph through a sleeve is never
    /// close to a catalogue scan in absolute terms, so the distance alone would read as alarming
    /// on cards that are perfectly right; being clearly nearer one printing than every other is
    /// the entire claim being made, and the number that carries it.
    /// </para>
    /// <para>
    /// An inconclusive comparison may mean poor alignment, missing candidates, or shared art.
    /// Report the measured reason, not an assumption about the illustration.
    /// </para>
    /// </summary>
    private static string DescribeArt(IdentifiedCard card) => card.ArtVerdict?.ToLowerInvariant() switch
    {
        "agrees" => $", and the art matches that printing (clearer than the next by {card.ArtMargin:0.00})",
        "moved" => $", then moved to {card.Set} {card.CollectorNumber} because the art matches it and not "
                 + $"{card.ArtMovedFrom} (clearer by {card.ArtMargin:0.00}). The set or number read off the "
                 + "card belongs to a different printing of it",
        "inconclusive" => $", and the art declined: {card.ArtReason ?? "unspecified"} "
                        + $"(distance {card.ArtDistance:0.000}, separation {card.ArtMargin:0.000})",
        _ => card.ArtReason is null ? string.Empty : $", art not checked: {card.ArtReason}"
    };

    private async Task<List<IdentifiedCard>> IdentifyAllAsync(
        ScanJobDocument job,
        IReadOnlyList<DetectedBoundary> boundaries,
        MemoryStream source,
        CancellationToken ct)
    {
        if (_options.MaxConcurrentCards < 1)
            throw new InvalidOperationException("ScanPipeline:MaxConcurrentCards must be at least 1.");
        var cards = new IdentifiedCard[boundaries.Count];
        var sourceBytes = source.ToArray();
        using var progressGate = new SemaphoreSlim(1);
        using var cropGate = new SemaphoreSlim(1);
        var processed = 0;
        var identified = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, boundaries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentCards, CancellationToken = ct },
            async (index, token) =>
        {
            var evidence = new List<ScanEvidenceEvent>();
            void Record(ScanEvidenceEvent item)
            {
                if (evidence.Count >= 512)
                    throw new InvalidOperationException("Identification evidence exceeded the 512-event per-card limit.");
                evidence.Add(item);
            }
            var card = await IdentifyOneAsync(index, Record, token);
            await WriteEvidenceAsync(ScanBlobPaths.BuildEvidenceBlobPath(job, index), new
            {
                schemaVersion = 1, jobId = job.JobId, index,
                cropBlobPath = $"{ScanBlobPaths.CropsContainer}/{ScanBlobPaths.BuildCropBlobName(job, index)}",
                card, decision = DescribeResolution(card) + DescribeArt(card),
                events = evidence
            }, token);
            await progressGate.WaitAsync(token);
            try
            {
                cards[index] = card;
                processed++;
                if (card.IsIdentified) identified++;
                job = await _jobs.UpsertAsync(job with
                {
                    CardsProcessed = processed,
                    CardsIdentified = identified
                }, token);
            }
            finally { progressGate.Release(); }
        });
        return cards.ToList();

        async Task<IdentifiedCard> IdentifyOneAsync(int index, Action<ScanEvidenceEvent> recordEvidence, CancellationToken ct)
        {
            var cardTimer = Stopwatch.StartNew();
            _log.LogInformation("Card {Index} of job {JobId}: processing card {Current} of {Total}.",
                index, job.JobId, index + 1, boundaries.Count);
            var boundary = boundaries[index];

            // The job-level hint wins over the boundary agent's guess: the user knows which binder
            // they photographed.
            var game = ResolveGame(job.GameHint, boundary.GameHint, _options.DefaultGame);

            if (!_idAgents.TryGetValue(game, out var agent))
            {
                return new IdentifiedCard
                {
                    Index = index,
                    Quad = boundary.Quad,
                    Game = game,
                    Confidence = boundary.Confidence,
                    Agent = _boundaryAgent.AgentId,
                    Error = $"No identification agent is deployed for game '{game}'."
                };
            }

            try
            {
                var cropPath = $"{ScanBlobPaths.CropsContainer}/{ScanBlobPaths.BuildCropBlobName(job, index)}";
                // Written to storage for the audit trail — you can open the container mid-demo and
                // see the rectified faces — and handed to the agent as bytes, because Team B cannot
                // reach the container either.
                var timer = Stopwatch.StartNew();
                byte[] cropBytes;
                // Bound full-photo decodes independently of network concurrency on the 2 GiB worker.
                await cropGate.WaitAsync(ct);
                try
                {
                    using var cardSource = new MemoryStream(sourceBytes, writable: false);
                    cropBytes = await WriteCropAsync(cardSource, boundary.Quad, cropPath, ct);
                }
                finally { cropGate.Release(); }
                LogTiming(job.JobId, "crop_and_store", timer, index);

                var image = new AgentImage(cropBytes, Path.GetFileName(cropPath), "image/png");
                var crop = new CardCrop(index, image, boundary.Quad, boundary.GameHint) { RecordEvidence = recordEvidence };
                timer.Restart();
                var card = await agent.IdentifyAsync(crop, ct);
                recordEvidence(ScanEvidenceEvent.Create("initial_identification",
                    new { card, alternates = card.ArtReferences, card.ArtCandidatesComplete }));
                LogTiming(job.JobId, "identify", timer, index);

                // After the agent, not instead of it. The agent reads the card; this asks whether
                // the printing it settled on looks like the thing photographed, which is the one
                // question the text cannot answer — a name, a set and a number can all be real and
                // all agree, and still describe a different printing of the same card.
                if (_artVerifier is not null)
                {
                    timer.Restart();
                    byte[] artBytes;
                    await cropGate.WaitAsync(ct);
                    try
                    {
                        using var artSource = new MemoryStream(sourceBytes, writable: false);
                        using var artCrop = new MemoryStream();
                        PerspectiveCropper.CropToPng(artSource, boundary.Quad, artCrop, _options.CropHeight);
                        artBytes = artCrop.ToArray();
                    }
                    finally { cropGate.Release(); }
                    card = await _artVerifier.VerifyAndReviewAsync(card, artBytes, job.JobId, crop, agent, ct);
                    LogTiming(job.JobId, "art_verification", timer, index);
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
                return card with { Index = index, Quad = boundary.Quad, Game = game };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Identification failed for card {Index} of job {JobId}: {Error}",
                    index, job.JobId, ex.Message);
                return new IdentifiedCard
                {
                    Index = index,
                    Quad = boundary.Quad,
                    Game = game,
                    Confidence = boundary.Confidence,
                    Agent = agent.AgentId,
                    Error = ex.Message
                };
            }
            finally { LogTiming(job.JobId, "card_total", cardTimer, index); }
        }
    }

    private async Task WriteEvidenceAsync(string path, object evidence, CancellationToken ct)
    {
        await WriteArchiveAsync(path, JsonSerializer.SerializeToUtf8Bytes(evidence, ScanJson.Options), "application/json", ct);
    }

    private async Task WriteArchiveAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        await using var content = new MemoryStream(bytes, writable: false);
        try { await _images.WriteAsync(path, content, contentType, ct); }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new IOException(await DescribeStorageRefusalAsync(ex, path, "writing"), ex);
        }
    }

    private void LogTiming(string jobId, string stage, Stopwatch timer, int? index = null) =>
        _log.LogInformation("Job {JobId}: timing {Stage}, card {Index}, elapsed {ElapsedMs} ms.",
            jobId, stage, index, timer.ElapsedMilliseconds);

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
        PerspectiveCropper.CropToPng(source, quad, crop, _options.CropHeight, marginFraction: 0.02);
        crop.Position = 0;

        await _images.WriteAsync(cropBlobPath, crop, "image/png", ct);
        return crop.ToArray();
    }
}

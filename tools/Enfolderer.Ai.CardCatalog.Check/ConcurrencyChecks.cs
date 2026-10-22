using Azure.Core;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Catalogue;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Enfolderer.Ai.Worker.Agents;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class ConcurrencyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var unsupportedStore = new RecordingStore();
        await unsupportedStore.CreateAsync(new ScanJobDocument
        {
            Id = "unsupported", JobId = "unsupported", BlobPath = "scan.png", GameHint = CardGames.Pokemon
        });
        var unsupportedImages = new Images();
        var unsupportedAgent = new IdentificationAgent();
        var unsupportedProcessor = new ScanJobProcessor(
            unsupportedStore, unsupportedImages, new Boundaries(), [unsupportedAgent],
            new ScanPipelineOptions(), new Credential(), new ScanPlatformOptions(),
            NullLogger<ScanJobProcessor>.Instance);
        await unsupportedProcessor.ProcessAsync(new ScanJobMessage("unsupported", "scan.png", CardGames.Pokemon), default);
        var unsupportedJob = await unsupportedStore.GetAsync("unsupported");
        check(unsupportedJob?.Status == ScanJobStatus.Failed
            && unsupportedJob.Error!.Contains("Only Magic: The Gathering")
            && unsupportedImages.Reads == 0 && unsupportedAgent.Completed.IsEmpty,
            "legacy unsupported queued jobs fail explicitly before image downloads or agent calls");

        check(new ScanPipelineOptions().MaxConcurrentCards == 9, "new branch defaults to nine concurrent cards");
        foreach (var concurrency in new[] { 1, 2, 3, 9 })
        {
            var count = concurrency == 9 ? 12 : 4;
            var store = new RecordingStore();
            var blobPrefix = concurrency == 1 ? null
                : ScanBlobPaths.BuildJobPrefix("parallel", new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            await store.CreateAsync(new ScanJobDocument
            {
                Id = "parallel", JobId = "parallel", BlobPrefix = blobPrefix, BlobPath = "scan.png"
            });
            var agent = new IdentificationAgent(requiredConcurrency: concurrency);
            var images = new Images();
            var processor = new ScanJobProcessor(store, images, new Boundaries(count), [agent],
                new ScanPipelineOptions { CropHeight = 140, MaxConcurrentCards = concurrency },
                new Credential(), new ScanPlatformOptions(), NullLogger<ScanJobProcessor>.Instance);
            await processor.ProcessAsync(new ScanJobMessage("parallel", "scan.png", null), default);
            var job = await store.GetAsync("parallel");
            check(agent.Peak == concurrency, $"card concurrency reaches but never exceeds {concurrency}");
            check(job?.Status == ScanJobStatus.Completed && job.CardsProcessed == count && job.CardsIdentified == count - 1
                && job.Result!.Cards.Select(c => c.Index).SequenceEqual(Enumerable.Range(0, count))
                && job.Result.Cards.Select(c => c.Quad!.BoundingBox().X).SequenceEqual(Enumerable.Range(0, count).Select(i => i * 100d))
                && job.Result.Cards[2].Error == "Test card failure",
                "parallel completion preserves order, geometry and isolated card failures");
            check(concurrency == 1 || agent.Completed.First() != 0,
                "results remain in reading order even when later cards finish first");
            check(store.Progress.SequenceEqual(Enumerable.Range(0, count + 1).Append(count)),
                "persisted progress is monotonic and no concurrent update loses a completed card");
            check(images.WrittenPaths.Count == count && Enumerable.Range(0, count).All(index =>
                    images.WrittenPaths.Contains($"crops/{blobPrefix ?? "parallel"}/card-{index:D3}.png")),
                "parallel workers keep every crop in the persisted dated or legacy job folder");
            await processor.ProcessAsync(new ScanJobMessage("parallel", "scan.png", null), default);
            check(agent.Completed.Count == count - 1 && store.Progress.Count == count + 2,
                "duplicate queue delivery never reruns a completed scan");
        }
        var duplicateStore = new RecordingStore();
        await duplicateStore.CreateAsync(new ScanJobDocument { Id = "duplicate", JobId = "duplicate", BlobPath = "scan.png" });
        var duplicateAgent = new IdentificationAgent();
        var duplicateProcessor = new ScanJobProcessor(duplicateStore, new Images(), new Boundaries(), [duplicateAgent],
            new ScanPipelineOptions { CropHeight = 140 }, new Credential(), new ScanPlatformOptions(),
            NullLogger<ScanJobProcessor>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            duplicateProcessor.ProcessAsync(new ScanJobMessage("duplicate", "scan.png", null), default)));
        check(duplicateAgent.Completed.Count == 3,
            "simultaneous duplicate deliveries run the pipeline only once");
        using var cancellation = new CancellationTokenSource();
        var cancelledStore = new RecordingStore();
        await cancelledStore.CreateAsync(new ScanJobDocument { Id = "parallel", JobId = "parallel", BlobPath = "scan.png" });
        var cancelledAgent = new IdentificationAgent(cancellation);
        var cancelledProcessor = new ScanJobProcessor(cancelledStore, new Images(), new Boundaries(), [cancelledAgent],
            new ScanPipelineOptions { CropHeight = 140 }, new Credential(), new ScanPlatformOptions(),
            NullLogger<ScanJobProcessor>.Instance);
        try
        {
            await cancelledProcessor.ProcessAsync(new ScanJobMessage("parallel", "scan.png", null), cancellation.Token);
            check(false, "cancellation must propagate");
        }
        catch (OperationCanceledException)
        {
            check((await cancelledStore.GetAsync("parallel"))?.Status != ScanJobStatus.Completed,
                "parallel cancellation propagates without publishing a successful result");
        }
        await AutoAddChecksAsync(check);
    }

    private static async Task AutoAddChecksAsync(Action<bool, string> check)
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string user = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        foreach (var mode in new[] { "off", "on", "retry", "add-failure", "read-failure" })
        {
            var jobId = Guid.NewGuid().ToString("N");
            var jobs = new RecordingStore();
            await jobs.CreateAsync(new ScanJobDocument
            {
                Id = jobId, JobId = jobId, BlobPath = "scan.png", OwnerTenantId = tenant, OwnerObjectId = user,
                UseCollection = true, AutoAddToCollection = mode is "on" or "retry" or "add-failure"
            });
            var snapshots = new InMemoryCollectionSnapshotStore();
            await snapshots.WriteAsync(tenant, user, new CollectionSnapshot { Printings = [new("test", "0", 4, 7)] });
            var saved = new InMemoryScannedCollectionStore();
            await saved.AddAsync(tenant, user, new ScannedCollectionAddition
            {
                JobId = Guid.NewGuid().ToString("N"),
                Printings = [new("test", "0", "en", "Card 0", 1), new("test", "0", "ja", "Card 0", 1),
                    new("other", "1", "en", "Card 0", 3)]
            });
            if (mode == "retry")
                await saved.AddAsync(tenant, user, new ScannedCollectionAddition
                    { JobId = jobId, Printings = [new("test", "0", "", "Card 0", 1)] });
            var scanned = new FaultingScannedStore(saved, mode);
            var images = new Images();
            using var http = new HttpClient(new OwnershipPrices());
            var annotations = new ScanAnnotationService(new ScryfallPriceClient(http), images,
                NullLogger<ScanAnnotationService>.Instance);
            var processor = new ScanJobProcessor(jobs, images, new Boundaries(1), [new IdentificationAgent()],
                new ScanPipelineOptions { CropHeight = 140 }, new Credential(), new ScanPlatformOptions(),
                NullLogger<ScanJobProcessor>.Instance, annotation: annotations, collections: snapshots, scannedCollections: scanned);
            await processor.ProcessAsync(new ScanJobMessage(jobId, "scan.png", null), default);
            var job = (await jobs.GetAsync(jobId))!;
            check(job.Status == ScanJobStatus.Completed && job.Result?.AnnotatedImagePath is not null,
                $"auto-add {mode}: collection outcomes do not discard a completed scan or overlay");
            if (mode == "read-failure")
                check(job.Result!.Cards.Single().Ownership?.Error is not null,
                    "failure of either ownership source is explicit, not a success-shaped uploaded-only count");
            else
                check(job.Result!.Cards.Single().Ownership is { Quantity: 6, AllQuantity: 12, Error: null },
                    $"auto-add {mode}: combined ownership is calculated before this scan's contribution, including on retry");
            check((await saved.ReadAsync(tenant, user)).Sum(p => p.Quantity) == (mode is "on" or "retry" ? 6 : 5),
                $"auto-add {mode}: saved quantities reflect the opt-in setting and never duplicate a scan");
            check((job.Result!.CollectionAddError is not null) == (mode == "add-failure"),
                $"auto-add {mode}: failed saves expose a manual-retry error without marking other outcomes as failed");
            await processor.ProcessAsync(new ScanJobMessage(jobId, "scan.png", null), default);
            check((await saved.ReadAsync(tenant, user)).Sum(p => p.Quantity) == (mode is "on" or "retry" ? 6 : 5),
                $"auto-add {mode}: a duplicate queue message cannot add another copy");
        }
    }

    private sealed class FaultingScannedStore(InMemoryScannedCollectionStore inner, string mode) : IScannedCollectionStore
    {
        public Task<bool> AddAsync(string tenantId, string objectId, ScannedCollectionAddition addition, CancellationToken ct = default) =>
            mode == "add-failure" ? throw new IOException("Test scanned collection write failure") : inner.AddAsync(tenantId, objectId, addition, ct);
        public Task<bool> ContainsAsync(string tenantId, string objectId, string jobId, CancellationToken ct = default) =>
            inner.ContainsAsync(tenantId, objectId, jobId, ct);
        public Task<IReadOnlyList<ScannedCollectionPrinting>> ReadAsync(string tenantId, string objectId, CancellationToken ct = default,
            string? excludingJobId = null) => mode == "read-failure" ? throw new IOException("Test scanned collection read failure")
                : inner.ReadAsync(tenantId, objectId, ct, excludingJobId);
    }

    private sealed class OwnershipPrices : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath == "/cards/search"
                ? """{"data":[{"set":"test","collector_number":"0","oracle_id":"11111111-1111-1111-1111-111111111111"},{"set":"other","collector_number":"1","oracle_id":"11111111-1111-1111-1111-111111111111"}],"has_more":false}"""
                : """{"set":"test","collector_number":"0","lang":"en","name":"Card 0","layout":"normal","oracle_id":"11111111-1111-1111-1111-111111111111","prices":{"eur":"1.00","eur_foil":"2.00"}}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RecordingStore : IJobStore
    {
        public Task MarkUploadedAsync(string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        private ScanJobDocument? _job;
        public List<int> Progress { get; } = [];
        public Task<ScanJobDocument> CreateAsync(ScanJobDocument job, CancellationToken ct = default)
        {
            _job = job;
            return Task.FromResult(job);
        }
        public Task<ScanJobDocument?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_job);
        public Task<IReadOnlyList<ScanJobDocument>> FindRecentAsync(string tenantId, IReadOnlyList<string> owners,
            DateTimeOffset since, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<ScanJobDocument> UpsertAsync(ScanJobDocument job, CancellationToken ct = default)
        {
            await Task.Delay(1, ct);
            _job = job;
            if (job.Status != ScanJobStatus.DetectingBoundaries) Progress.Add(job.CardsProcessed);
            return job;
        }
    }

    private sealed class Boundaries(int count = 4) : ICardBoundaryAgent
    {
        public string AgentId => "boundary";
        public Task<IReadOnlyList<DetectedBoundary>> DetectAsync(AgentImage image, string path, int width, int height,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DetectedBoundary>>(
                Enumerable.Range(0, count).Select(i => new DetectedBoundary(
                    new CardQuad([new(i * 100, 0), new(i * 100 + 99, 0),
                        new(i * 100 + 99, 139), new(i * 100, 139)]), 1, CardGames.Magic)).ToArray());
    }

    private sealed class IdentificationAgent(CancellationTokenSource? cancellation = null, int requiredConcurrency = 0) : ICardIdentificationAgent
    {
        private int _active;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<int> Completed { get; } = new();
        public int Peak { get; private set; }
        public string Game => CardGames.Magic;
        public string AgentId => "identifier";
        public async Task<IdentifiedCard> IdentifyAsync(CardCrop crop, CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _active);
            lock (this) Peak = Math.Max(Peak, active);
            if (active >= requiredConcurrency) _started.TrySetResult();
            try
            {
                cancellation?.Cancel();
                await _started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await Task.Delay(crop.Index == 0 ? 120 : 25, ct);
                using var decoded = Image.Load<Rgba32>(crop.Image.Content.ToArray());
                if (crop.Index == 2) throw new InvalidOperationException("Test card failure");
                Completed.Enqueue(crop.Index);
                return new IdentifiedCard { Name = $"Card {crop.Index}", Set = "test", CollectorNumber = $"{crop.Index}" };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class Images : IScanImageStore
    {
        public int Reads { get; private set; }
        public System.Collections.Concurrent.ConcurrentQueue<string> WrittenPaths { get; } = new();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            Reads++;
            using var image = new Image<Rgba32>(1200, 140);
            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return Task.FromResult<Stream>(stream);
        }
        public Task WriteAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        {
            WrittenPaths.Enqueue(path);
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

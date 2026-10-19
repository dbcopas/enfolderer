using Azure.Core;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
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

        foreach (var concurrency in new[] { 1, 2, 3 })
        {
            var store = new RecordingStore();
            await store.CreateAsync(new ScanJobDocument { Id = "parallel", JobId = "parallel", BlobPath = "scan.png" });
            var agent = new IdentificationAgent();
            var processor = new ScanJobProcessor(store, new Images(), new Boundaries(), [agent],
                new ScanPipelineOptions { CropHeight = 140, MaxConcurrentCards = concurrency },
                new Credential(), new ScanPlatformOptions(), NullLogger<ScanJobProcessor>.Instance);
            await processor.ProcessAsync(new ScanJobMessage("parallel", "scan.png", null), default);
            var job = await store.GetAsync("parallel");
            check(agent.Peak == concurrency, $"card concurrency reaches but never exceeds {concurrency}");
            check(job?.Status == ScanJobStatus.Completed && job.CardsProcessed == 4 && job.CardsIdentified == 3
                && job.Result!.Cards.Select(c => c.Index).SequenceEqual([0, 1, 2, 3])
                && job.Result.Cards.Select(c => c.Quad!.BoundingBox().X).SequenceEqual(new double[] { 0, 100, 200, 300 })
                && job.Result.Cards[2].Error == "Test card failure",
                "parallel completion preserves order, geometry and isolated card failures");
            check(concurrency == 1 || agent.Completed.First() != 0,
                "results remain in reading order even when later cards finish first");
            check(store.Progress.SequenceEqual([0, 1, 2, 3, 4, 4]),
                "persisted progress is monotonic and no concurrent update loses a completed card");
            await processor.ProcessAsync(new ScanJobMessage("parallel", "scan.png", null), default);
            check(agent.Completed.Count == 3 && store.Progress.Count == 6,
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

    private sealed class Boundaries : ICardBoundaryAgent
    {
        public string AgentId => "boundary";
        public Task<IReadOnlyList<DetectedBoundary>> DetectAsync(AgentImage image, string path, int width, int height,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DetectedBoundary>>(
                Enumerable.Range(0, 4).Select(i => new DetectedBoundary(
                    new CardQuad([new(i * 100, 0), new(i * 100 + 99, 0),
                        new(i * 100 + 99, 139), new(i * 100, 139)]), 1, CardGames.Magic)).ToArray());
    }

    private sealed class IdentificationAgent(CancellationTokenSource? cancellation = null) : ICardIdentificationAgent
    {
        private int _active;
        public System.Collections.Concurrent.ConcurrentQueue<int> Completed { get; } = new();
        public int Peak { get; private set; }
        public string Game => CardGames.Magic;
        public string AgentId => "identifier";
        public async Task<IdentifiedCard> IdentifyAsync(CardCrop crop, CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _active);
            lock (this) Peak = Math.Max(Peak, active);
            try
            {
                cancellation?.Cancel();
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
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            Reads++;
            using var image = new Image<Rgba32>(400, 140);
            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return Task.FromResult<Stream>(stream);
        }
        public Task WriteAsync(string path, Stream content, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;
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

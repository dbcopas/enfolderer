using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Worker.Agents;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class ScanReliabilityChecks
{
    public static async Task RunOfflineAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nBounded retries of terminal Foundry failures");
        check(FoundryAgentClient.IsServerError("failed", "SERVER_ERROR"), "terminal server errors are retryable");
        check(!FoundryAgentClient.IsServerError("cancelled", "server_error"), "cancellation is not retried");
        check(!FoundryAgentClient.IsServerError("failed", "invalid_request"), "invalid requests are not retried");

        await CheckRunsAsync(check, ["server_error", "completed"], 2, null);
        await CheckRunsAsync(check, ["server_error"], 3, "2 server_error retries were exhausted");
        await CheckRunsAsync(check, ["invalid_request"], 1, "invalid_request");
        await CheckRunsAsync(check, ["rate_limit_exceeded", "server_error", "completed"], 3, null);
        await CheckRunsAsync(check, ["rate_limit_exceeded"], 4, "3 retries did not clear it");
        await CheckRunsAsync(check, ["completed"], 1, "403", HttpStatusCode.Forbidden);
        await CheckRunsAsync(check, ["completed"], 1, "500", HttpStatusCode.InternalServerError);

        using (var cancellation = new CancellationTokenSource())
        {
            var log = new ListLogger<FoundryAgentClient>();
            log.OnLog = text =>
            {
                if (text.Contains("retry 1 of 2", StringComparison.Ordinal)) cancellation.Cancel();
            };
            using var handler = new RunHandler(["server_error"]);
            using var http = new HttpClient(handler);
            var client = Client(http, log);
            try
            {
                await client.RunAsync("agent", "identify", Image(), "catalogue", cancellation.Token);
                check(false, "cancellation interrupts server-error backoff");
            }
            catch (OperationCanceledException)
            {
                check(handler.Runs == 1 && handler.Deletes == 1,
                    "cancellation interrupts backoff and still cleans up the uploaded image");
            }
        }

        Console.WriteLine("\nAligned art comparison and explicit diagnostics");
        using var reference = Pattern(7);
        using var other = Pattern(19);
        using var padded = new Image<Rgba32>(160, 220, new Rgba32(35, 35, 35));
        using var smaller = reference.Clone(c => c.Resize(128, 176));
        padded.Mutate(c => c.DrawImage(smaller, new Point(24, 22), 1f));
        var referenceBytes = Png(reference);
        var otherBytes = Png(other);
        var cropBytes = Png(padded);
        using (var crop = new MemoryStream(cropBytes))
        using (var target = new MemoryStream(referenceBytes))
        {
            var targetHash = CardArtHash.Compute(target);
            var aligned = CardArtHash.ComputeAlignments(crop).Min(h => CardArtHash.NormalisedDistance(h, targetHash));
            check(aligned <= ArtAdjudicator.MaxAgreeingDistance, "bounded alignment tolerates sleeve margins and offsets");
        }

        var card = new IdentifiedCard
        {
            Index = 1, Set = "old", CollectorNumber = "1", Name = "Synthetic card",
            ReadSet = "misread", ReadCollectorNumber = "013", Resolution = "relocated",
            ArtReferences = [
                new("old", "1", "https://cards.scryfall.io/old.png"),
                new("new", "2", "https://cards.scryfall.io/new.png")]
        };
        var (moved, logs) = await VerifyAsync(card, cropBytes, new Dictionary<string, byte[]>
        {
            ["/old.png"] = otherBytes, ["/new.png"] = referenceBytes
        });
        check(moved.Set == "new" && moved.ArtVerdict == "moved" && moved.ArtMovedFrom == "old 1",
            "the worker moves an offset crop to its matching printing");
        check(moved.ArtReason == "match" && moved.ArtDistance <= ArtAdjudicator.MaxAgreeingDistance,
            "the result carries the actual match evidence");
        check(logs.Any(l => l.Contains("art candidate new 2")) && logs.Any(l => l.Contains("maximum") && l.Contains("minimum")),
            "logs name each measured candidate and both acceptance thresholds");
        check(moved.ReadSet == "misread" && moved.ReadCollectorNumber == "013",
            "moving a printing preserves the original readings");
        var roundTrip = JsonSerializer.Deserialize<IdentifiedCard>(
            JsonSerializer.Serialize(moved, ScanJson.Options), ScanJson.Options);
        check(roundTrip is not null && roundTrip.ArtReason == moved.ArtReason && roundTrip.ArtDistance == moved.ArtDistance,
            "the additive art evidence fields survive result JSON");

        var (shared, _) = await VerifyAsync(card, cropBytes, new Dictionary<string, byte[]>
        {
            ["/old.png"] = referenceBytes, ["/new.png"] = referenceBytes
        });
        check(shared.Set == "old" && shared.ArtReason == "separation_too_small" && shared.ArtVerdict == "inconclusive",
            "identical illustrations never move the printing");

        var (missing, missingLogs) = await VerifyAsync(card, cropBytes, new Dictionary<string, byte[]>
        {
            ["/new.png"] = referenceBytes
        });
        check(missing.Set == "old" && missing.ArtReason == "missing_candidates",
            "a failed competing-image download cannot manufacture a decisive match");
        check(missingLogs.Any(l => l.Contains("HTTP 404")), "failed downloads are explicit in the log");

        var (absent, absentLogs) = await VerifyAsync(card with { ArtReferences = [] }, cropBytes, []);
        check(absent.ArtReason == "no_references" && absent.ArtVerdict is null && absentLogs.Count > 0,
            "missing model-supplied references are reported rather than silently endorsed");
        var (blocked, _) = await VerifyAsync(card with
        {
            ArtReferences = [new("old", "1", "https://untrusted.test/card.png")]
        }, cropBytes, []);
        check(blocked.ArtReason == "no_fetchable_references", "blocked image hosts produce an explicit skipped-check reason");
        var (noImages, _) = await VerifyAsync(card, cropBytes, []);
        check(noImages.ArtReason == "no_images_readable" && noImages.ArtVerdict is null,
            "failure to read every catalogue image does not look like a comparison");
        var (unreadable, _) = await VerifyAsync(card, [1, 2, 3], []);
        check(unreadable.ArtReason == "crop_unreadable", "an undecodable crop reports why comparison was skipped");
        var (tooFar, _) = await VerifyAsync(card, otherBytes, new Dictionary<string, byte[]>
        {
            ["/old.png"] = referenceBytes, ["/new.png"] = referenceBytes
        });
        check(tooFar.ArtReason == "distance_too_large", "poor visual matches report distance, not assumed shared artwork");

        var single = ArtAdjudicator.Adjudicate(("old", "1"), [new("new", "2", 0.05)]);
        check(single.Verdict == ArtVerdict.Inconclusive, "one alternative cannot endorse a different printing");
        var distant = ArtAdjudicator.Adjudicate(("old", "1"), [new("old", "1", 0.45), new("new", "2", 0.53)]);
        check(distant.Verdict == ArtVerdict.Inconclusive, "an unrelated picture still fails the unchanged distance gate");
    }

    public static async Task RunPhotoAsync(Action<bool, string> check, string? trainingDirectory)
    {
        Console.WriteLine("\nReal photograph 01: live-scan boundaries, public catalogue pictures (not persisted)");
        var root = trainingDirectory ?? FindTrainingDirectory();
        var path = Path.Combine(root, "01", "image.jpg");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer-PhotoCheck/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var catalogue = new ScryfallCatalogue(http);
        var crops = new List<byte[]>();
        var cases = new[]
        {
            (Name: "Sword of Hearth and Home", Set: "mh2", Number: "238", Expected: "136",
                Left: .067, Top: .161, Right: .359, Bottom: .392),
            (Name: "Food Chain", Set: "2x2", Number: "147", Expected: "133",
                Left: .347, Top: .159, Right: .651, Bottom: .4),
            (Name: "Emeritus of Woe", Set: "sos", Number: "80", Expected: "80",
                Left: .651, Top: .157, Right: .965, Bottom: .404),
            (Name: "Mystic Remora", Set: "dmr", Number: "420", Expected: "420",
                Left: .063, Top: .392, Right: .381, Bottom: .642),
            (Name: "Ugin's Labyrinth", Set: "mh3", Number: "233", Expected: "233",
                Left: .347, Top: .395, Right: .656, Bottom: .638),
            (Name: "Mavinda, Students' Advocate", Set: "stx", Number: "21", Expected: "21",
                Left: .656, Top: .359, Right: .960, Bottom: .6),
            (Name: "Echocasting Symposium", Set: "sos", Number: "44", Expected: "44",
                Left: .059, Top: .640, Right: .376, Bottom: .890),
            (Name: "Molten-Core Maestro", Set: "sos", Number: "125", Expected: "125",
                Left: .317, Top: .597, Right: .639, Bottom: .85),
            (Name: "Mockingbird", Set: "blb", Number: "61", Expected: "61",
                Left: .643, Top: .6, Right: .973, Bottom: .859)
        };
        foreach (var item in cases)
        {
            using var source = File.OpenRead(path);
            var dimensions = PerspectiveCropper.ReadDimensions(source);
            var quad = new CardQuad([
                new(item.Left * dimensions.Width, item.Top * dimensions.Height),
                new(item.Right * dimensions.Width, item.Top * dimensions.Height),
                new(item.Right * dimensions.Width, item.Bottom * dimensions.Height),
                new(item.Left * dimensions.Width, item.Bottom * dimensions.Height)]);
            using var crop = new MemoryStream();
            PerspectiveCropper.CropToPng(source, quad, crop);
            crops.Add(crop.ToArray());
        }

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var printings = await catalogue.SearchPrintingsAsync(item.Name, null, "en", default);
            var expectedSet = index < 2 ? "tmc" : item.Set;
            check(printings.Any(p => p.Set == expectedSet && p.CollectorNumber == item.Expected),
                $"{item.Name}: expected printing is present in the real catalogue");
            var images = new Dictionary<string, byte[]>();
            foreach (var printing in printings.Where(p => !string.IsNullOrEmpty(p.ImageUrl)).Take(12))
            {
                if (!CardArtVerifier.IsFetchable(printing.ImageUrl))
                    throw new InvalidOperationException($"Unexpected catalogue image host: {printing.ImageUrl}");
                images[new Uri(printing.ImageUrl).AbsolutePath] = await http.GetByteArrayAsync(printing.ImageUrl);
            }
            var card = new IdentifiedCard
            {
                Index = index, Set = item.Set, CollectorNumber = item.Number, Name = item.Name,
                ArtReferences = printings.Where(p => !string.IsNullOrEmpty(p.ImageUrl)).Take(12)
                    .Select(p => new CardArtReference(p.Set, p.CollectorNumber, p.ImageUrl)).ToList()
            };
            var (result, logs) = await VerifyAsync(card, crops[index], images);
            foreach (var line in logs) Console.WriteLine("       " + line);
            check(result.Set == expectedSet && result.CollectorNumber == item.Expected,
                $"{item.Name}: actual photograph returns {expectedSet} {item.Expected}");
            if (index < 2)
            {
                check(result.ArtVerdict == "moved" && result.ArtDistance <= ArtAdjudicator.MaxAgreeingDistance
                    && result.ArtMargin >= ArtAdjudicator.MinSeparation,
                    $"{item.Name}: the move passes both original acceptance thresholds");
            }
            var (unrelated, _) = await VerifyAsync(card, crops[index == 0 ? 1 : 0], images);
            check(unrelated.ArtVerdict == "inconclusive" && unrelated.Set == item.Set,
                $"{item.Name}: an unrelated card's photograph cannot select one of these printings");
            await Task.Delay(150);
        }
    }

    private static string FindTrainingDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "training");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Pass the training directory after --photo-art.");
    }

    private static async Task CheckRunsAsync(Action<bool, string> check, string[] outcomes, int expectedRuns,
        string? expectedError, HttpStatusCode? startFailure = null)
    {
        using var handler = new RunHandler(outcomes, startFailure);
        using var http = new HttpClient(handler);
        var log = new ListLogger<FoundryAgentClient>();
        var client = Client(http, log);
        try
        {
            var reply = await client.RunAsync("agent", "identify", Image(), "catalogue", default);
            check(expectedError is null && reply == "identified", $"{string.Join(", ", outcomes)}: returns the completed reply");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FoundryAccessException)
        {
            check(expectedError is not null && ex.Message.Contains(expectedError, StringComparison.Ordinal),
                $"{string.Join(", ", outcomes)}: terminal failure remains explicit ({expectedError})");
        }
        check(handler.Runs == expectedRuns, $"run attempts bounded at {expectedRuns}");
        check(handler.Threads == 1 && handler.Uploads == 1 && handler.Deletes == 1,
            "retries reuse the thread and upload, with exactly one cleanup");
        if (expectedRuns > 1)
            check(log.Lines.Any(l => l.Contains("retry") || l.Contains("rate limited")), "retry and backoff are logged");
    }

    private static FoundryAgentClient Client(HttpClient http, ILogger<FoundryAgentClient> log) =>
        new(http, new TestCredential(), log, "https://foundry.test", "v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1),
            throttleBackoff: TimeSpan.FromMilliseconds(1));

    private static AgentImage Image() => new(new byte[] { 1, 2, 3 }, "card.png", "image/png");

    private static async Task<(IdentifiedCard Card, List<string> Logs)> VerifyAsync(
        IdentifiedCard card, byte[] crop, Dictionary<string, byte[]> images)
    {
        using var handler = new ImageHandler(images);
        using var http = new HttpClient(handler);
        var log = new ListLogger<CardArtVerifier>();
        var result = await new CardArtVerifier(http, log).VerifyAsync(card, crop, "photo-check", default);
        return (result, log.Lines);
    }

    private static Image<Rgba32> Pattern(int seed)
    {
        var image = new Image<Rgba32>(128, 176);
        var random = new Random(seed);
        var blobs = Enumerable.Range(0, 12)
            .Select(_ => (X: random.NextDouble(), Y: random.NextDouble(), Weight: random.NextDouble() * 2 - 1)).ToArray();
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var fx = (double)x / image.Width;
            var fy = (double)y / image.Height;
            var value = blobs.Sum(b => b.Weight / (0.02 + Math.Pow(fx - b.X, 2) + Math.Pow(fy - b.Y, 2)));
            var level = (byte)Math.Clamp(Math.Tanh(value / 12) * 90 + 128, 0, 255);
            image[x, y] = new Rgba32(level, level, level);
        }
        return image;
    }

    private static byte[] Png(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RunHandler(string[] outcomes, HttpStatusCode? startFailure = null) : HttpMessageHandler
    {
        public int Runs { get; private set; }
        public int Threads { get; private set; }
        public int Uploads { get; private set; }
        public int Deletes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (request.Method == HttpMethod.Delete) { Deletes++; json = "{}"; }
            else if (path == "/files") { Uploads++; json = """{"id":"file-1"}"""; }
            else if (path == "/threads") { Threads++; json = """{"id":"thread-1"}"""; }
            else if (path == "/threads/thread-1/runs")
            {
                Runs++;
                if (startFailure is not null)
                    return Task.FromResult(new HttpResponseMessage(startFailure.Value) { Content = new StringContent("{}") });
                json = $$"""{"id":"run-{{Runs}}"}""";
            }
            else if (path.StartsWith("/threads/thread-1/runs/run-", StringComparison.Ordinal))
            {
                var outcome = outcomes[Math.Min(Runs - 1, outcomes.Length - 1)];
                json = outcome == "completed" ? """{"status":"completed"}"""
                    : $$$"""{"status":"failed","last_error":{"code":"{{{outcome}}}","message":"test failure"}}""";
            }
            else if (path == "/threads/thread-1/messages")
                json = """{"data":[{"role":"assistant","content":[{"text":{"value":"identified"}}]}]}""";
            else throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class ImageHandler(Dictionary<string, byte[]> images) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(images.TryGetValue(request.RequestUri!.AbsolutePath, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public Action<string>? OnLog { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);
            OnLog?.Invoke(line);
        }
    }
}

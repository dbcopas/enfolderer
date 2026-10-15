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
        Console.WriteLine("\nAuthoritative measured geometry");
        static CardQuad Box(double x) => new([new(x, 0), new(x + 100, 0), new(x + 100, 140), new(x, 140)]);
        var first = Box(0);
        var second = Box(200);
        DetectedCard[] measured = [new(first, 0.9), new(second, 0.8)];
        var rotated = new CardQuad([new(101, 141), new(1, 141), new(1, 1), new(101, 1)]);
        var kept = FoundryCardBoundaryAgent.PreserveMeasuredRegions(measured,
            [new(rotated, 0.1, CardGames.Magic), new(Box(1000), 1, CardGames.Pokemon)]);
        check(kept.Count == 2 && kept[1].Quad == second && kept[1].GameHint == CardGames.Unknown,
            "agent omission cannot drop a measured region; extra estimates cannot add one");
        check(kept[0].Quad.Points.SequenceEqual([first.Points[2], first.Points[3], first.Points[0], first.Points[1]])
            && kept[0].GameHint == CardGames.Magic && kept[0].Confidence == 0.9,
            "orientation and game enrich exact measured corners without moving them or replacing geometric confidence");
        var empty = FoundryCardBoundaryAgent.PreserveMeasuredRegions(measured, []);
        check(empty.Count == 2 && empty.All(b => b.GameHint == CardGames.Unknown),
            "an empty agent list retains all measured candidates");
        var duplicate = FoundryCardBoundaryAgent.PreserveMeasuredRegions(measured,
            [new(first, 1, CardGames.Magic), new(first, 1, CardGames.Pokemon)]);
        check(duplicate.Count == 2 && duplicate[0].GameHint == CardGames.Unknown && duplicate[0].Quad == first,
            "duplicate agent matches do not guess orientation or game metadata");

        await LanguageChecksAsync(check);
        await FinishChecksAsync(check);
        Console.WriteLine("\nResolution-specific diagnostic explanations");
        var ambiguousExplanation = ScanJobProcessor.DescribeResolution(new IdentifiedCard
        {
            Resolution = "ambiguous", ReadSet = "LTR", ReadCollectorNumber = "90"
        });
        check(ambiguousExplanation.Contains("multiple printings")
            && !ambiguousExplanation.Contains("no set") && !ambiguousExplanation.Contains("default"),
            "ambiguous printings explain non-distinguishing readings, never falsely claiming no set was read");
        var assumedExplanation = ScanJobProcessor.DescribeResolution(new IdentifiedCard { Resolution = "assumed" });
        check(assumedExplanation.Contains("number lookup") && assumedExplanation.Contains("not corroborated"),
            "assumed printings explain uncorroborated identity, not a name-based catalogue default");
        check(ScanJobProcessor.DescribeResolution(new IdentifiedCard { Resolution = "unplaced" }).Contains("catalogue default"),
            "unplaced alone explains a name-based catalogue default");
        check(ScanJobProcessor.DescribeResolution(new IdentifiedCard { Resolution = "relocated" }).Contains("outside the read set"),
            "relocation explains that the read set was replaced");
        check(ScanJobProcessor.DescribeResolution(new IdentifiedCard { Resolution = "corrected" }).Contains("name won"),
            "correction explains why the exported number differs from the reading");
        check(ScanJobProcessor.DescribeResolution(new IdentifiedCard { Resolution = "unresolved" }).Contains("not a confirmed printing"),
            "unresolved readings do not imply catalogue confirmation");
        Console.WriteLine("\nRegeneration of malformed completed replies");
        using (var handler = new RunHandler(["completed"], replies: ["{\"cards\":[", "{\"cards\":[]}"]))
        using (var http = new HttpClient(handler))
        {
            var logger = new ListLogger<FoundryAgentClient>();
            var reply = await Client(http, logger).RunAsync("agent", "locate", Image(), "imaging", default,
                validateReply: text => FoundryCardBoundaryAgent.ParseBoundaries(text, 100, 200));
            check(reply == "{\"cards\":[]}" && handler.Runs == 2 && handler.Corrections == 1,
                "a malformed completed boundary reply is regenerated once, never patched");
            check(handler.Uploads == 1 && handler.Deletes == 1 && handler.Threads == 1,
                "JSON regeneration reuses original images and cleans them once");
            check(logger.Lines.Any(l => l.Contains("invalid reply") && l.Contains("run-1")),
                "invalid reply diagnostics include the run and thread");
        }
        using (var handler = new RunHandler(["completed"], replies: ["{\"cards\":[", "{\"cards\":["]))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http, new ListLogger<FoundryAgentClient>())
                    .RunAsync("agent", "locate", Image(), "imaging", default,
                        validateReply: text => FoundryCardBoundaryAgent.ParseBoundaries(text, 100, 200));
                check(false, "persistent malformed JSON must fail");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message.Contains("after 2 attempts") && handler.Runs == 2 && handler.Deletes == 1,
                    "persistent malformed JSON fails explicitly after exactly two attempts");
            }
        }
        using (var handler = new RunHandler(["completed"], replies: ["{\"cards\":[]}"], splitReply: true))
        using (var http = new HttpClient(handler))
        {
            var reply = await Client(http, new ListLogger<FoundryAgentClient>())
                .RunAsync("agent", "locate", Image(), "imaging", default,
                    validateReply: text => FoundryCardBoundaryAgent.ParseBoundaries(text, 100, 200));
            check(reply == "{\"cards\":[]}" && handler.Runs == 1,
                "all text parts of the current run are combined, ignoring stale assistant messages");
        }
        Console.WriteLine("\nBounded retries of terminal Foundry failures");
        check(FoundryAgentClient.IsServerError("failed", "SERVER_ERROR"), "terminal server errors are retryable");
        check(!FoundryAgentClient.IsServerError("cancelled", "server_error"), "cancellation is not retried");
        check(!FoundryAgentClient.IsServerError("failed", "invalid_request"), "invalid requests are not retried");

        await CheckRunsAsync(check, ["server_error", "completed"], 2, null);
        await CheckRunsAsync(check, ["server_error"], 3, "2 server_error retries were exhausted");
        await CheckRunsAsync(check, ["invalid_request"], 1, "invalid_request");
        await CheckRunsAsync(check, ["incomplete"], 1, "max_completion_tokens");
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

        static async Task FinishChecksAsync(Action<bool, string> check)
        {
            Console.WriteLine("\nFinish determined from the printing marker");
            var crop = new CardCrop(0, Image(),
                new CardQuad([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]), CardGames.Magic);
            IdentifiedCard Parse(string fields, string game = CardGames.Magic) =>
                FoundryCardIdentificationAgent.ParseIdentification(
                    "{\"set\":\"sos\",\"collectorNumber\":\"80\",\"name\":\"Card\"," + fields + "}",
                    crop, game, "test-agent");
            var star = Parse("\"finishMarker\":\"star\",\"finishConfidence\":0.9,\"finish\":\"nonfoil\"");
            check(star.Finish == "foil" && star.FinishEvidence == "set_language_marker",
                "a reliable star overrules a contradictory model finish");
            var dot = Parse("\"finishMarker\":\"dot\",\"finishConfidence\":0.8,\"finish\":\"foil\"");
            check(dot.Finish == "nonfoil", "a reliable dot means nonfoil at the confidence boundary");
            var blurred = Parse("\"finishMarker\":\"star\",\"finishConfidence\":0.79,\"finish\":\"foil\"");
            check(blurred.Finish is null && blurred.FinishEvidence == "unknown",
                "a blurred marker below the confidence gate remains unknown");
            check(Parse("\"finishMarker\":\"unknown\",\"finishConfidence\":1,\"finish\":\"nonfoil\"").Finish is null,
                "an unreadable marker cannot silently establish nonfoil");
            check(Parse("\"finishMarker\":\"absent\",\"finishConfidence\":1,\"finish\":\"nonfoil\"").Finish is null,
                "a frame without this marker cannot establish nonfoil");
            check(Parse("\"finish\":\"nonfoil\"").Finish is null, "legacy MTG replies without finish evidence remain unknown");
            check(Parse("\"finishMarker\":\"star\",\"finishConfidence\":\"high\"").Finish is null,
                "invalid confidence cannot endorse a finish");
            check(Parse("\"finishMarker\":\"star\",\"finishConfidence\":0.9,\"finish\":\"etched\"").Finish == "foil",
                "a star alone does not claim an etched finish");
            var old = Parse("\"finishMarker\":\"absent\",\"finishEvidence\":\"visual\",\"finishConfidence\":0.9,\"finish\":\"foil\"");
            check(old.Finish == "foil" && old.FinishEvidence == "visual",
                "clearly visible foil on an older frame retains separately labelled visual evidence");
            var shootingStar = Parse("\"finishMarker\":\"absent\",\"finishEvidence\":\"shooting_star_watermark\",\"finishConfidence\":0.8,\"finish\":\"nonfoil\"");
            check(shootingStar.Finish == "foil" && shootingStar.FinishEvidence == "shooting_star_watermark"
                && shootingStar.FinishMarker == "absent",
                "an old-frame shooting-star watermark establishes foil without a footer marker or sheen");
            check(Parse("\"finishMarker\":\"absent\",\"finishEvidence\":\"shooting_star_watermark\",\"finishConfidence\":0.9,\"finish\":\"etched\"").Finish == "foil",
                "a shooting-star watermark does not establish etched foil");
            check(Parse("\"finishMarker\":\"absent\",\"finishEvidence\":\"shooting_star_watermark\",\"finishConfidence\":0.79,\"finish\":\"foil\"").Finish is null,
                "an uncertain shooting-star watermark remains unknown");
            check(Parse("\"finishMarker\":\"unknown\",\"finishEvidence\":\"shooting_star_watermark\",\"finishConfidence\":1,\"finish\":\"foil\"").Finish is null,
                "shooting-star evidence requires an explicitly absent footer marker");
            var oldRoundTrip = JsonSerializer.Deserialize<IdentifiedCard>(
                JsonSerializer.Serialize(shootingStar, ScanJson.Options), ScanJson.Options);
            check(oldRoundTrip?.FinishEvidence == "shooting_star_watermark" && oldRoundTrip.Finish == "foil",
                "shooting-star evidence survives result JSON");
            check(Parse("\"finish\":\"foil\"", CardGames.Pokemon).Finish == "foil",
                "non-MTG finish parsing is unchanged");
            var roundTrip = JsonSerializer.Deserialize<IdentifiedCard>(JsonSerializer.Serialize(star, ScanJson.Options), ScanJson.Options);
            check(roundTrip is not null && roundTrip.Finish == "foil" && roundTrip.FinishMarker == "star"
                && roundTrip.FinishConfidence == 0.9, "finish evidence survives result JSON");

            using var image = new Image<Rgba32>(100, 200, new Rgba32(255, 0, 0));
            for (var y = 150; y < 200; y++)
            for (var x = 0; x < 65; x++) image[x, y] = new Rgba32(0, 255, 0);
            var footerBytes = CardFooterCropper.CropToPng(Png(image));
            using var footer = SixLabors.ImageSharp.Image.Load<Rgba32>(footerBytes);
            check(footer.Width == 1200 && footer.Height == 600 && footer[0, 0].G == 255
                && footer[1199, 599].R == 255, "full-width footer preserves the retro-frame collector-number area");
            image.Mutate(c => c.Rotate(RotateMode.Rotate90));
            using var sidewaysFooter = SixLabors.ImageSharp.Image.Load<Rgba32>(CardFooterCropper.CropToPng(Png(image)));
            check(sidewaysFooter.Width == 1200 && sidewaysFooter.Height == 1200
                && (sidewaysFooter[0, 600].G == 255 || sidewaysFooter[1199, 599].G == 255),
                "sideways cards supply both quarter-turn footer views without guessing printed orientation");

            using var handler = new RunHandler(["server_error", "completed"]);
            using var http = new HttpClient(handler);
            var reply = await Client(http, new ListLogger<FoundryAgentClient>())
                .RunAsync("agent", "identify", Image(), "catalogue", default,
                    new AgentImage(footerBytes, "footer.png", "image/png"));
            check(reply == "identified" && handler.Uploads == 2 && handler.Deletes == 2 && handler.Threads == 1,
                "full card and detail are uploaded once, reused on retries, and both cleaned up");
            check(handler.ImageParts == 2, "both images reach the same identification message");
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

    public static async Task RunBoundaryPhotosAsync(Action<bool, string> check, string? trainingDirectory)
    {
        Console.WriteLine("\nReal photographs 01-06: measured regions survive empty agent responses");
        var root = trainingDirectory ?? FindTrainingDirectory();
        var total = 0;
        for (var page = 1; page <= 6; page++)
        {
            var directory = Path.Combine(root, page.ToString("00"));
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "image.jpg"));
            using var source = new MemoryStream(bytes);
            var dimensions = PerspectiveCropper.ReadDimensions(source);
            var expected = File.ReadAllLines(Path.Combine(directory, "cards.txt"))
                .Count(line => !string.IsNullOrWhiteSpace(line));
            using var handler = new RunHandler(["completed"], replies: ["{\"cards\":[]}"]);
            using var http = new HttpClient(handler);
            var log = new ListLogger<FoundryCardBoundaryAgent>();
            var agent = new FoundryCardBoundaryAgent(Client(http, new ListLogger<FoundryAgentClient>()), "agent", log);
            var boundaries = await agent.DetectAsync(new AgentImage(bytes, "image.jpg", "image/jpeg"),
                "scans/test/image.jpg", dimensions.Width, dimensions.Height);
            check(boundaries.Count == expected && boundaries.All(b => b.Quad.IsValid),
                $"page {page:00}: {boundaries.Count}/{expected} measured regions reach the real boundary adapter");
            check(log.Lines.Any(l => l.Contains("Measured regions are authoritative")),
                $"page {page:00}: omitted metadata is explicitly logged");
            total += boundaries.Count;
        }
        check(total == 47, $"all six photos retain 47/47 measured candidates (actual {total})");
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
        var measuredCrops = new List<byte[]>();
        using var measurementSource = File.OpenRead(path);
        var measuredCards = CardDetector.Detect(measurementSource);
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
            var nearest = measuredCards.MinBy(card =>
            {
                var box = card.Quad.BoundingBox();
                return Math.Pow(box.X + box.Width / 2 - (item.Left + item.Right) / 2 * dimensions.Width, 2)
                    + Math.Pow(box.Y + box.Height / 2 - (item.Top + item.Bottom) / 2 * dimensions.Height, 2);
            }) ?? throw new InvalidOperationException("No measured region for the page 01 annotated card.");
            var authoritative = FoundryCardBoundaryAgent.PreserveMeasuredRegions([nearest],
                [new DetectedBoundary(quad, 1, CardGames.Magic)])[0];
            source.Position = 0;
            using var measuredCrop = new MemoryStream();
            PerspectiveCropper.CropToPng(source, authoritative.Quad, measuredCrop);
            measuredCrops.Add(measuredCrop.ToArray());
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
            var (measuredResult, measuredLogs) = await VerifyAsync(card, measuredCrops[index], images);
            if (measuredResult.Set != expectedSet || measuredResult.CollectorNumber != item.Expected)
            {
                foreach (var line in measuredLogs) Console.WriteLine("       [measured] " + line);
                Console.WriteLine($"       [measuredResult] Set={measuredResult.Set} Number={measuredResult.CollectorNumber} Verdict={measuredResult.ArtVerdict}");
            }
            check(measuredResult.Set == expectedSet && measuredResult.CollectorNumber == item.Expected,
                $"{item.Name}: authoritative measured crop preserves {expectedSet} {item.Expected}");
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

    public static async Task RunMultilingualPhotoAsync(Action<bool, string> check, string? trainingDirectory)
    {
        var root = trainingDirectory ?? FindTrainingDirectory();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer-PhotoCheck/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var catalogue = new ScryfallCatalogue(http);
        Console.WriteLine("\nSideways Japanese retro-frame photographs (page 05, authoritative measured crops)");
        var bounds = new[]
        {
            (.142, .063, .361, .273), (.419, .044, .619, .234), (.647, .025, .890, .257),
            (.171, .304, .366, .492), (.428, .304, .623, .492), (.700, .285, .895, .473),
            (.176, .552, .371, .739), (.433, .552, .628, .739), (.657, .546, .895, .771),
            (.176, .781, .371, .968), (.423, .781, .628, .974), (.681, .781, .895, .984)
        };
        using var photo = File.OpenRead(Path.Combine(root, "05", "image.jpg"));
        var dimensions = PerspectiveCropper.ReadDimensions(photo);
        photo.Position = 0;
        var measured = CardDetector.Detect(photo).ToList();
        check(measured.Count == bounds.Length, "page 05 has exactly twelve measured candidates");
        if (measured.Count != bounds.Length) return;
        var quads = new List<CardQuad>();
        foreach (var (left, top, right, bottom) in bounds)
        {
            // The historical boxes locate annotated identities; only measured corners reach the verifier.
            var nearest = measured.MinBy(card =>
            {
                var box = card.Quad.BoundingBox();
                return Math.Pow((box.X + box.Width / 2) / dimensions.Width - (left + right) / 2, 2)
                    + Math.Pow((box.Y + box.Height / 2) / dimensions.Height - (top + bottom) / 2, 2);
            }) ?? throw new InvalidOperationException("No measured region remains for the annotated card.");
            quads.Add(nearest.Quad);
            measured.Remove(nearest);
        }
        var cases = new[]
        {
            (Name: "Polluted Delta", Set: "mh3", Number: "438", OldSet: "mh3", OldNumber: "224"),
            (Name: "Misty Rainforest", Set: "mh2", Number: "438", OldSet: "spg", OldNumber: "111"),
            (Name: "Scalding Tarn", Set: "mh2", Number: "439", OldSet: "spg", OldNumber: "112")
        };
        for (var kind = 0; kind < cases.Length; kind++)
        {
            var item = cases[kind];
            var printings = await catalogue.SearchPrintingsAsync(item.Name, null, "ja", default);
            var images = new Dictionary<string, byte[]>();
            foreach (var printing in printings.Where(p => !string.IsNullOrEmpty(p.ImageUrl)).Take(12))
            {
                if (!CardArtVerifier.IsFetchable(printing.ImageUrl)) throw new InvalidOperationException("Unexpected image host.");
                images[new Uri(printing.ImageUrl).AbsolutePath] = await http.GetByteArrayAsync(printing.ImageUrl);
            }
            for (var row = 0; row < 4; row++)
            {
                var index = row * 3 + kind;
                using var source = File.OpenRead(Path.Combine(root, "05", "image.jpg"));
                using var crop = new MemoryStream();
                PerspectiveCropper.CropToPng(source, quads[index], crop);
                var card = new IdentifiedCard
                {
                    Index = index, Name = item.Name, Set = item.Set, CollectorNumber = item.Number,
                    Language = "ja", Resolution = "confirmed",
                    ArtReferences = printings.Where(p => !string.IsNullOrEmpty(p.ImageUrl)).Take(12)
                        .Select(p => new CardArtReference(p.Set, p.CollectorNumber, p.ImageUrl)).ToList()
                };
                var (result, logs) = await VerifyAsync(card, crop.ToArray(), images);
                foreach (var log in logs) Console.WriteLine("       " + log);
                check(result.Set == item.Set && result.CollectorNumber == item.Number && result.Language == "ja",
                    $"page 05 card {index} {item.Name}: preserved {item.Set} {item.Number}, ja "
                    + $"({result.ArtVerdict}/{result.ArtReason})");
            }
            await Task.Delay(150);
        }
    }

    private static async Task LanguageChecksAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nPhotographed language, independent of catalogue translation");
        using var image = new Image<Rgba32>(100, 140);
        var crop = new CardCrop(1, new AgentImage(Png(image), "card.png", "image/png"),
            new CardQuad([new(0, 0), new(100, 0), new(100, 140), new(0, 140)]), CardGames.Magic);
        string Reply(string? language, string? readName, string? readLanguage) =>
            JsonSerializer.Serialize(new
            {
                set = "clb", collectorNumber = "240", name = "Majestic Genesis",
                language, readName, readLanguage, resolution = "confirmed", confidence = 1
            });
        IdentifiedCard Parse(string text, string game = CardGames.Magic) =>
            FoundryCardIdentificationAgent.ParseIdentification(text, crop, game, "test");
        bool Rejects(string text)
        {
            try
            {
                FoundryCardIdentificationAgent.ValidateLanguage(Parse(text));
                return false;
            }
            catch (InvalidOperationException) { return true; }
        }
        const string japaneseTitle = "\u58ee\u9e97\u306a\u308b\u5275\u9020";
        var valid = Reply("ja", japaneseTitle, " JP ");
        var japanese = Parse(valid);
        FoundryCardIdentificationAgent.ValidateLanguage(japanese);
        check(japanese.Name == "Majestic Genesis" && japanese.Language == "ja"
            && japanese.ReadName == japaneseTitle && japanese.ReadLanguage == "ja",
            "Japanese title/footer coexist with the catalogue's English name; JP normalizes to ja");
        check(Parse(Reply(" JPN ", japaneseTitle, "jpn")).Language == "ja",
            "a Japanese language alias is normalized in the final answer too");
        check(Rejects(Reply("en", japaneseTitle, "JP")),
            "a Japanese footer cannot be silently overwritten by English catalogue language");
        var wrong = Reply("en", japaneseTitle, null);
        check(Rejects(wrong), "Japanese kana reject English even when the footer language is unreadable");
        check(Parse(Reply(null, japaneseTitle, null)).Language is null
            && Rejects(Reply(null, japaneseTitle, null)) && Rejects(Reply("", japaneseTitle, null)),
            "missing or empty Magic language never defaults to en and requires re-reading");
        const string legacy = """{"set":"clb","collectorNumber":"240","name":"Majestic Genesis"}""";
        check(Parse(legacy).Language is null && Rejects(legacy),
            "legacy replies omitting the language property cannot silently become English");
        check(Rejects(Reply("ja", null, "JP")), "an identified Magic reply must retain the original title");
        check(Rejects(Reply("en", "Vitalit\u00e4tskraft", "DE")),
            "a German footer also rejects contradictory English language");
        FoundryCardIdentificationAgent.ValidateLanguage(Parse(Reply("en", "Majestic Genesis", "EN")));
        FoundryCardIdentificationAgent.ValidateLanguage(Parse(Reply("de", "Vitalit\u00e4tskraft", "DE")));
        FoundryCardIdentificationAgent.ValidateLanguage(Parse(Reply("ja", japaneseTitle, null)));
        FoundryCardIdentificationAgent.ValidateLanguage(Parse(Reply("zhs", "\u95ea\u7535", null)));
        check(true, "English, German, Japanese without a legible footer, and Chinese titles remain valid");
        FoundryCardIdentificationAgent.ValidateLanguage(Parse("""{"error":"Unreadable card"}"""));
        check(true, "an unreadable card is not retried for missing language evidence");
        check(Parse(Reply(null, null, null), CardGames.Pokemon).Language == "en",
            "non-MTG legacy language parsing is unchanged");
        var roundTrip = JsonSerializer.Deserialize<IdentifiedCard>(
            JsonSerializer.Serialize(japanese, ScanJson.Options), ScanJson.Options);
        check(roundTrip?.ReadName == japaneseTitle && roundTrip.ReadLanguage == "ja",
            "photographed title and footer language survive result JSON");

        using (var handler = new RunHandler(["completed"], replies: [wrong, valid]))
        using (var http = new HttpClient(handler))
        {
            var log = new ListLogger<FoundryAgentClient>();
            var agent = new FoundryCardIdentificationAgent(Client(http, log), GameAgentProfile.Mtg, "agent",
                new ListLogger<FoundryCardIdentificationAgent>());
            var result = await agent.IdentifyAsync(crop);
            check(result.Language == "ja" && result.ReadName == japaneseTitle
                && handler.Runs == 2 && handler.Corrections == 1,
                "the real identification adapter retries a contradictory language and accepts the corrected reply");
            check(handler.Uploads == 2 && handler.Deletes == 2 && handler.Threads == 1
                && log.Lines.Any(l => l.Contains("invalid reply")),
                "language retry reuses both images, logs the contradiction, and cleans up exactly once");
        }
        using (var handler = new RunHandler(["completed"], replies: [wrong]))
        using (var http = new HttpClient(handler))
        {
            var agent = new FoundryCardIdentificationAgent(Client(http, new ListLogger<FoundryAgentClient>()),
                GameAgentProfile.Mtg, "agent", new ListLogger<FoundryCardIdentificationAgent>());
            try
            {
                await agent.IdentifyAsync(crop);
                check(false, "persistent language contradictions must fail explicitly");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message.Contains("after 2 attempts") && handler.Runs == 2
                    && handler.Corrections == 1 && handler.Deletes == 2,
                    "persistent language contradictions fail after one retry, never returning false English success");
            }
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

    private sealed class RunHandler(string[] outcomes, HttpStatusCode? startFailure = null,
        string[]? replies = null, bool splitReply = false) : HttpMessageHandler
    {
        public int Runs { get; private set; }
        public int Threads { get; private set; }
        public int Uploads { get; private set; }
        public int Deletes { get; private set; }
        public int ImageParts { get; private set; }
        public int Corrections { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (request.Method == HttpMethod.Delete) { Deletes++; json = "{}"; }
            else if (path == "/files") { Uploads++; json = """{"id":"file-1"}"""; }
            else if (path == "/threads")
            {
                Threads++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                ImageParts = body.RootElement.GetProperty("messages")[0].GetProperty("content")
                    .EnumerateArray().Count(p => p.GetProperty("type").GetString() == "image_file");
                json = """{"id":"thread-1"}""";
            }
            else if (path == "/threads/thread-1/runs")
            {
                Runs++;
                if (startFailure is not null)
                    return new HttpResponseMessage(startFailure.Value) { Content = new StringContent("{}") };
                json = $$"""{"id":"run-{{Runs}}"}""";
            }
            else if (path.StartsWith("/threads/thread-1/runs/run-", StringComparison.Ordinal))
            {
                var outcome = outcomes[Math.Min(Runs - 1, outcomes.Length - 1)];
                json = outcome == "completed" ? """{"status":"completed"}"""
                    : outcome == "incomplete" ? """{"status":"incomplete","incomplete_details":{"reason":"max_completion_tokens"}}"""
                    : $$$"""{"status":"failed","last_error":{"code":"{{{outcome}}}","message":"test failure"}}""";
            }
            else if (path == "/threads/thread-1/messages" && request.Method == HttpMethod.Post)
            {
                Corrections++;
                json = "{}";
            }
            else if (path == "/threads/thread-1/messages")
            {
                var reply = replies is null ? "identified" : replies[Math.Min(Runs - 1, replies.Length - 1)];
                var parts = splitReply ? new[] { reply[..(reply.Length / 2)], reply[(reply.Length / 2)..] } : [reply];
                json = JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new { role = "assistant", run_id = "stale-run", content = new[] { new { text = new { value = "stale" } } } },
                        new { role = "assistant", run_id = $"run-{Runs}", content = parts.Select(p => new { text = new { value = p } }).ToArray() }
                    }
                });
            }
            else throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

using Azure.Identity;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Worker.Agents;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class LiveReadingCheck
{
    public static async Task<int> RunBoundaryAsync(string endpoint, string agentId, string directory, string blobPath,
        string? outputDirectory = null)
    {
        var listing = Path.Combine(directory, "cards.txt");
        using var source = File.OpenRead(Path.Combine(directory, "image.jpg"));
        var size = PerspectiveCropper.ReadDimensions(source);
        source.Position = 0;
        var expectedRegions = File.Exists(Path.Combine(directory, "boundaries.json"))
            ? FoundryCardBoundaryAgent.ParseBoundaries(await File.ReadAllTextAsync(Path.Combine(directory, "boundaries.json")),
                size.Width, size.Height) : null;
        var expectedCount = expectedRegions?.Count ?? File.ReadAllLines(listing).Count(l => !string.IsNullOrWhiteSpace(l));
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes);
        using var http = new HttpClient();
        using var logs = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var client = new FoundryAgentClient(http, new AzureCliCredential(), logs.CreateLogger<FoundryAgentClient>(),
            endpoint, "2025-11-15-preview", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), useResponses: true);
        var agent = new FoundryCardBoundaryAgent(client, agentId, logs.CreateLogger<FoundryCardBoundaryAgent>());
        var boundaries = await agent.DetectAsync(new AgentImage(bytes.ToArray(), "image.jpg", "image/jpeg"),
            blobPath, size.Width, size.Height);
        Console.WriteLine($"Live boundary adapter check: {boundaries.Count}/{expectedCount} retained candidates; valid quads.");
        var matched = expectedRegions is null ? expectedCount : expectedRegions.Count(expected =>
            boundaries.Count(actual => Overlap(expected.Quad, actual.Quad) > 0.75) == 1);
        Console.WriteLine($"Whole-card geometry: {matched}/{expectedCount} match annotated physical-card regions.");
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "boundaries.json"), JsonSerializer.Serialize(new
            {
                cards = boundaries.Select(b => new
                {
                    quad = new { points = b.Quad.Points.Select(p => new { x = p.X, y = p.Y }) },
                    confidence = b.Confidence, gameHint = b.GameHint
                })
            }));
            for (var i = 0; i < boundaries.Count; i++)
            {
                source.Position = 0;
                using var crop = File.Create(Path.Combine(outputDirectory, $"card-{i:D2}.png"));
                PerspectiveCropper.CropToPng(source, boundaries[i].Quad, crop);
            }
        }
        return boundaries.Count == expectedCount && matched == expectedCount ? 0 : 1;
    }

    private static double Overlap(CardQuad first, CardQuad second)
    {
        var a = first.BoundingBox();
        var b = second.BoundingBox();
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }

    public static async Task<int> RunAsync(string endpoint, string agentId, string directory, string indices)
    {
        var selected = indices.Split(',').Select(int.Parse).ToHashSet();
        var expected = File.ReadAllLines(Path.Combine(directory, "cards.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Split(';')).ToList();
        using var source = File.OpenRead(Path.Combine(directory, "image.jpg"));
        var boundaries = CardDetector.Detect(source);
        source.Position = 0;
        if (selected.Any(i => i < 0 || i >= boundaries.Count))
            throw new ArgumentOutOfRangeException(nameof(indices), "Indices must refer to detected cards.");
        using var http = new HttpClient();
        using var logs = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var client = new FoundryAgentClient(http, new AzureCliCredential(), logs.CreateLogger<FoundryAgentClient>(),
            endpoint, "2025-11-15-preview", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), useResponses: true);
        using var pricingHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var agent = new FoundryCardIdentificationAgent(client, GameAgentProfile.Mtg, agentId,
            logs.CreateLogger<FoundryCardIdentificationAgent>(),
            new Enfolderer.Ai.Infrastructure.Catalogue.ScryfallPriceClient(pricingHttp));
        var verifier = new CardArtVerifier(http, logs.CreateLogger<CardArtVerifier>());
        var failures = 0;
        foreach (var index in selected.Order())
        {
            source.Position = 0;
            using var crop = new MemoryStream();
            var quad = boundaries[index].Quad;
            PerspectiveCropper.CropToPng(source, quad, crop, marginFraction: 0.02);
            var cardCrop = new CardCrop(index,
                new AgentImage(crop.ToArray(), $"card-{index}.png", "image/png"), quad, CardGames.Magic);
            var card = await agent.IdentifyAsync(cardCrop);
            source.Position = 0;
            using var artCrop = new MemoryStream();
            PerspectiveCropper.CropToPng(source, quad, artCrop);
            card = await verifier.VerifyAndReviewAsync(card, artCrop.ToArray(), "live-reading", cardCrop, agent, default);
            var f = expected[index];
            var matches =
                string.Equals(f[1], card.Name?.Split("//")[0].Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[2], card.Set, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[3], card.CollectorNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[5] == "jp" ? "ja" : f[5], card.Language, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"{(matches ? "ok" : "FAIL")} live card {index}: "
                + $"{card.Set};{card.CollectorNumber};{card.Language};{card.Name}; resolution={card.Resolution}; "
                + $"printingPlaced={card.PrintingWasPlaced}; "
                + $"readSet={card.ReadSet}; readNumber={card.ReadCollectorNumber}; "
                + $"readName={card.ReadName}; readLanguage={card.ReadLanguage}; error={card.Error}");
            if (!matches) failures++;
        }
        return failures == 0 ? 0 : 1;
    }
}

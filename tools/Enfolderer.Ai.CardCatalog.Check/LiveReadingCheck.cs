using Azure.Identity;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class LiveReadingCheck
{
    public static async Task<int> RunBoundaryAsync(string endpoint, string agentId, string directory, string blobPath)
    {
        var expectedCount = File.ReadAllLines(Path.Combine(directory, "cards.txt"))
            .Count(l => !string.IsNullOrWhiteSpace(l));
        using var source = File.OpenRead(Path.Combine(directory, "image.jpg"));
        var size = PerspectiveCropper.ReadDimensions(source);
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes);
        using var http = new HttpClient();
        using var logs = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var client = new FoundryAgentClient(http, new AzureCliCredential(), logs.CreateLogger<FoundryAgentClient>(),
            endpoint, "v1", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
        var agent = new FoundryCardBoundaryAgent(client, agentId, logs.CreateLogger<FoundryCardBoundaryAgent>());
        var boundaries = await agent.DetectAsync(new AgentImage(bytes.ToArray(), "image.jpg", "image/jpeg"),
            blobPath, size.Width, size.Height);
        Console.WriteLine($"Live boundary adapter check: {boundaries.Count}/{expectedCount} retained candidates; valid quads.");
        return boundaries.Count == expectedCount ? 0 : 1;
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
            endpoint, "v1", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
        var agent = new FoundryCardIdentificationAgent(client, GameAgentProfile.Mtg, agentId,
            logs.CreateLogger<FoundryCardIdentificationAgent>());
        var failures = 0;
        foreach (var index in selected.Order())
        {
            source.Position = 0;
            using var crop = new MemoryStream();
            var quad = boundaries[index].Quad;
            PerspectiveCropper.CropToPng(source, quad, crop);
            var card = await agent.IdentifyAsync(new CardCrop(index,
                new AgentImage(crop.ToArray(), $"card-{index}.png", "image/png"), quad, CardGames.Magic));
            var matches = expected.Any(f =>
                string.Equals(f[1], card.Name?.Split("//")[0].Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[2], card.Set, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[3], card.CollectorNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f[5] == "jp" ? "ja" : f[5], card.Language, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{(matches && card.PrintingWasPlaced ? "ok" : "FAIL")} live card {index}: "
                + $"{card.Set};{card.CollectorNumber};{card.Language};{card.Name}; resolution={card.Resolution}; "
                + $"readSet={card.ReadSet}; readNumber={card.ReadCollectorNumber}; "
                + $"readName={card.ReadName}; readLanguage={card.ReadLanguage}; error={card.Error}");
            if (!matches || !card.PrintingWasPlaced) failures++;
        }
        return failures == 0 ? 0 : 1;
    }
}

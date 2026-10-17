using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Worker.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using Enfolderer.Ai.Worker.Agents;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class LegacyPrintingChecks
{
    public static async Task<int> RunAsync(string cropPath)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Enfolderer-PrintingCheck/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var catalogue = new ScryfallCatalogue(http);
        var candidates = await catalogue.SearchPrintingsAsync("Mana Crypt", null, "en", default);
        if (candidates.Any(c => c.Set is "me2" or "vma" or "prm"))
            throw new InvalidOperationException("Digital-only printings entered a physical-card comparison.");
        using var logs = LoggerFactory.Create(b => b.AddConsole());
        var tools = new MtgCatalogueTools(catalogue, logs.CreateLogger<MtgCatalogueTools>());
        var cropBytes = await File.ReadAllBytesAsync(cropPath);
        var crop = new CardCrop(0, new AgentImage(cropBytes, "card.png", "image/png"),
            new CardQuad([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]), CardGames.Magic);
        var reading = FoundryCardIdentificationAgent.ParseIdentification(
            await tools.ResolvePrintingAsync("Mana Crypt", "", language: "en"), crop, CardGames.Magic, "test");
        if (!reading.ArtCandidatesComplete || !reading.ArtReferences.Any(r => r.Set == "phpr"))
            throw new InvalidOperationException("The catalogue-to-worker candidate path omitted the original promo.");
        var verifier = new CardArtVerifier(http, logs.CreateLogger<CardArtVerifier>());
        var result = await verifier.VerifyAsync(reading, cropBytes, "legacy-printing-check", default);
        Console.WriteLine($"Physical candidates: {candidates.Count}; result: {result.Set} {result.CollectorNumber}; {result.ArtVerdict}; {result.ArtReason}");
        return result.Set == "phpr" && result.CollectorNumber == "5" ? 0 : 1;
    }

    public static async Task RunOfflineAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nPhysical legacy printings: candidate coverage and safe limits");
        object Printing(string set, int number, bool digital = false) => new
        {
            set, collector_number = number.ToString(), name = "Legacy card", lang = "en", digital,
            image_uris = new { normal = $"https://cards.scryfall.io/{set}-{number}.png" }
        };
        using var handler = new Handler(request =>
        {
            var q = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath == "/cards/search" && !q.Contains("page=2"))
            {
                check(q.Contains("game:paper"), "printing searches explicitly request paper cards");
                return JsonSerializer.Serialize(new
                {
                    data = Enumerable.Range(1, 13).Select(i => Printing("new", i)).Append(Printing("mtgo", 1, true)),
                    has_more = true, next_page = "https://api.scryfall.com/cards/search?page=2"
                });
            }
            return JsonSerializer.Serialize(new { data = new[] { Printing("old", 1) }, has_more = false });
        });
        using var http = new HttpClient(handler);
        var catalogue = new ScryfallCatalogue(http);
        var printings = await catalogue.SearchPrintingsAsync("Legacy card", null, "en", default);
        check(printings.Count == 14 && printings[^1].Set == "old" && printings.All(p => p.Set != "mtgo"),
            "an old promo beyond the first twelve and on a later page survives; digital-only cards do not");
        var tools = new MtgCatalogueTools(catalogue, NullLogger<MtgCatalogueTools>.Instance);
        using var result = JsonDocument.Parse(await tools.ResolvePrintingAsync("Legacy card", "new", language: "en"));
        check(result.RootElement.GetProperty("alternates").GetArrayLength() == 14
            && result.RootElement.GetProperty("alternatesComplete").GetBoolean(),
            "resolve_printing carries the entire physical candidate field through the tool response");

        using var manyHandler = new Handler(_ => JsonSerializer.Serialize(new
        {
            data = Enumerable.Range(1, 65).Select(i => Printing("old", i)), has_more = false
        }));
        using var manyHttp = new HttpClient(manyHandler);
        var manyTools = new MtgCatalogueTools(new ScryfallCatalogue(manyHttp), NullLogger<MtgCatalogueTools>.Instance);
        using var many = JsonDocument.Parse(await manyTools.ResolvePrintingAsync("Legacy card", "old", language: "en"));
        check(many.RootElement.GetProperty("alternates").GetArrayLength() == 64
            && !many.RootElement.GetProperty("alternatesComplete").GetBoolean(),
            "a bounded list explicitly signals incompleteness rather than presenting a biased field as complete");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await manyTools.ResolvePrintingAsync("Legacy card", "old", language: "en", ct: cancelled.Token);
            check(false, "printing candidate cancellation must propagate");
        }
        catch (OperationCanceledException) { check(true, "printing candidate cancellation propagates"); }

        using var unsafeHandler = new Handler(_ => JsonSerializer.Serialize(new
        {
            data = new[] { Printing("old", 1) }, has_more = true, next_page = "https://untrusted.example/cards/search"
        }));
        using var unsafeHttp = new HttpClient(unsafeHandler);
        try
        {
            await new ScryfallCatalogue(unsafeHttp).SearchPrintingsAsync("Legacy card", null, "en", default);
            check(false, "unsafe next-page links must fail");
        }
        catch (InvalidDataException) { check(true, "pagination cannot redirect catalogue fetches to another host"); }
    }

    private sealed class Handler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(request), System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

using System.Net;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class PromoPrintingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nVisible promotional footer families");
        // Public fixture: GET https://api.scryfall.com/cards/pgpx/2018, John Avon, Grand Prix Promos.
        var mutavault = Card("pgpx", "2018", "Mutavault", "John Avon");
        using (var handler = new Handler(mutavault))
        using (var http = new HttpClient(handler))
        {
            var catalogue = new ScryfallCatalogue(http);
            var result = await catalogue.ResolveAsync("Mutavault", "PRM", "001/001", "en", default, "Grand Prix");
            check(result is { Resolution: "corrected", Printing.Set: "pgpx", Printing.CollectorNumber: "2018" }
                && result.ReadCollectorNumber == "001/001",
                "photographed PRM 001/001 + Grand Prix resolves the exact-name family, retaining the printed fraction");
            check(handler.Requests.Count == 1 && handler.Requests[0].Contains("set:pgpx")
                && handler.Requests[0].Contains("!\"Mutavault\""),
                "family lookup precedes and excludes fuzzy, numbered PRM and broad fallback calls");
            var tools = new MtgCatalogueTools(catalogue, NullLogger<MtgCatalogueTools>.Instance);
            using var reply = JsonDocument.Parse(await tools.ResolvePrintingAsync(
                "Mutavault", "PRM", "001/001", "en", default, "Grand Prix"));
            var json = reply.RootElement;
            check(json.GetProperty("readSet").GetString() == "PRM"
                && json.GetProperty("readCollectorNumber").GetString() == "001/001"
                && json.GetProperty("readPromoLabel").GetString() == "Grand Prix"
                && json.GetProperty("set").GetString() == "pgpx"
                && json.GetProperty("collectorNumber").GetString() == "2018",
                "actual MCP response keeps raw set, fraction and label separate from catalogue identifiers");
            var parsed = Parse(reply.RootElement.GetRawText());
            check(parsed.ReadSet == "PRM" && parsed.ReadCollectorNumber == "001/001"
                && parsed.ReadPromoLabel == "Grand Prix" && parsed.Set == "pgpx",
                "worker parsing preserves original footer evidence unchanged");
            FoundryCardIdentificationAgent.ValidatePromoPrinting(parsed);
            var shortened = await catalogue.ResolveAsync("Mutavault", "PRM", "001", "en", default, "Grand Prix");
            check(shortened.Printing is { Set: "pgpx", CollectorNumber: "2018" }
                && shortened.ReadCollectorNumber == "001",
                "the independent model's shortened PRM 001 reading also maps by visible family, not number");
            var unnamed = await catalogue.ResolveAsync(null, "PRM", "001", "en", default, "Grand Prix");
            check(unnamed.Printing is null && unnamed.Resolution == "unresolved",
                "a visible family without a readable name cannot select an arbitrary promo");
        }
        foreach (var label in new string?[] { null, "PRM" })
        {
            using var handler = new Handler(mutavault);
            using var http = new HttpClient(handler);
            await new ScryfallCatalogue(http).ResolveAsync("Mutavault", "PRM", "001", "en", default, label);
            check(handler.Requests.All(r => !r.Contains("set:pgpx")),
                "PRM alone or absent label does not force a Grand Prix family");
        }
        foreach (var cards in new[]
        {
            Array.Empty<object>(),
            new[] { Card("pgpx", "2018", "Unrelated Card", "John Avon") },
            new[] { Card("clb", "903", "Mutavault", "Other Artist") }
        })
        {
            using var handler = new Handler(cards);
            using var http = new HttpClient(handler);
            var tools = new MtgCatalogueTools(new ScryfallCatalogue(http), NullLogger<MtgCatalogueTools>.Instance);
            using var reply = JsonDocument.Parse(await tools.ResolvePrintingAsync(
                "Mutavault", "PRM", "001/001", "en", default, "Grand Prix"));
            check(!reply.RootElement.GetProperty("found").GetBoolean()
                && reply.RootElement.GetProperty("resolution").GetString() == "unresolved"
                && reply.RootElement.GetProperty("readPromoLabel").GetString() == "Grand Prix"
                && handler.Requests.All(r => r.Contains("set:pgpx")),
                "missing, unrelated-name or wrong-family matches stay unresolved without broad fallback");
        }
        using (var handler = new Handler(mutavault, Card("pgpx", "001", "Mutavault", "Another Artist")))
        using (var http = new HttpClient(handler))
        {
            var tools = new MtgCatalogueTools(new ScryfallCatalogue(http), NullLogger<MtgCatalogueTools>.Instance);
            using var reply = JsonDocument.Parse(await tools.ResolvePrintingAsync(
                "Mutavault", "PRM", "001/001", "en", default, "Grand Prix"));
            check(reply.RootElement.GetProperty("resolution").GetString() == "ambiguous"
                && reply.RootElement.GetProperty("candidates").GetArrayLength() == 2
                && Parse(reply.RootElement.GetRawText()).Resolution == "ambiguous",
                "multiple exact-name family printings remain explicitly ambiguous even if one number matches");
        }
        using (var handler = new Handler(Card("pgpx", "2015", "Griselbrand", "Igor Kieryluk")))
        using (var http = new HttpClient(handler))
        {
            var result = await new ScryfallCatalogue(http).ResolveAsync(
                "Griselbrand", "PRM", "001/001", "en", default, "grand  prix");
            check(result.Printing is { Set: "pgpx", CollectorNumber: "2015", Name: "Griselbrand" },
                "promo-family mapping is name-independent and handles transcribed label whitespace/case");
        }
        var wrong = Parse("""
            {"set":"clb","collectorNumber":"903","name":"Mutavault","readSet":"PRM",
             "readCollectorNumber":"001/001","readPromoLabel":"Grand Prix","resolution":"relocated"}
            """);
        try
        {
            FoundryCardIdentificationAgent.ValidatePromoPrinting(wrong);
            check(false, "explicit Grand Prix evidence must reject non-PGPX printing");
        }
        catch (InvalidOperationException ex)
        {
            check(ex.Message.Contains("resolve_printing") && ex.Message.Contains("promoLabel")
                && ex.Message.Contains("ambiguous"),
                "worker guard rejects CLB 903 with corrective family-tool instructions, without endorsing ambiguity");
        }
        FoundryCardIdentificationAgent.ValidatePromoPrinting(wrong with { ReadPromoLabel = null });
        check(true, "worker guard does not invent a Grand Prix label from PRM alone");
        var unresolved = wrong with { Set = "PRM", CollectorNumber = "001/001", Resolution = "unresolved" };
        FoundryCardIdentificationAgent.ValidatePromoPrinting(unresolved);
        check(unresolved.IsIdentified && !unresolved.PrintingWasPlaced,
            "missing promo-family matches preserve readable raw footer/name as unresolved, never a confirmed printing");
        try
        {
            FoundryCardIdentificationAgent.ValidatePromoPrinting(wrong with { Resolution = "unresolved" });
            check(false, "an unresolved label must not launder an unrelated catalogue printing");
        }
        catch (InvalidOperationException)
        {
            check(true, "unresolved fallback preserves raw PRM evidence only, not an unrelated CLB printing");
        }
        var prompt = FoundryCardIdentificationAgent.BuildPrompt(GameAgentProfile.Mtg);
        check(prompt.Contains("promoLabel") && prompt.Contains("readPromoLabel") && prompt.Contains("001/001")
            && prompt.Contains("PRM alone is not evidence") && prompt.Contains("never upgrade")
            && prompt.Contains("pgpx"),
            "actual identification prompt separates visible label/raw footer from catalogue family/year and preserves ambiguity");
        var review = FoundryCardIdentificationAgent.BuildReviewPrompt(wrong);
        check(review.Contains("promoLabel \"Grand Prix\"") && review.Contains("PRM and 001/001")
            && review.Contains("PRM alone is insufficient") && review.Contains("Never upgrade an ambiguous"),
            "printing-review prompt requires the visible label and family lookup without replacing raw evidence");
        check(prompt.Contains("\"identificationSummary\":null") && prompt.Contains("1-2 sentence user-facing")
            && prompt.Contains("visible title/footer") && prompt.Contains("catalogue corroboration or uncertainty")
            && prompt.Contains("Do not provide private chain-of-thought"),
            "identification prompt requests a short evidence-based user explanation, not private chain-of-thought");
        const string summary = "The title reads Mutavault and the footer shows PRM 001/001 and Grand Prix. "
            + "The catalogue has one matching Grand Prix printing, PGPX 2018.";
        var explained = Parse(JsonSerializer.Serialize(new
        {
            set = "pgpx", collectorNumber = "2018", name = "Mutavault", identificationSummary = summary
        }));
        check(explained.IdentificationSummary == summary
            && JsonSerializer.Serialize(explained).Contains("\"identificationSummary\":"),
            "parser preserves the user-facing explanation and its public JSON property name");
        foreach (var optional in new[] { "{}", """{"identificationSummary":null}""",
            """{"identificationSummary":"   "}""", """{"identificationSummary":42}""" })
        {
            check(Parse(optional).IdentificationSummary is null,
                "missing, null, blank or non-string summaries remain optional without inventing an explanation");
        }
    }

    private static IdentifiedCard Parse(string json) => FoundryCardIdentificationAgent.ParseIdentification(
        json, new CardCrop(0, new AgentImage(Array.Empty<byte>(), "card.png", "image/png"),
            new CardQuad([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]), CardGames.Magic), CardGames.Magic, "test");

    private static object Card(string set, string number, string name, string artist) => new
    {
        set, collector_number = number, name, artist, lang = "en", games = new[] { "paper" },
        image_uris = new { normal = $"https://cards.scryfall.io/{set}/{number}.jpg" }
    };

    private sealed class Handler(params object[] cards) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(Uri.UnescapeDataString(uri.PathAndQuery));
            if (uri.AbsolutePath != "/cards/search")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = cards, has_more = false }))
            });
        }
    }
}

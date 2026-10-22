using System.Net;
using System.Text.Json;
using Azure.Core;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Catalogue;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class AlternateTitleChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nCatalogue-backed alternate titles");
        foreach (var (number, name, flavor) in new[]
        {
            ("1", "Adeline, Resplendent Cathar", "Hero of Light"),
            ("52", "Inalla, Archmage Ritualist", "Kuja, Mage Manufacturer"),
            ("30", "Laboratory Maniac", "Vana'diel Adventurers"),
            ("18", "Tymna the Weaver", "Cecil Harvey"),
            ("4", "Counterspell", "Wild Rose Rebellion"),
            ("17", "Jodah, the Unifier", "Warrior of Light"),
            ("19", "Winota, Joiner of Forces", "Bartz Klauser")
        })
        {
            using var handler = new PrintingHandler(number, name, flavor);
            using var http = new HttpClient(handler);
            var catalogue = new ScryfallCatalogue(http);
            var resolved = await catalogue.ResolveAsync(flavor, "FCA", "R " + number.PadLeft(4, '0'), "en", default);
            check(resolved is { Resolution: "confirmed", Printing: not null } && resolved.Printing.Name == name
                && resolved.Printing.FlavorName == flavor && handler.Calls == 1,
                $"failed-scan fixture FCA {number}: exact printing and catalogue flavor name resolve in one request");
            var card = new IdentifiedCard
            {
                Game = CardGames.Magic, Set = "fca", CollectorNumber = number, Name = name,
                ReadName = flavor, Language = "en", Resolution = "confirmed"
            };
            var agent = Agent(http);
            await agent.ValidateTitleAsync(card, default);
            check(handler.Calls == 2, $"FCA {number}: worker independently verifies the photographed alias against the exact catalogue printing");
            foreach (var wrong in new[] { card with { Name = "Lightning Bolt" }, card with { ReadName = flavor + " invented" },
                card with { ReadName = "???" } })
            {
                try
                {
                    await agent.ValidateTitleAsync(wrong, default);
                    check(false, "an unrelated canonical name or invented alias must be rejected");
                }
                catch (InvalidOperationException)
                {
                    check(true, $"FCA {number}: an unrelated name or invented alias cannot bypass the title guard");
                }
            }
            var tools = new MtgCatalogueTools(catalogue, NullLogger<MtgCatalogueTools>.Instance);
            using var toolResult = JsonDocument.Parse(await tools.ResolvePrintingAsync(flavor, "fca", number, "en"));
            check(toolResult.RootElement.GetProperty("flavorName").GetString() == flavor
                && toolResult.RootElement.GetProperty("name").GetString() == name,
                $"FCA {number}: the actual MCP tool returns canonical and flavour names separately");
        }
        using (var handler = new PrintingHandler("17", "Lightning Bolt", "Unrelated alias"))
        using (var http = new HttpClient(handler))
        {
            var result = await new ScryfallCatalogue(http).ResolveAsync("Warrior of Light", "fca", "17", "en", default);
            check(result.Resolution != "confirmed", "an exact number without matching title evidence is never confirmed");
        }
        foreach (var status in new[] { HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized })
        {
            using var http = new HttpClient(new ErrorHandler(status));
            var tools = new MtgCatalogueTools(new ScryfallCatalogue(http), NullLogger<MtgCatalogueTools>.Instance);
            foreach (var call in new Func<Task<string>>[]
            {
                () => tools.ResolvePrintingAsync("Warrior of Light", "fca", "17", "en"),
                () => tools.LookupBySetAndNumberAsync("fca", "17", "en"),
                () => tools.LookupByNameAndSetAsync("Warrior of Light", "fca"),
                () => tools.LookupByNameAsync("Warrior of Light")
            })
            {
                try
                {
                    await call();
                    check(false, "catalogue failure must surface explicitly");
                }
                catch (ModelContextProtocol.McpException ex)
                {
                    check(status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                        && ex.Message.StartsWith("catalogue_transient_failure:", StringComparison.Ordinal),
                        $"catalogue {status} is explicitly classified as retryable without returning a fake printing");
                }
                catch (HttpRequestException ex)
                {
                    check(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && ex.StatusCode == status,
                        $"catalogue {status} is not disguised as a transient failure");
                }
            }
        }
        var prompt = FoundryCardIdentificationAgent.BuildPrompt(GameAgentProfile.Mtg);
        check(prompt.Contains("flavor_name") && prompt.Contains("FFXI") && prompt.Contains("Do not infer FCA")
            && prompt.Contains("canonical name") && prompt.Contains("\"R 0017\""),
            "the real prompt distinguishes alternate/canonical titles, franchise markers, visible set codes and rarity prefixes");
    }

    private static FoundryCardIdentificationAgent Agent(HttpClient http) => new(
        new FoundryAgentClient(http, new Credential(), NullLogger<FoundryAgentClient>.Instance,
            "https://unused.test", "unused", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)),
        GameAgentProfile.Mtg, "unused", NullLogger<FoundryCardIdentificationAgent>.Instance, new ScryfallPriceClient(http));

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PrintingHandler(string number, string name, string flavor) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath != "/cards/fca/" + number)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                    { set = "fca", collector_number = number, name, flavor_name = flavor, lang = "en", prices = new { } }))
            });
        }
    }

    private sealed class ErrorHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}

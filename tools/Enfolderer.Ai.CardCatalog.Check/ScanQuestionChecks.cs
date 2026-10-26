using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class ScanQuestionChecks
{
    public static async Task RunLiveAsync(string endpoint, string tenantId, Action<bool, string> check)
    {
        var gatewayEndpoint = Environment.GetEnvironmentVariable("ENFOLDERER_TEST_GATEWAY_ENDPOINT");
        var options = new ScanKnowledgeOptions
        {
            Enabled = true, Endpoint = endpoint, GatewayEnabled = !string.IsNullOrEmpty(gatewayEndpoint),
            GatewayEndpoint = gatewayEndpoint ?? "",
            GatewayApiKey = Environment.GetEnvironmentVariable("ENFOLDERER_TEST_GATEWAY_KEY") ?? ""
        };
        if (!options.IsValid || !Guid.TryParse(tenantId, out _))
            throw new ArgumentException("Supply a trusted Search HTTPS origin and tenant GUID.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(80), MaxResponseContentBufferSize = 1024 * 1024 };
        var client = new FoundryIqScanQuestions(http,
            new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId, ProcessTimeout = TimeSpan.FromSeconds(60) }),
            options, NullLogger<FoundryIqScanQuestions>.Instance);
        var scan = new ScanResultDocument
        {
            Cards =
            [
                new IdentifiedCard { Index = 0, Game = "mtg", Name = "Solemnity", Set = "hou", CollectorNumber = "22" },
                new IdentifiedCard { Index = 1, Game = "mtg", Name = "Naked Singularity", Set = "ice", CollectorNumber = "329" }
            ]
        };
        var answer = await client.AskAsync(scan, "What does Solemnity do to counters?", default);
        Console.WriteLine(answer.Answer);
        foreach (var source in answer.Sources) Console.WriteLine($"{source.Title}: {source.Url}");
        check(answer.Grounded && answer.Sources.Any(source => source.Title == "Solemnity"),
            "live IQ returns a cited card answer verified by canonical index lookup");
        var unrelated = await client.AskAsync(scan, "What is the weather in Paris today?", default);
        Console.WriteLine(unrelated.Answer);
        check(!unrelated.Grounded && unrelated.Sources.Count == 0,
            "live IQ does not publish an unrelated uncited answer");
        var historical = new ScanResultDocument
        {
            Cards =
            [
                new IdentifiedCard { Index = 0, Game = "mtg", Name = "Sword of Hearth and Home", Set = "tmc", CollectorNumber = "136" },
                new IdentifiedCard { Index = 1, Game = "mtg", Name = "Food Chain", Set = "tmc", CollectorNumber = "133" },
                new IdentifiedCard { Index = 2, Game = "mtg", Name = "Emeritus of Woe // Demonic Tutor", Set = "sos", CollectorNumber = "80" },
                new IdentifiedCard { Index = 3, Game = "mtg", Name = "Mystic Remora", Set = "dmr", CollectorNumber = "420" },
                new IdentifiedCard { Index = 4, Game = "mtg", Name = "Ugin's Labyrinth", Set = "mh3", CollectorNumber = "233" },
                new IdentifiedCard { Index = 5, Game = "mtg", Name = "Mavinda, Students' Advocate", Set = "stx", CollectorNumber = "21" },
                new IdentifiedCard { Index = 6, Game = "mtg", Name = "Echocasting Symposium", Set = "sos", CollectorNumber = "44" },
                new IdentifiedCard { Index = 7, Game = "mtg", Name = "Molten-Core Maestro", Set = "sos", CollectorNumber = "125" },
                new IdentifiedCard { Index = 8, Game = "mtg", Name = "Mockingbird", Set = "blb", CollectorNumber = "61" }
            ]
        };
        foreach (var (question, title) in new[]
        {
            ("What does Echocasting Symposium do?", "Echocasting Symposium"),
            ("Explain Molten-Core Maestro's abilities.", "Molten-Core Maestro"),
            ("What does Emeritus of Woe do?", "Emeritus of Woe")
        })
        {
            var response = await client.AskAsync(historical, question, default);
            Console.WriteLine(response.Answer);
            check(response.Grounded && response.Sources.Any(source => source.Title.Contains(title, StringComparison.OrdinalIgnoreCase)),
                $"historical reported scan identities have live cited coverage: {title}");
        }
        var comparison = await client.AskAsync(historical, "Compare the abilities of Food Chain and Mystic Remora.", default);
        Console.WriteLine(comparison.Answer);
        check(comparison.Grounded && comparison.Sources.Any(s => s.Title == "Food Chain")
            && comparison.Sources.Any(s => s.Title == "Mystic Remora"), "live comparison cites both compared cards");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var interaction = await client.AskAsync(scan, "How do Solemnity and Naked Singularity interact? Explain only what their text supports, not an official ruling.", default);
            Console.WriteLine(interaction.Answer);
            check(interaction.Grounded && interaction.Sources.Any(s => s.Title == "Solemnity")
                && interaction.Sources.Any(s => s.Title == "Naked Singularity"),
                $"live interaction explanation cites both card texts (independent attempt {attempt}/3)");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nFoundry IQ grounded scan questions");
        var options = new ScanKnowledgeOptions { Enabled = true, Endpoint = "https://cards.search.windows.net" };
        check(options.IsValid && !new ScanKnowledgeOptions { Endpoint = "https://evil.example" }.IsValid
            && !new ScanKnowledgeOptions { Endpoint = "http://cards.search.windows.net" }.IsValid,
            "knowledge configuration requires a trusted Search HTTPS origin");
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new FoundryIqScanQuestions(http, new Credential(), options, NullLogger<FoundryIqScanQuestions>.Instance);
        var scan = new ScanResultDocument
        {
            JobId = "private-job", AnnotatedImagePath = "private-blob", Cards =
            [
                new IdentifiedCard { Index = 0, Game = "mtg", Name = "Solemnity", Set = "hou", CollectorNumber = "22",
                    Resolution = "unplaced", Ownership = new(7, 18, 1), Prices = new() { Nonfoil = 123 } }
            ]
        };
        var answer = await client.AskAsync(scan, "What does this card do?", default);
        check(answer.Grounded && answer.Sources.Single().Url == "https://scryfall.com/card/hou/22/solemnity"
            && answer.Answer.Contains("[ref_id:0]"), "IQ answer retains verified clickable source mapping");
        using var payload = JsonDocument.Parse(handler.Payload!);
        check(payload.RootElement.GetProperty("outputMode").GetString() == "answerSynthesis"
            && payload.RootElement.GetProperty("maxOutputSize").GetInt32() == 6000
            && handler.Paths[0].Contains("/knowledgebases/scan-cards/retrieve")
            && handler.Paths[1].Contains("/indexes/scryfall/docs/11111111-1111-1111-1111-111111111111"),
            "client uses actual IQ synthesis and canonical indexed citation lookup");
        check(handler.Payload!.Contains("printingPlaced") && !handler.Payload.Contains("private-job")
            && !handler.Payload.Contains("private-blob") && !handler.Payload.Contains("requiredQuantity"),
            "scan context excludes storage paths and ownership data");
        var userText = payload.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        check(!userText.Contains("123") && !userText.Contains("18") && userText.Contains("printingPlaced"),
            "only identification facts, not private quantities/prices, enter scan context");
        check(handler.Authorized && handler.Paths.All(p => p.Contains("api-version=2026-08-01-preview")),
            "managed-identity bearer tokens and pinned preview API version are used");
        handler.Role = "assistant";
        answer = await client.AskAsync(scan, "Question", default);
        check(answer.Grounded, "explicit assistant answer role is also supported");
        handler.Role = "user";
        await Reject("non-assistant answer role is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.Role = null;
        handler.Text = "An unsupported uncited answer.";
        handler.Paths.Clear();
        answer = await client.AskAsync(scan, "Question", default);
        check(!answer.Grounded && answer.Sources.Count == 0 && !answer.Answer.Contains(handler.Text)
            && handler.Paths.Count == 2 && answer.Kind == "insufficientEvidence",
            "uncited model output checks coverage and distinguishes insufficient evidence");
        handler.Covered = false;
        answer = await client.AskAsync(scan, "Question", default);
        check(answer.Kind == "missingKnowledge" && answer.MissingCards!.Single() == "Solemnity",
            "missing catalogue knowledge is reported explicitly");
        handler.Covered = true;
        handler.Paths.Clear();
        foreach (var question in new[] { "Do I own Solemnity?", "Which card is most valuable?", "Explain the identification evidence for card 1." })
        {
            answer = await client.AskAsync(scan, question, default);
            check(answer.Kind == "scanEvidence" && answer.Grounded && handler.Paths.Count == 0,
                $"private recorded question avoids Search/model calls: {question}");
        }
        answer = await client.AskAsync(scan, "Do I own Solemnity?", default);
        check(answer.Answer.Contains("exact printing 7") && answer.Answer.Contains("all printings 18"),
            "ownership answer uses recorded quantities without inferring current ownership");
        answer = await client.AskAsync(scan, "What is the price?", default);
        check(answer.Answer.Contains("123.00 EUR") && answer.Answer.Contains("not current offers"),
            "price answer uses timestamped recorded values and caveats");
        var noFacts = RecordedScanAnswers.TryAnswer(new ScanResultDocument { Cards = [new IdentifiedCard { Index = 0 }] }, "Do I own card 1?");
        check(noFacts!.Answer.Contains("unavailable or disabled") && !noFacts.Answer.Contains("exact printing 0"),
            "disabled collection is unavailable, never zero owned");
        check(RecordedScanAnswers.TryAnswer(scan, "Explain identification for card 999999999999999999999") is not null,
            "oversized card selector does not overflow");
        handler.Text = "Claim [ref_id:99]";
        await Reject("unknown citation is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.Text = "Claim [ref_id:abc]";
        await Reject("malformed citation is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.Text = "Claim [ref_id:0]";
        handler.Url = "javascript:alert(1)";
        await Reject("unsafe source URL is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.Url = "https://scryfall.com.evil.example/card";
        await Reject("lookalike source hostname is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.Url = "https://scryfall.com/card/hou/22/solemnity";
        options.GatewayEnabled = true;
        options.GatewayEndpoint = "https://cards.swedencentral-01.ai.gateway.azure.com";
        options.GatewayApiKey = "fixture-gateway-key";
        handler.Text = "[{\"ref_id\":0,\"content\":\"Solemnity prevents counters.\"}]";
        handler.Paths.Clear();
        answer = await client.AskAsync(scan, "What does this card do?", default);
        check(answer.Grounded && handler.Paths.Any(p => p.EndsWith("/chat/completions"))
            && handler.GatewayAuthorized && handler.Payload!.Contains("extractiveData"),
            "dedicated preview synthesizes IQ extractive evidence with its own key and verified citations");
        check(handler.GatewayPayload is not null && !handler.GatewayPayload.Contains("private-job")
            && !handler.GatewayPayload.Contains("private-blob") && !handler.GatewayPayload.Contains("requiredQuantity"),
            "gateway model receives public facts and evidence, never scan storage/ownership data");
        check(handler.GatewayPayload!.Contains("plain text") && handler.GatewayPayload.Contains("conditional card-text deductions")
            && handler.GatewayPayload.Contains("printingPlaced=false"),
            "gateway synthesis preserves answer formatting, printing uncertainty and supported interaction explanations");
        var localCalls = handler.Paths.Count;
        await client.AskAsync(scan, "How many copies do I own?", default);
        check(handler.Paths.Count == localCalls, "private recorded facts bypass both Search and gateway models");
        handler.GatewayText = "Unsupported [ref_id:99]";
        await Reject("gateway invented citation is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.GatewayText = "Solemnity prevents counters [ref_id:0].";
        handler.GatewayFinishReason = "length";
        await Reject("gateway truncated answer is rejected", check, () => client.AskAsync(scan, "Question", default));
        handler.GatewayFinishReason = "stop";
        handler.GatewayMalformed = true;
        await Reject("gateway malformed response is surfaced as invalid evidence", check, () => client.AskAsync(scan, "Question", default));
        handler.GatewayMalformed = false;
        handler.GatewayStatus = HttpStatusCode.TooManyRequests;
        try { await client.AskAsync(scan, "Question", default); check(false, "gateway quota rejection propagates without direct fallback"); }
        catch (HttpRequestException ex) { check(ex.StatusCode == HttpStatusCode.TooManyRequests, "gateway quota rejection propagates without direct fallback"); }
        check(!new ScanKnowledgeOptions { Endpoint = options.Endpoint, GatewayEnabled = true }.IsValid
            && !new ScanKnowledgeOptions { Endpoint = options.Endpoint, GatewayEnabled = true,
                GatewayEndpoint = "https://evil.example", GatewayApiKey = "key" }.IsValid,
            "enabled gateway requires trusted HTTPS origin and nonempty key");
        options.GatewayEnabled = false;
        options.GatewayRequired = true;
        var beforeRequired = handler.Paths.Count;
        await Reject("required gateway cannot silently revert to direct synthesis", check,
            () => client.AskAsync(scan, "Question", default));
        await client.AskAsync(scan, "How many copies do I own?", default);
        check(handler.Paths.Count == beforeRequired, "gateway setup gap makes no external calls and preserves private local answers");
        options.GatewayRequired = false;
        handler.Status = HttpStatusCode.Forbidden;
        try { await client.AskAsync(scan, "Question", default); check(false, "retrieval access failure propagates"); }
        catch (HttpRequestException ex) { check(ex.StatusCode == HttpStatusCode.Forbidden, "retrieval access failure propagates"); }
    }

    private static async Task Reject(string name, Action<bool, string> check, Func<Task<ScanAnswer>> action)
    {
        try { await action(); check(false, name); }
        catch (InvalidDataException) { check(true, name); }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => ValueTask.FromResult(GetToken(context, ct));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Text { get; set; } = "Solemnity prevents counters [ref_id:0].";
        public string? Role { get; set; }
        public bool Covered { get; set; } = true;
        public string Url { get; set; } = "https://scryfall.com/card/hou/22/solemnity";
        public string? Payload { get; private set; }
        public List<string> Paths { get; } = [];
        public bool Authorized { get; private set; } = true;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode GatewayStatus { get; set; } = HttpStatusCode.OK;
        public string GatewayText { get; set; } = "Solemnity prevents counters [ref_id:0].";
        public string GatewayFinishReason { get; set; } = "stop";
        public bool GatewayMalformed { get; set; }
        public bool GatewayAuthorized { get; private set; }
        public string? GatewayPayload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith("/chat/completions"))
            {
                GatewayAuthorized = request.Headers.Authorization is null
                    && request.Headers.GetValues("api-key").Single() == "fixture-gateway-key";
                GatewayPayload = await request.Content!.ReadAsStringAsync(ct);
                return new(GatewayStatus) { Content = new StringContent(GatewayMalformed ? "{}" : JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = GatewayFinishReason,
                        message = new { role = "assistant", content = GatewayText } } }
                }), Encoding.UTF8, "application/json") };
            }
            Authorized &= request.Headers.Authorization?.ToString() == "Bearer test-token";
            object response;
            if (request.RequestUri.AbsolutePath.EndsWith("/docs/search"))
            {
                response = new { value = Covered ? new[] { new { id = "11111111-1111-1111-1111-111111111111" } } : [] };
            }
            else if (request.Method == HttpMethod.Post)
            {
                Payload = await request.Content!.ReadAsStringAsync(ct);
                var message = new Dictionary<string, object>
                {
                    ["content"] = new[] { new { type = "text", text = Text } }
                };
                if (Role is not null) message["role"] = Role;
                response = new
                {
                    response = new[] { message },
                    references = new[] { new { type = "searchIndex", id = "0", docKey = "11111111-1111-1111-1111-111111111111" } }
                };
            }
            else response = new { title = "Solemnity", url = Url };
            return new(Status) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
}

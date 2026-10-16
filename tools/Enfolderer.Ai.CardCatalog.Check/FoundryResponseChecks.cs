using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Enfolderer.Ai.Worker.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class FoundryResponseChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nFoundry agent-identity Responses transport");
        using (var handler = new FlowHandler())
        using (var http = new HttpClient(handler))
        {
            var reply = await Client(http).RunAsync("CardAgent", "identify",
                new AgentImage(new byte[] { 1, 2 }, "card.png", "image/png"), "catalogue", default,
                new AgentImage(new byte[] { 3, 4 }, "footer.png", "image/png"));
            check(reply == "identified" && handler.Creates == 2 && handler.Deletes == 2,
                "background response, MCP approval continuation and deletion preserve one isolated scan");
            check(handler.Images == 2 && handler.Polls >= 3 && handler.Continuation,
                "full-card and footer images are inline high-detail input; approval reuses the previous response");
        }
        using (var handler = new FlowHandler(label: "other-team"))
        using (var http = new HttpClient(handler))
        {
            await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
            check(handler.Approved == false, "cross-team MCP approvals are explicitly denied");
        }
        using (var handler = new FlowHandler(malformed: true))
        using (var http = new HttpClient(handler))
        {
            await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default,
                validateReply: r => { using var _ = JsonDocument.Parse(r); });
            check(handler.Creates == 3 && handler.Deletes == 3,
                "malformed JSON regenerates once using the same original response history");
        }
        using (var handler = new FlowHandler(failure: HttpStatusCode.Forbidden))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "Responses authorization failure must fail");
            }
            catch (FoundryAccessException ex)
            {
                check(ex.StatusCode == 403 && handler.Creates == 1,
                    "project/agent authorization failure is never retried or downgraded to classic");
            }
        }
        using (var handler = new FlowHandler(failure: HttpStatusCode.ServiceUnavailable))
        using (var http = new HttpClient(handler))
        {
            await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
            check(handler.Creates == 3, "transient response creation failure retries without changing identity");
        }
        using (var handler = new FlowHandler(toolError: true))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "MCP authentication/tool failures must fail");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message.Contains("MCP call failed") && handler.Deletes == 2,
                    "MCP failure cannot become a success-shaped model-only identification");
            }
        }
        using (var handler = new FlowHandler(pending: true))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http, TimeSpan.FromMilliseconds(60)).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "pending response must time out");
            }
            catch (TimeoutException)
            {
                check(handler.Cancels == 1 && handler.Deletes == 1,
                    "timeout cancels background work and deletes retained response input");
            }
        }
        using (var handler = new FlowHandler())
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("asst_legacy", "identify", null, "catalogue", default);
                check(false, "classic IDs must be rejected");
            }
            catch (InvalidOperationException)
            {
                check(handler.Creates == 0, "new transport rejects classic IDs before any request");
            }
        }
    }

    private static FoundryAgentClient Client(HttpClient http, TimeSpan? timeout = null) => new(
        http, new Credential(), NullLogger<FoundryAgentClient>.Instance, "https://foundry.test",
        "2025-11-15-preview", timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1),
        throttleBackoff: TimeSpan.FromMilliseconds(1), useResponses: true);

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
            new("foundry-test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            ValueTask.FromResult(GetToken(context, ct));
    }

    private sealed class FlowHandler(string label = "catalogue", bool malformed = false,
        HttpStatusCode? failure = null, bool toolError = false, bool pending = false) : HttpMessageHandler
    {
        public int Creates { get; private set; }
        public int Deletes { get; private set; }
        public int Cancels { get; private set; }
        public int Images { get; private set; }
        public int Polls { get; private set; }
        public bool Continuation { get; private set; }
        public bool? Approved { get; private set; }
        private int _responses;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != "foundry-test-token"
                || !request.RequestUri!.AbsolutePath.StartsWith(
                    "/agents/CardAgent/endpoint/protocols/openai/responses", StringComparison.Ordinal))
                throw new InvalidOperationException("Wrong identity transport or endpoint.");
            string json;
            if (request.Method == HttpMethod.Delete) { Deletes++; json = "{}"; }
            else if (request.RequestUri.AbsolutePath.EndsWith("/cancel")) { Cancels++; json = "{}"; }
            else if (request.Method == HttpMethod.Post)
            {
                Creates++;
                if (failure is not null && Creates == 1)
                    return new(failure.Value) { Content = new StringContent("{}") };
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (_responses == 0)
                {
                    if (body.RootElement.TryGetProperty("previous_response_id", out _))
                        throw new InvalidOperationException("Initial response must omit previous_response_id, not send null.");
                    var parts = body.RootElement.GetProperty("input")[0].GetProperty("content");
                    Images = parts.EnumerateArray().Count(p => p.GetProperty("type").GetString() == "input_image"
                        && p.GetProperty("detail").GetString() == "high"
                        && p.GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,"));
                }
                else if (_responses == 1)
                {
                    Continuation = body.RootElement.GetProperty("previous_response_id").GetString() == "resp-1";
                    Approved = body.RootElement.GetProperty("input")[0].GetProperty("approve").GetBoolean();
                }
                _responses++;
                json = $$"""{"id":"resp-{{_responses}}","status":"queued"}""";
            }
            else
            {
                Polls++;
                if (pending || Polls == 1)
                    json = """{"status":"in_progress"}""";
                else if (_responses == 1)
                    json = JsonSerializer.Serialize(new { status = "completed", output = new[] {
                        new { type = "mcp_approval_request", id = "approval-1", server_label = label, name = "lookup" } } });
                else if (toolError)
                    json = """{"status":"completed","output":[{"type":"mcp_call","error":{"code":"unauthorized"}}]}""";
                else
                {
                    var text = malformed ? (_responses == 2 ? "{" : "{}") : "identified";
                    json = JsonSerializer.Serialize(new { status = "completed", output = new[] {
                        new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } });
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}

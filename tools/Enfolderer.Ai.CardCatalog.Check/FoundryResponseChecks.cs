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
        using (var response = JsonDocument.Parse("""
            {"usage":{"input_tokens":2000,"output_tokens":300,
            "input_tokens_details":{"cached_tokens":1024},"output_tokens_details":{"reasoning_tokens":200}}}
            """))
        {
            var usage = FoundryAgentClient.ReadResponseUsage(response.RootElement);
            check(usage is { InputTokens: 2000, OutputTokens: 300, CachedTokens: 1024, ReasoningTokens: 200 },
                "Responses usage preserves billed input/output plus cached and reasoning token detail");
        }
        using (var response = JsonDocument.Parse("""{"usage":{"input_tokens":2,"output_tokens":3}}"""))
            check(FoundryAgentClient.ReadResponseUsage(response.RootElement) is { CachedTokens: null, ReasoningTokens: null },
                "missing optional token metrics remain unknown rather than invented zeros");
        foreach (var json in new[] { "{}", """{"usage":null}""", """{"usage":{"input_tokens":-1,"output_tokens":1}}""" })
        {
            using var response = JsonDocument.Parse(json);
            check(FoundryAgentClient.ReadResponseUsage(response.RootElement) is null,
                "missing or invalid usage is explicitly unavailable");
        }
        using (var handler = new FlowHandler(pollDelay: TimeSpan.FromMilliseconds(100)))
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(20) })
        {
            var reply = await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
            check(reply == "identified" && handler.Deletes == 2,
                "a slow Foundry request outlives the HTTP timeout but completes within the agent deadline");
        }
        using (var handler = new FlowHandler(pollDelay: TimeSpan.FromSeconds(10)))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http, TimeSpan.FromMilliseconds(100)).RunAsync(
                    "CardAgent", "identify", null, "catalogue", default);
                check(false, "a stalled HTTP request must respect the agent deadline");
            }
            catch (TimeoutException ex)
            {
                check(ex.Message.Contains("CardAgent") && handler.Cancels == 1 && handler.Deletes == 1,
                    "agent deadline interrupts a stalled HTTP request and cleans up the background response");
            }
        }
        using (var handler = new FlowHandler(pollDelay: TimeSpan.FromSeconds(10)))
        using (var http = new HttpClient(handler))
        using (var cancellation = new CancellationTokenSource())
        {
            var run = Client(http).RunAsync("CardAgent", "identify", null, "catalogue", cancellation.Token);
            cancellation.Cancel();
            try
            {
                await run;
                check(false, "caller cancellation must interrupt the request");
            }
            catch (OperationCanceledException)
            {
                check(handler.Cancels == 1 && handler.Deletes == 1,
                    "caller cancellation remains cancellation and cleanup uses an independent token");
            }
        }
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
        foreach (var failedStatus in new[] { false, true })
        {
            using var handler = new FlowHandler(toolFailure: "catalogue_transient_failure: timed out", failedStatus: failedStatus);
            using var http = new HttpClient(handler);
            var reply = await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
            check(reply == "identified" && handler.Creates == 3 && handler.Deletes == 3,
                $"explicit transient catalogue failure recovers on its own card history (failed status: {failedStatus})");
        }
        using (var handler = new FlowHandler(toolFailure: "catalogue_transient_failure: timed out", toolFailures: 10))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "persistent catalogue failure must remain explicit");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message.Contains("catalogue_transient_failure") && handler.Creates == 4 && handler.Deletes == 4,
                    "catalogue retries stop after two independent attempts and retain the failure");
            }
        }
        using (var handler = new FlowHandler())
        using (var http = new HttpClient(handler))
        {
            var validations = 0;
            var reply = await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default,
                validateReplyAsync: async (_, ct) =>
                {
                    await Task.Delay(1, ct);
                    if (++validations == 1) throw new InvalidOperationException("Title proof did not agree.");
                });
            check(reply == "identified" && validations == 2 && handler.Creates == 3,
                "asynchronous catalogue title verification retains the existing one-reread safety guard");
        }
        foreach (var failure in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.RequestTimeout,
            HttpStatusCode.ServiceUnavailable })
        {
            using var handler = new FlowHandler(pollFailure: failure, retryAfter: TimeSpan.FromSeconds(1));
            using var http = new HttpClient(handler);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var reply = await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
            check(reply == "identified" && handler.Creates == 2 && handler.Deletes == 2
                && timer.Elapsed >= TimeSpan.FromSeconds(1),
                $"polling {failure} honors Retry-After and resumes the existing response without recreating the card");
        }
        using (var handler = new FlowHandler(pollFailure: HttpStatusCode.BadRequest))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "permanent polling error must fail");
            }
            catch (FoundryAccessException ex)
            {
                check(ex.StatusCode == 400 && handler.Polls == 1 && handler.Creates == 1,
                    "permanent polling errors are not retried");
            }
        }
        using (var handler = new FlowHandler(failure: HttpStatusCode.ServiceUnavailable,
            pollFailure: HttpStatusCode.ServiceUnavailable, pollFailures: 3))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "transient retry exhaustion must fail");
            }
            catch (FoundryAccessException ex)
            {
                check(ex.StatusCode == 503 && handler.Creates == 2 && handler.Polls == 2
                    && handler.Cancels == 1 && handler.Deletes == 1,
                    "creation and polling share one bounded per-card service retry budget and clean up on exhaustion");
            }
        }
        using (var handler = new FlowHandler(pollFailure: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromSeconds(30)))
        using (var http = new HttpClient(handler))
        using (var cancellation = new CancellationTokenSource())
        {
            var run = Client(http).RunAsync("CardAgent", "identify", null, "catalogue", cancellation.Token);
            await handler.PollFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                await run;
                check(false, "cancellation during retry delay must propagate");
            }
            catch (OperationCanceledException)
            {
                check(handler.Polls == 1 && handler.Cancels == 1 && handler.Deletes == 1,
                    "cancellation interrupts per-card retry backoff and cleans up the known response");
            }
        }
        using (var handler = new FlowHandler(pollFailure: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromSeconds(30)))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await Client(http, TimeSpan.FromMilliseconds(100)).RunAsync("CardAgent", "identify", null, "catalogue", default);
                check(false, "deadline must bound retry delay");
            }
            catch (TimeoutException)
            {
                check(handler.Polls == 1 && handler.Deletes == 1,
                    "the same agent deadline bounds all retry waits");
            }
        }
        using (var handler = new ConcurrentRetryHandler())
        using (var http = new HttpClient(handler))
        {
            var client = Client(http);
            var replies = await Task.WhenAll(Enumerable.Range(0, 9).Select(index =>
                client.RunAsync("CardAgent", $"card-{index}", null, "catalogue", default)));
            check(replies.SequenceEqual(Enumerable.Range(0, 9).Select(index => $"card-{index}"))
                && handler.Creates.Count == 9 && handler.Creates.Values.All(count => count == 2)
                && handler.Polls.Count == 9 && handler.Polls.Values.All(count => count == 3)
                && handler.Deletes.Count == 9,
                "nine concurrent cards on one client have isolated histories, throttle/service retry budgets and cleanup");
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
        HttpStatusCode? failure = null, bool toolError = false, bool pending = false,
        TimeSpan pollDelay = default, HttpStatusCode? pollFailure = null, int pollFailures = 1,
        TimeSpan? retryAfter = null, string? toolFailure = null, bool failedStatus = false, int toolFailures = 1) : HttpMessageHandler
    {
        public int Creates { get; private set; }
        public int Deletes { get; private set; }
        public int Cancels { get; private set; }
        public int Images { get; private set; }
        public int Polls { get; private set; }
        public bool Continuation { get; private set; }
        public bool? Approved { get; private set; }
        public TaskCompletionSource PollFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                if (body.RootElement.TryGetProperty("reasoning", out _))
                    throw new InvalidOperationException("Named agents require reasoning on their definition, not per request.");
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
                if (pollFailure is not null && Polls <= pollFailures)
                {
                    var response = new HttpResponseMessage(pollFailure.Value) { Content = new StringContent("{}") };
                    if (retryAfter is not null)
                        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
                    PollFailed.TrySetResult();
                    return response;
                }
                if (pollDelay > TimeSpan.Zero) await Task.Delay(pollDelay, ct);
                if (pending || Polls == 1)
                    json = """{"status":"in_progress"}""";
                else if (_responses == 1)
                    json = JsonSerializer.Serialize(new { status = "completed", output = new[] {
                        new { type = "mcp_approval_request", id = "approval-1", server_label = label, name = "lookup" } } });
                else if (toolError)
                    json = """{"status":"completed","output":[{"type":"mcp_call","error":{"code":"unauthorized"}}]}""";
                else if (toolFailure is not null && _responses < 2 + toolFailures)
                    json = failedStatus
                        ? JsonSerializer.Serialize(new { status = "failed", error = new { code = "mcp_error", message = toolFailure } })
                        : JsonSerializer.Serialize(new { status = "completed", output = new[] {
                            new { type = "mcp_call", error = new { code = "tool_error", message = toolFailure } } } });
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

    private sealed class ConcurrentRetryHandler : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Creates { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Polls { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Deletes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string id;
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("previous_response_id", out _))
                    throw new InvalidOperationException("Independent cards must not reuse another response's history.");
                id = body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
                if (Creates.AddOrUpdate(id, 1, (_, count) => count + 1) == 1)
                    return Json(HttpStatusCode.TooManyRequests, "{}");
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id, status = "queued" }));
            }
            id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (request.Method == HttpMethod.Delete)
            {
                Deletes[id] = true;
                return Json(HttpStatusCode.OK, "{}");
            }
            var poll = Polls.AddOrUpdate(id, 1, (_, count) => count + 1);
            if (poll == 1) return Json(HttpStatusCode.ServiceUnavailable, "{}");
            if (poll == 2) throw new HttpRequestException("Transient polling connection failure.");
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                status = "completed", output = new[] {
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = id } } } }
            }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

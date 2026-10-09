using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Thin client over the Azure AI Foundry Agents data plane (create thread → run → read reply).
/// One instance is created per Foundry project endpoint, which is what makes the security boundary
/// visible: the worker holds a separate credential-scoped client for Team A and for Team B, and a
/// missing RBAC assignment surfaces as a 401/403 from exactly one of them.
/// </summary>
public sealed class FoundryAgentClient
{
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];

    // In preference order; see UploadImageAsync for why there is more than one.
    private static readonly string[] FilePurposes = ["assistants", "vision"];

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ILogger<FoundryAgentClient> _log;
    private readonly string _projectEndpoint;
    private readonly string _apiVersion;
    private readonly TimeSpan _runTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly int _throttleRetries;
    private readonly TimeSpan _throttleBackoff;

    public FoundryAgentClient(
        HttpClient http,
        TokenCredential credential,
        ILogger<FoundryAgentClient> log,
        string projectEndpoint,
        string apiVersion,
        TimeSpan runTimeout,
        TimeSpan pollInterval,
        int throttleRetries = 3,
        TimeSpan throttleBackoff = default)
    {
        _http = http;
        _credential = credential;
        _log = log;
        _projectEndpoint = projectEndpoint.TrimEnd('/');
        _apiVersion = apiVersion;
        _runTimeout = runTimeout > TimeSpan.Zero ? runTimeout : TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(2);
        _throttleRetries = throttleRetries >= 0 ? throttleRetries : 0;
        _throttleBackoff = throttleBackoff > TimeSpan.Zero ? throttleBackoff : TimeSpan.FromSeconds(10);
    }

    /// <summary>Project endpoint this client talks to; useful in error messages during the demo.</summary>
    public string ProjectEndpoint => _projectEndpoint;

    /// <summary>
    /// Runs <paramref name="agentId"/> against a single user message containing
    /// <paramref name="prompt"/> and, optionally, an image, then returns the agent's reply text.
    /// <para>
    /// The image is uploaded to the project and referenced by file id rather than passed as a URL.
    /// The storage account has no public endpoint, so no blob URL is fetchable by the Foundry
    /// service, which runs outside our VNet. Uploading also means each project receives only
    /// the bytes the orchestrator chose to send it, which is a tighter boundary than a
    /// container-scoped read grant.
    /// </para>
    /// <para>
    /// <paramref name="approvedServerLabel"/> is the one MCP server this agent is expected to call.
    /// Foundry pauses a run at every MCP tool call and waits to be told to go ahead, so something
    /// has to answer; this client approves calls to that label and refuses everything else. Pass
    /// <see langword="null"/> for an agent with no tools, which then refuses any call at all.
    /// </para>
    /// </summary>
    public async Task<string> RunAsync(
        string agentId, string prompt, AgentImage? image, string? approvedServerLabel, CancellationToken ct)
    {
        string? fileId = null;
        try
        {
            var content = new List<object> { new { type = "text", text = prompt } };
            if (image is not null)
            {
                fileId = await UploadImageAsync(image, ct);
                // "high" detail: a collector number is small print, and the default downsamples it
                // to the point where the catalogue lookup has nothing to work with.
                content.Add(new { type = "image_file", image_file = new { file_id = fileId, detail = "high" } });
            }

            using var thread = await SendAsync(
                HttpMethod.Post,
                $"/threads?api-version={_apiVersion}",
                new { messages = new[] { new { role = "user", content } } },
                ct);
            var threadId = RequireString(thread, "id", "thread id");

            await RunToCompletionAsync(agentId, threadId, approvedServerLabel, ct);

            return await ReadLastAssistantMessageAsync(threadId, ct);
        }
        finally
        {
            // An uploaded file outlives the thread, so without this a binder page leaves nineteen
            // images sitting in the project after every scan.
            if (fileId is not null) await TryDeleteFileAsync(fileId);
        }
    }

    /// <summary>
    /// Starts a run on an existing thread and polls it to completion, retrying throttling and
    /// terminal server errors within separate bounded budgets.
    /// </summary>
    /// <remarks>
    /// A token-per-minute limit is not a property of the request, so a run that failed on it would
    /// very likely have succeeded a few seconds later. It also arrives part-way through a page
    /// rather than at the start — the first few cards spend the minute's budget and the rest are
    /// refused — so without a retry a scan loses its last cards and nothing in the result says the
    /// cause was quota rather than the photograph.
    /// <para>
    /// The retry reuses the thread and the already-uploaded image: only the run is new, so waiting
    /// costs no extra tokens and the model sees exactly the same request. The wait is deliberately
    /// long because the limit is measured over a rolling minute; a brisk retry would simply be
    /// refused again and spend the remaining attempts without the budget ever having refilled.
    /// </para>
    /// </remarks>
    private async Task RunToCompletionAsync(
        string agentId, string threadId, string? approvedServerLabel, CancellationToken ct)
    {
        var throttleRetries = 0;
        var serverErrorRetries = 0;
        while (true)
        {
            string status;
            string? lastError;
            string? errorCode;

            try
            {
                using var run = await SendAsync(
                    HttpMethod.Post,
                    $"/threads/{threadId}/runs?api-version={_apiVersion}",
                    new { assistant_id = agentId },
                    ct);
                var runId = RequireString(run, "id", "run id");

                (status, lastError, errorCode) = await WaitForRunAsync(threadId, runId, approvedServerLabel, ct);
                if (IsCompleted(status)) return;
            }
            catch (FoundryAccessException ex) when (ex.StatusCode == 429 && throttleRetries < _throttleRetries)
            {
                // The same limit, refused one step earlier: the service declined to start the run
                // at all rather than starting it and failing it.
                await DelayAfterThrottleAsync(agentId, ex.Message, ++throttleRetries, ct);
                continue;
            }

            if (IsThrottled(errorCode, lastError) && throttleRetries < _throttleRetries)
            {
                await DelayAfterThrottleAsync(agentId, lastError, ++throttleRetries, ct);
                continue;
            }

            if (IsServerError(status, errorCode) && serverErrorRetries < MaxServerErrorRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, serverErrorRetries++));
                _log.LogWarning(
                    "Agent '{AgentId}' run failed with server_error; retry {Retry} of {Max} on thread "
                    + "{ThreadId} in {Delay}. {Error}",
                    agentId, serverErrorRetries, MaxServerErrorRetries, threadId, delay, lastError);
                await Task.Delay(delay, ct);
                continue;
            }

            // The job's Error field only carries ex.Message, so the reason has to travel with it.
            var reason = string.IsNullOrEmpty(lastError) ? string.Empty : $": {lastError}";
            var exhausted = IsThrottled(errorCode, lastError)
                ? $" The model deployment is rate limited and {_throttleRetries} retries did not clear it; "
                  + "the deployment's tokens-per-minute quota is shared by every project in the account."
                : IsServerError(status, errorCode)
                    ? $" {MaxServerErrorRetries} server_error retries were exhausted."
                    : string.Empty;
            throw new InvalidOperationException(
                $"Agent '{agentId}' run ended with status '{status}'{reason}.{exhausted}");
        }
    }

    private const int MaxServerErrorRetries = 2;

    internal static bool IsServerError(string status, string? errorCode) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
        && string.Equals(errorCode, "server_error", StringComparison.OrdinalIgnoreCase);

    private async Task DelayAfterThrottleAsync(string agentId, string? message, int attempt, CancellationToken ct)
    {
        var delay = RetryAfter(message) ?? TimeSpan.FromSeconds(_throttleBackoff.TotalSeconds * Math.Pow(2, attempt - 1));
        _log.LogWarning(
            "Agent '{AgentId}' was rate limited by its model deployment (attempt {Attempt} of {Max}); "
            + "waiting {Delay} before trying again. {Message}",
            agentId, attempt, _throttleRetries + 1, delay, AgentJson.Summarize(message));
        await Task.Delay(delay, ct);
    }

    /// <summary>
    /// Whether a failed run was refused for quota rather than for anything about the request.
    /// Internal so the decision can be self-tested: retrying the wrong failure turns one bad card
    /// into a minute of waiting, and not retrying this one loses the card.
    /// </summary>
    internal static bool IsThrottled(string? errorCode, string? message) =>
        string.Equals(errorCode, "rate_limit_exceeded", StringComparison.OrdinalIgnoreCase)
        || (errorCode is null
            && message is not null
            && message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the wait the service asked for out of its own message, when it gave one.
    /// </summary>
    /// <remarks>
    /// Azure OpenAI usually appends "Please retry after 37 seconds" to a throttling message, and
    /// that figure is better than any backoff we could guess because it is the service's own view
    /// of when the budget refills. It is not always present — the Foundry run's <c>last_error</c>
    /// sometimes carries only the bare sentence — so it cannot be relied on, only preferred.
    /// </remarks>
    internal static TimeSpan? RetryAfter(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            message,
            @"retry\s+after\s+(\d+)\s*(seconds?|s\b|milliseconds?|ms\b)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var value) || value <= 0) return null;

        var unit = match.Groups[2].Value;
        var span = unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase)
            || unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMilliseconds(value)
                : TimeSpan.FromSeconds(value);

        // A service that asks for an implausibly long wait would stall the whole job behind one
        // card, so the figure is taken as advice and not as an instruction.
        return span > MaxThrottleWait ? MaxThrottleWait : span;
    }

    /// <summary>Longest this client will wait on one throttled attempt, however long it is asked to.</summary>
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Uploads an image to the project and returns its file id.
    /// </summary>
    /// <remarks>
    /// The service's own enum carries both <c>assistants</c> and <c>vision</c>, and its
    /// documentation and its samples disagree about which applies to an agent image input. The
    /// samples use <c>assistants</c>, so that is tried first and <c>vision</c> is the fallback; a
    /// rejection shows up as a 400, not as a run failure further down.
    /// </remarks>
    private async Task<string> UploadImageAsync(AgentImage image, CancellationToken ct)
    {
        foreach (var purpose in FilePurposes)
        {
            try
            {
                using var document = await UploadAsync(image, purpose, ct);
                var id = RequireString(document, "id", "file id");
                _log.LogDebug("Uploaded {FileName} to {Endpoint} as {FileId} (purpose {Purpose}).",
                    image.FileName, _projectEndpoint, id, purpose);
                return id;
            }
            catch (FoundryAccessException ex) when (ex.StatusCode == 400 && purpose != FilePurposes[^1])
            {
                _log.LogWarning("Upload with purpose '{Purpose}' was rejected; retrying. {Message}", purpose, ex.Message);
            }
        }

        throw new UnreachableException();
    }

    private async Task<JsonDocument> UploadAsync(AgentImage image, string purpose, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);

        using var form = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(image.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        // The service reads the name from this part's Content-Disposition.
        form.Add(file, "file", image.FileName);
        form.Add(new StringContent(purpose), "purpose");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_projectEndpoint}/files?api-version={_apiVersion}")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync(response, HttpMethod.Post, "/files", ct);
    }

    /// <summary>
    /// Best-effort delete. A leaked file costs a little project storage and nothing else, so it
    /// must never turn a successful identification into a failed job — but it is logged, because
    /// repeated failures here are what fill a project up. <c>scripts/cleanup.ps1</c> sweeps them.
    /// </summary>
    private async Task TryDeleteFileAsync(string fileId)
    {
        try
        {
            // Deliberately not the caller's token: cleanup must still run when the job is cancelled.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var _ = await SendAsync(HttpMethod.Delete, $"/files/{fileId}?api-version={_apiVersion}", null, cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not delete uploaded file {FileId} from {Endpoint}.", fileId, _projectEndpoint);
        }
    }

    private static bool IsCompleted(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(string status) =>
        IsCompleted(status)
        || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Foundry parks a run here when it wants to call a tool and is waiting to be told it may.
    /// <para>
    /// This is not a terminal state and it is not an error, but nothing moves until the caller
    /// answers: a run left in <c>requires_action</c> simply sits there until the timeout, which
    /// looks exactly like a hung model.
    /// </para>
    /// </summary>
    private static bool RequiresAction(string status) =>
        string.Equals(status, "requires_action", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Polls the run to completion, answering any tool-approval request along the way.
    /// <para>
    /// Approval is a real decision, not a formality, and it is made here rather than in the agent:
    /// the agent asks to call a tool, and the orchestrator — which knows which server that agent is
    /// supposed to be using — decides. An agent that has been edited to reference some other team's
    /// server is refused here even if Foundry would have allowed it, so the boundary holds in two
    /// independent places.
    /// </para>
    /// </summary>
    private async Task<(string Status, string? LastError, string? ErrorCode)> WaitForRunAsync(
        string threadId, string runId, string? approvedServerLabel, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _runTimeout;
        var lastStatus = "unknown";
        var approvals = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var run = await SendAsync(HttpMethod.Get, $"/threads/{threadId}/runs/{runId}?api-version={_apiVersion}", null, ct);
            var status = RequireString(run, "status", "run status");

            if (!string.Equals(status, lastStatus, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogDebug("Foundry run {RunId} is {Status}.", runId, status);
                lastStatus = status;
            }

            if (IsTerminal(status))
            {
                string? reason = null;
                string? code = null;
                if (!IsCompleted(status) && run.RootElement.TryGetProperty("last_error", out var lastError))
                {
                    _log.LogError("Foundry run {RunId} failed: {Error}", runId, lastError.ToString());
                    reason = AgentJson.Summarize(lastError.ToString());
                    if (lastError.ValueKind == JsonValueKind.Object
                        && lastError.TryGetProperty("code", out var c)
                        && c.ValueKind == JsonValueKind.String)
                    {
                        code = c.GetString();
                    }
                }
                return (status, reason, code);
            }

            if (RequiresAction(status))
            {
                // A loop here would be the model calling a tool, being approved, and calling it
                // again. That is legitimate up to a point and a bug past it, and the run timeout
                // alone would not distinguish the two.
                if (++approvals > MaxToolApprovals)
                {
                    throw new InvalidOperationException(
                        $"Foundry run '{runId}' asked for tool approval more than {MaxToolApprovals} times; "
                        + "the agent is probably calling its tool in a loop.");
                }

                await ApproveToolCallsAsync(threadId, runId, run.RootElement, approvedServerLabel, ct);
                continue;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Foundry run '{runId}' did not complete within {_runTimeout} (last status '{lastStatus}').");
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    /// <summary>Most tool calls one run may ask approval for before it is treated as looping.</summary>
    private const int MaxToolApprovals = 10;

    /// <summary>
    /// Answers a <c>requires_action</c> run, approving tool calls that come from the server the
    /// caller named and refusing the rest.
    /// <para>
    /// Refusals are sent rather than dropped. Declining leaves the model to carry on without that
    /// tool and say so, which is recoverable; staying silent would hang the run until the timeout,
    /// which is not.
    /// </para>
    /// </summary>
    private async Task ApproveToolCallsAsync(
        string threadId, string runId, JsonElement run, string? approvedServerLabel, CancellationToken ct)
    {
        var decisions = BuildToolApprovals(run, approvedServerLabel, runId, _log);

        using var _ = await SendAsync(
            HttpMethod.Post,
            $"/threads/{threadId}/runs/{runId}/submit_tool_outputs?api-version={_apiVersion}",
            new { tool_approvals = decisions.Select(d => new { tool_call_id = d.ToolCallId, approve = d.Approve }) },
            ct);
    }

    /// <summary>One answer to one pending tool call.</summary>
    internal readonly record struct ToolApproval(string ToolCallId, bool Approve, string? ServerLabel, string ToolName);

    /// <summary>
    /// Decides which pending tool calls to allow. Internal so the decision can be self-tested
    /// without a live run, because getting it wrong either hangs the pipeline or quietly widens the
    /// boundary the demo is about.
    /// </summary>
    internal static IReadOnlyList<ToolApproval> BuildToolApprovals(
        JsonElement run, string? approvedServerLabel, string runId, ILogger? log = null)
    {
        if (!run.TryGetProperty("required_action", out var required))
            throw new InvalidOperationException($"Foundry run '{runId}' requires action but did not say what.");

        var actionType = required.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!string.Equals(actionType, "submit_tool_approval", StringComparison.OrdinalIgnoreCase))
        {
            // submit_tool_outputs means a function tool, which none of these agents declare: the
            // tools are all MCP servers the service calls itself. Guessing an output would be
            // worse than stopping.
            throw new InvalidOperationException(
                $"Foundry run '{runId}' is waiting on an action of type '{actionType}', which this "
                + "orchestrator does not supply. Only MCP tool approval is supported.");
        }

        if (!required.TryGetProperty("submit_tool_approval", out var approval) ||
            !approval.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Foundry run '{runId}' requested approval but listed no tool calls.");

        var decisions = new List<ToolApproval>();
        foreach (var call in calls.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            var label = call.TryGetProperty("server_label", out var l) ? l.GetString() : null;
            var name = (call.TryGetProperty("name", out var n) ? n.GetString() : null) ?? "(unnamed)";

            var approved = approvedServerLabel is not null
                && string.Equals(label, approvedServerLabel, StringComparison.OrdinalIgnoreCase);

            if (approved)
            {
                log?.LogInformation("Approving tool call {Tool} on '{Label}' for run {RunId}.", name, label, runId);
            }
            else
            {
                log?.LogError(
                    "Refusing tool call {Tool} for run {RunId}: it targets MCP server '{Label}', but this "
                    + "agent is only permitted to call '{Expected}'.",
                    name, runId, label ?? "(none)", approvedServerLabel ?? "(nothing)");
            }

            decisions.Add(new ToolApproval(id, approved, label, name));
        }

        if (decisions.Count == 0)
            throw new InvalidOperationException($"Foundry run '{runId}' requested approval but listed no usable tool calls.");

        return decisions;
    }

    private async Task<string> ReadLastAssistantMessageAsync(string threadId, CancellationToken ct)
    {
        using var messages = await SendAsync(
            HttpMethod.Get,
            $"/threads/{threadId}/messages?api-version={_apiVersion}&order=desc&limit=10",
            null,
            ct);

        if (!messages.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Foundry message list did not contain a 'data' array.");

        foreach (var message in data.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var role) || role.GetString() != "assistant") continue;
            if (!message.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.TryGetProperty("value", out var value))
                {
                    var reply = value.GetString();
                    if (!string.IsNullOrWhiteSpace(reply)) return reply;
                }
            }
        }

        throw new InvalidOperationException("Foundry run completed but produced no assistant text.");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);

        using var request = new HttpRequestMessage(method, _projectEndpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync(response, method, path, ct);
    }

    private async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // 401/403 here is the expected outcome of the "revoke the cross-project connection"
            // step in the Foundry boundary demo, so keep the endpoint in the message. The response
            // body reaches the caller through the job's Error field, so log it in full but only
            // summarise it into the exception.
            _log.LogError(
                "Foundry request {Method} {Path} against {Endpoint} failed with {StatusCode}: {Payload}",
                method, path, _projectEndpoint, (int)response.StatusCode, payload);

            throw new FoundryAccessException(
                $"Foundry request {method} {path} against {_projectEndpoint} failed with {(int)response.StatusCode}: {AgentJson.Summarize(payload)}",
                (int)response.StatusCode);
        }

        return JsonDocument.Parse(payload);
    }

    private static string RequireString(JsonDocument document, string property, string description)
    {
        if (!document.RootElement.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Foundry response did not contain a {description}.");
        return value.GetString()!;
    }
}

/// <summary>Raised when a Foundry data-plane call is rejected; carries the HTTP status code.</summary>
public sealed class FoundryAccessException : Exception
{
    public FoundryAccessException(string message, int statusCode) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }

    public bool IsAuthorizationFailure => StatusCode is 401 or 403;
}

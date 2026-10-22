using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Agents;

public sealed partial class FoundryAgentClient
{
    private async Task<string> RunResponseAsync(
        string agentName, string prompt, AgentImage? image, string? approvedServerLabel,
        CancellationToken ct, AgentImage? detailImage, Action<string>? validateReply)
    {
        if (string.IsNullOrWhiteSpace(agentName) || agentName.StartsWith("asst_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The Responses pipeline requires a named Foundry agent with its own agent identity, not a classic assistant ID.");

        var path = $"/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/openai/responses";
        var query = $"?api-version={Uri.EscapeDataString(_apiVersion)}";
        var content = new List<object> { new { type = "input_text", text = prompt } };
        AddImage(image);
        if (detailImage is not null)
        {
            content.Add(new
            {
                type = "input_text",
                text = "Second image: enlarged full-width footer of the same card, not another card. "
                    + "For a sideways crop it contains two stacked footer views from the two possible quarter-turns; "
                    + "use the one whose text reads upright and ignore the other. Use the full card for context; "
                    + "enlargement does not restore missing detail."
            });
            AddImage(detailImage);
        }

        object input = new[] { new { role = "user", content } };
        string? previousId = null;
        var responseIds = new HashSet<string>(StringComparer.Ordinal);
        var finishedIds = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        var regenerations = 0;
        var approvals = 0;
        var throttles = 0;
        var serverErrors = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_runTimeout);
        try
        {
            while (true)
            {
                var parentId = previousId;
                var body = new Dictionary<string, object>
                {
                    ["input"] = input, ["background"] = true, ["store"] = true
                };
                if (previousId is not null) body["previous_response_id"] = previousId;
                using var document = await SendResponseAsync(HttpMethod.Post, path + query, body);

                previousId = RequireString(document, "id", "response id");
                responseIds.Add(previousId);

                using var completed = await WaitForResponseAsync(previousId);
                finishedIds.Add(previousId);
                var root = completed.RootElement;
                LogResponseUsage(agentName, previousId, root);
                var status = root.GetProperty("status").GetString();
                if (!string.Equals(status, "completed", StringComparison.Ordinal))
                {
                    var error = root.TryGetProperty("error", out var value) ? value : default;
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c)
                        ? c.GetString() : null;
                    var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                        ? m.GetString() : null;
                    if (IsThrottled(code, message) && throttles < _throttleRetries)
                    {
                        previousId = parentId;
                        await DelayAsync(message, ++throttles, throttled: true);
                        continue;
                    }
                    if (IsServerError(status ?? "", code) && serverErrors < MaxServerErrorRetries)
                    {
                        _log.LogWarning("Agent {Agent} response {Response} failed with server_error; retry {Retry}.",
                            agentName, previousId, serverErrors + 1);
                        previousId = parentId;
                        await DelayAsync(message, ++serverErrors, throttled: false);
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Foundry agent '{agentName}' response '{previousId}' ended with '{status}': {message ?? root.GetRawText()}");
                }

                var decisions = BuildResponseApprovals(root, approvedServerLabel);
                if (decisions.Count > 0)
                {
                    if (++approvals > MaxToolApprovals)
                        throw new InvalidOperationException($"Foundry agent '{agentName}' exceeded {MaxToolApprovals} approval rounds.");
                    foreach (var decision in decisions)
                    {
                        if (decision.Approve)
                            _log.LogInformation("Approving tool {Tool} on {Label} for response {Response}.",
                                decision.ToolName, decision.ServerLabel, previousId);
                        else
                            _log.LogError("Refusing tool {Tool} on {Label} for response {Response}; expected {Expected}.",
                                decision.ToolName, decision.ServerLabel, previousId, approvedServerLabel);
                    }
                    input = decisions.Select(d => new
                    {
                        type = "mcp_approval_response", approval_request_id = d.ToolCallId, approve = d.Approve
                    }).ToArray();
                    continue;
                }

                var output = root.GetProperty("output");
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "mcp_call"
                        && item.TryGetProperty("error", out var toolError)
                        && toolError.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                        throw new InvalidOperationException($"Foundry MCP call failed: {toolError.GetRawText()}");

                var reply = string.Concat(output.EnumerateArray()
                    .Where(i => i.TryGetProperty("type", out var type) && type.GetString() == "message"
                        && i.TryGetProperty("role", out var role) && role.GetString() == "assistant")
                    .SelectMany(i => i.GetProperty("content").EnumerateArray())
                    .Where(p => p.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                    .Select(p => p.GetProperty("text").GetString()));
                if (string.IsNullOrWhiteSpace(reply))
                    throw new InvalidOperationException($"Foundry response '{previousId}' completed without assistant text.");
                try
                {
                    validateReply?.Invoke(reply);
                    _log.LogInformation("Agent {AgentId}: timing responses_and_reply, response {ResponseId}, elapsed {ElapsedMs} ms.",
                        agentName, previousId, timer.ElapsedMilliseconds);
                    return reply;
                }
                catch (Exception ex) when (validateReply is not null && ex is InvalidOperationException or JsonException)
                {
                    _log.LogWarning(ex, "Agent {Agent} response {Response} returned an invalid reply.", agentName, previousId);
                    if (regenerations++ > 0)
                        throw new InvalidOperationException($"Agent '{agentName}' returned an invalid reply after 2 attempts.", ex);
                    input = new[] { new { role = "user", content = "Your response could not be used: " + ex.Message
                        + " Re-examine the original images and return the entire requested JSON object again, without prose or fragments." } };
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Foundry agent '{agentName}' did not complete within {_runTimeout}.");
        }
        finally
        {
            _log.LogInformation(
                "Agent {AgentId}: response_summary elapsed_ms={ElapsedMs} responses={Responses} approvals={Approvals} corrections={Corrections} throttle_retries={ThrottleRetries} service_retries={ServiceRetries}.",
                agentName, timer.ElapsedMilliseconds, responseIds.Count, approvals, regenerations,
                throttles, serverErrors);
            foreach (var id in responseIds)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    if (!finishedIds.Contains(id))
                    {
                        using var cancelled = await SendAsync(HttpMethod.Post,
                            path + "/" + Uri.EscapeDataString(id) + "/cancel" + query, null, cleanup.Token);
                    }
                    using var deleted = await SendAsync(HttpMethod.Delete,
                        path + "/" + Uri.EscapeDataString(id) + query, null, cleanup.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or FoundryAccessException or OperationCanceledException)
                {
                    _log.LogWarning(ex, "Could not delete response {Response} from {Endpoint}.", id, _projectEndpoint);
                }
            }
        }

        async Task<JsonDocument> SendResponseAsync(HttpMethod method, string requestPath, object? body)
        {
            while (true)
            {
                try
                {
                    return await SendAsync(method, requestPath, body, timeout.Token);
                }
                catch (FoundryAccessException ex) when (ex.StatusCode == 429 && throttles < _throttleRetries)
                {
                    await DelayAsync(ex.Message, ++throttles, throttled: true, ex.RetryAfter);
                }
                catch (FoundryAccessException ex) when (ex.StatusCode is 408 or 500 or 502 or 503 or 504
                    && serverErrors < MaxServerErrorRetries)
                {
                    await DelayAsync(ex.Message, ++serverErrors, throttled: false, ex.RetryAfter);
                }
                // An interrupted POST may already have created background work. Only replay
                // transport failures for reads, where the known response ID can be resumed safely.
                catch (HttpRequestException ex) when (method == HttpMethod.Get && serverErrors < MaxServerErrorRetries)
                {
                    await DelayAsync(ex.Message, ++serverErrors, throttled: false);
                }
            }
        }

        async Task DelayAsync(string? message, int attempt, bool throttled, TimeSpan? retryAfter = null)
        {
            var delay = retryAfter ?? RetryAfter(message)
                ?? TimeSpan.FromSeconds((throttled ? _throttleBackoff.TotalSeconds : 1) * Math.Pow(2, attempt - 1));
            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxThrottleWait ? MaxThrottleWait : delay;
            delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
            _log.LogWarning(
                "Agent {Agent} response {Response}: transient {Kind} failure; retry {Retry} of {Max} in {Delay}. {Message}",
                agentName, previousId, throttled ? "throttle" : "service/transport", attempt,
                throttled ? _throttleRetries : MaxServerErrorRetries, delay, AgentJson.Summarize(message));
            await Task.Delay(delay, timeout.Token);
        }

        async Task<JsonDocument> WaitForResponseAsync(string id)
        {
            while (true)
            {
                var document = await SendResponseAsync(HttpMethod.Get, path + "/" + Uri.EscapeDataString(id) + query, null);
                var status = RequireString(document, "status", "response status");
                if (status is not "queued" and not "in_progress")
                    return document;
                document.Dispose();
                await Task.Delay(_pollInterval, timeout.Token);
            }
        }

        void AddImage(AgentImage? value)
        {
            if (value is not null)
                content.Add(new
                {
                    type = "input_image",
                    image_url = $"data:{value.ContentType};base64,{Convert.ToBase64String(value.Content.Span)}",
                    detail = "high"
                });
        }
    }

    private void LogResponseUsage(string agentName, string responseId, JsonElement response)
    {
        var usage = ReadResponseUsage(response);
        if (usage is null)
        {
            _log.LogWarning("Agent {AgentId} response {ResponseId}: token usage unavailable.", agentName, responseId);
            return;
        }
        _log.LogInformation(
            "Agent {AgentId} response {ResponseId}: token_usage input={InputTokens} output={OutputTokens} cached={CachedTokens} reasoning={ReasoningTokens}.",
            agentName, responseId, usage.InputTokens, usage.OutputTokens, usage.CachedTokens, usage.ReasoningTokens);
    }

    internal sealed record ResponseUsage(long InputTokens, long OutputTokens, long? CachedTokens, long? ReasoningTokens);

    internal static ResponseUsage? ReadResponseUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || Read(usage, "input_tokens") is not { } input || Read(usage, "output_tokens") is not { } output)
            return null;
        return new(input, output, Detail("input_tokens_details", "cached_tokens"), Detail("output_tokens_details", "reasoning_tokens"));

        long? Detail(string group, string name) =>
            usage.TryGetProperty(group, out var details) && details.ValueKind == JsonValueKind.Object ? Read(details, name) : null;
        static long? Read(JsonElement value, string name) =>
            value.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt64(out var number) && number >= 0 ? number : null;
    }

    internal static IReadOnlyList<ToolApproval> BuildResponseApprovals(JsonElement response, string? approvedLabel)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Foundry response did not contain an output array.");
        var result = new List<ToolApproval>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "mcp_approval_request")
                continue;
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Foundry requested MCP approval without an ID.");
            var label = item.TryGetProperty("server_label", out var l) ? l.GetString() : null;
            result.Add(new(id, approvedLabel is not null && string.Equals(label, approvedLabel, StringComparison.Ordinal),
                label, item.GetProperty("name").GetString() ?? "(unnamed)"));
        }
        return result;
    }
}

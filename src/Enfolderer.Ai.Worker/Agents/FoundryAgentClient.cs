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

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ILogger<FoundryAgentClient> _log;
    private readonly string _projectEndpoint;
    private readonly string _apiVersion;
    private readonly TimeSpan _runTimeout;
    private readonly TimeSpan _pollInterval;

    public FoundryAgentClient(
        HttpClient http,
        TokenCredential credential,
        ILogger<FoundryAgentClient> log,
        string projectEndpoint,
        string apiVersion,
        TimeSpan runTimeout,
        TimeSpan pollInterval)
    {
        _http = http;
        _credential = credential;
        _log = log;
        _projectEndpoint = projectEndpoint.TrimEnd('/');
        _apiVersion = apiVersion;
        _runTimeout = runTimeout > TimeSpan.Zero ? runTimeout : TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(2);
    }

    /// <summary>Project endpoint this client talks to; useful in error messages during the demo.</summary>
    public string ProjectEndpoint => _projectEndpoint;

    /// <summary>
    /// Runs <paramref name="agentId"/> against a single user message containing
    /// <paramref name="prompt"/> and, optionally, an image URL, then returns the agent's reply text.
    /// </summary>
    public async Task<string> RunAsync(string agentId, string prompt, Uri? imageUrl, CancellationToken ct)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        if (imageUrl is not null)
            content.Add(new { type = "image_url", image_url = new { url = imageUrl.ToString() } });

        using var thread = await SendAsync(
            HttpMethod.Post,
            $"/threads?api-version={_apiVersion}",
            new { messages = new[] { new { role = "user", content } } },
            ct);
        var threadId = RequireString(thread, "id", "thread id");

        using var run = await SendAsync(
            HttpMethod.Post,
            $"/threads/{threadId}/runs?api-version={_apiVersion}",
            new { assistant_id = agentId },
            ct);
        var runId = RequireString(run, "id", "run id");

        var status = await WaitForRunAsync(threadId, runId, ct);
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Agent '{agentId}' run ended with status '{status}'.");

        return await ReadLastAssistantMessageAsync(threadId, ct);
    }

    private async Task<string> WaitForRunAsync(string threadId, string runId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _runTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var run = await SendAsync(HttpMethod.Get, $"/threads/{threadId}/runs/{runId}?api-version={_apiVersion}", null, ct);
            var status = RequireString(run, "status", "run status");

            if (status is "completed" or "failed" or "cancelled" or "expired")
            {
                if (status != "completed" && run.RootElement.TryGetProperty("last_error", out var lastError))
                    _log.LogError("Foundry run {RunId} failed: {Error}", runId, lastError.ToString());
                return status;
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Foundry run '{runId}' did not complete within {_runTimeout}.");

            await Task.Delay(_pollInterval, ct);
        }
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
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // 401/403 here is the expected outcome of the "revoke the cross-project connection"
            // step in the Foundry boundary demo, so keep the endpoint in the message.
            throw new FoundryAccessException(
                $"Foundry request {method} {path} against {_projectEndpoint} failed with {(int)response.StatusCode}: {payload}",
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

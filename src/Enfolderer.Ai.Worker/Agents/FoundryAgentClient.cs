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
    /// <paramref name="prompt"/> and, optionally, an image, then returns the agent's reply text.
    /// <para>
    /// The image is uploaded to the project and referenced by file id rather than passed as a URL.
    /// The storage account is private, so a blob URL — SAS or not — is not something the Foundry
    /// service can fetch: it runs outside our VNet. Uploading also means each project receives only
    /// the bytes the orchestrator chose to send it, which is a tighter boundary than a
    /// container-scoped read grant.
    /// </para>
    /// </summary>
    public async Task<string> RunAsync(string agentId, string prompt, AgentImage? image, CancellationToken ct)
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

            using var run = await SendAsync(
                HttpMethod.Post,
                $"/threads/{threadId}/runs?api-version={_apiVersion}",
                new { assistant_id = agentId },
                ct);
            var runId = RequireString(run, "id", "run id");

            var (status, lastError) = await WaitForRunAsync(threadId, runId, ct);
            if (!IsCompleted(status))
            {
                // The job's Error field only carries ex.Message, so the reason has to travel with it.
                var reason = string.IsNullOrEmpty(lastError) ? string.Empty : $": {lastError}";
                throw new InvalidOperationException($"Agent '{agentId}' run ended with status '{status}'{reason}.");
            }

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

    private async Task<(string Status, string? LastError)> WaitForRunAsync(string threadId, string runId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _runTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var run = await SendAsync(HttpMethod.Get, $"/threads/{threadId}/runs/{runId}?api-version={_apiVersion}", null, ct);
            var status = RequireString(run, "status", "run status");

            if (IsTerminal(status))
            {
                string? reason = null;
                if (!IsCompleted(status) && run.RootElement.TryGetProperty("last_error", out var lastError))
                {
                    _log.LogError("Foundry run {RunId} failed: {Error}", runId, lastError.ToString());
                    reason = AgentJson.Summarize(lastError.ToString());
                }
                return (status, reason);
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

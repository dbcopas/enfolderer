using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Talks to the Azure-hosted scan API: create a job, upload the image with the returned write-only
/// SAS, submit it, then poll until the multi-agent pipeline produces a result document.
/// The desktop app holds no storage key and no client secret; it presents a user token obtained by
/// interactive Entra ID sign-in.
/// </summary>
public sealed class AiScanClient
{
    /// <summary>Polling cadence, matching the plan's 2 second baseline.</summary>
    public static readonly TimeSpan InitialPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound the backoff never exceeds.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Total time a single image may take before the client gives up.</summary>
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly Uri _apiBaseUrl;
    private readonly TokenCredential? _credential;
    private readonly string[] _scopes;

    public AiScanClient(HttpClient http, Uri apiBaseUrl, TokenCredential? credential, string[] scopes)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiBaseUrl = apiBaseUrl ?? throw new ArgumentNullException(nameof(apiBaseUrl));
        _credential = credential;
        _scopes = scopes ?? Array.Empty<string>();
    }

    /// <summary>Overall timeout applied to <see cref="ScanImageAsync"/>.</summary>
    public TimeSpan OverallTimeout { get; set; } = DefaultOverallTimeout;

    /// <summary>
    /// Runs one image through the pipeline and returns the versioned result document.
    /// </summary>
    public async Task<ScanResultDocument> ScanImageAsync(
        string imagePath,
        string? gameHint = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);

        var fileName = Path.GetFileName(imagePath);
        statusCallback?.Invoke($"Creating scan job for {fileName}...");

        var job = await PostAsync<CreateJobRequest, CreateJobResponse>(
            "jobs",
            new CreateJobRequest { FileName = fileName, GameHint = gameHint },
            ct);

        statusCallback?.Invoke($"Uploading {fileName}...");
        await UploadAsync(job.UploadUrl, imagePath, ct);

        statusCallback?.Invoke("Submitting job...");
        await PostAsync<object?, JobStatusResponse>($"jobs/{job.JobId}/submit", null, ct);

        return await PollAsync(job.JobId, statusCallback, ct);
    }

    /// <summary>Polls <c>GET /jobs/{id}</c> until the job reaches a terminal state.</summary>
    private async Task<ScanResultDocument> PollAsync(string jobId, Action<string>? statusCallback, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + OverallTimeout;
        var delay = InitialPollInterval;
        ScanJobStatus? lastReported = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var status = await GetAsync<JobStatusResponse>($"jobs/{jobId}", ct);

            if (lastReported != status.Status)
            {
                statusCallback?.Invoke(DescribeStatus(status));
                lastReported = status.Status;
            }

            switch (status.Status)
            {
                case ScanJobStatus.Completed:
                    return status.Result
                        ?? throw new InvalidOperationException("The scan completed but returned no result document.");

                case ScanJobStatus.Failed:
                    throw new InvalidOperationException(status.Error ?? "The scan failed without an error message.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Scan job {jobId} did not finish within {OverallTimeout}.");

            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(delay < remaining ? delay : remaining, ct);
            delay = NextDelay(delay);
        }
    }

    /// <summary>Exponential backoff, capped. Exposed for the self-tests.</summary>
    public static TimeSpan NextDelay(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled > MaxPollInterval ? MaxPollInterval : doubled;
    }

    /// <summary>Human-readable progress line for a poll response. Exposed for the self-tests.</summary>
    public static string DescribeStatus(JobStatusResponse status) => status.Status switch
    {
        ScanJobStatus.Pending => "Waiting for upload...",
        ScanJobStatus.Uploaded => "Queued for processing...",
        ScanJobStatus.DetectingBoundaries => "Finding card boundaries (cardgeo)...",
        ScanJobStatus.Identifying => status.CardsDetected > 0
            ? $"Identifying {status.CardsDetected} card(s) (cardid)..."
            : "Identifying cards (cardid)...",
        ScanJobStatus.Completed => $"Completed: {status.CardsIdentified}/{status.CardsDetected} card(s) identified.",
        ScanJobStatus.Failed => $"Failed: {status.Error}",
        _ => status.Status.ToString()
    };

    private async Task UploadAsync(string uploadUrl, string imagePath, CancellationToken ct)
    {
        await using var file = File.OpenRead(imagePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };

        // A blob SAS URL authenticates itself; the local development upload endpoint is on the API
        // and therefore still needs the caller's bearer token.
        if (IsApiUrl(uploadUrl))
            await AuthorizeAsync(request, ct);
        else
            request.Headers.Add("x-ms-blob-type", "BlockBlob");

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "upload the image", ct);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBaseUrl, path));
        if (body is not null) request.Content = JsonContent.Create(body, options: ScanJson.Options);
        await AuthorizeAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"POST {path}", ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBaseUrl, path));
        await AuthorizeAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"GET {path}", ct);
        return await ReadAsync<TResponse>(response, ct);
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_credential is null || _scopes.Length == 0) return;
        var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, ScanJson.Options)
            ?? throw new InvalidOperationException($"The scan API returned an empty {typeof(T).Name}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Failed to {what}: HTTP {(int)response.StatusCode}. {body}");
    }

    private bool IsApiUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        Uri.Compare(parsed, _apiBaseUrl, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Maps the result document onto the CSV rows the importer already understands. Only cards the
    /// agents fully identified are exported; the rest are reported as failures.
    /// Exposed for the self-tests.
    /// </summary>
    public static IReadOnlyList<BinderScanService.ScannedCard> MapCards(ScanResultDocument result) =>
        (result?.Cards ?? Array.Empty<IdentifiedCard>())
            .Where(c => c.IsIdentified)
            .Select(c => new BinderScanService.ScannedCard(c.Set!, c.CollectorNumber!, c.Name!))
            .ToList();
}

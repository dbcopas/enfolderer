using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Talks to the Azure-hosted scan API: create a job, PUT the image to the API, submit it, then poll
/// until the multi-agent pipeline produces a result document. The storage account has no public
/// endpoint, so every byte goes through the API; the desktop app holds no storage key and no client
/// secret, and presents a user token obtained by interactive Entra ID sign-in.
/// </summary>
public sealed class AiScanClient
{
    /// <summary>Polling cadence, matching the plan's 2 second baseline.</summary>
    public static readonly TimeSpan InitialPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound the backoff never exceeds.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(3);

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
        await UploadAsync(job.JobId, imagePath, ct);

        statusCallback?.Invoke("Submitting job...");
        await PostAsync<object?, JobStatusResponse>($"jobs/{job.JobId}/submit", null, ct);

        return await PollAsync(job.JobId, statusCallback, ct);
    }

    /// <summary>Polls <c>GET /jobs/{id}</c> until the job reaches a terminal state.</summary>
    private async Task<ScanResultDocument> PollAsync(string jobId, Action<string>? statusCallback, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + OverallTimeout;
        var delay = InitialPollInterval;
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var status = await GetAsync<JobStatusResponse>($"jobs/{jobId}", ct);

            statusCallback?.Invoke($"{DescribeStatus(status)} Elapsed {elapsed.Elapsed:mm\\:ss}.");

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
        ScanJobStatus.Identifying => status.ProgressMessage ?? (status.CardsDetected > 0
            ? $"Reading cards and checking catalogue/art (cardid): {status.CardsProcessed}/{status.CardsDetected} processed, "
                + $"{status.CardsIdentified} identified..."
            : "Identifying cards (cardid)..."),
        ScanJobStatus.Completed => $"Completed: {status.CardsIdentified}/{status.CardsDetected} card(s) identified.",
        ScanJobStatus.Failed => $"Failed: {status.Error}",
        _ => status.Status.ToString()
    };

    private async Task UploadAsync(string jobId, string imagePath, CancellationToken ct)
    {
        await using var file = File.OpenRead(imagePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(imagePath));

        // The route is fixed and built here rather than taken from the response, so there is no
        // server-supplied URL that could send the image, and the bearer token with it, anywhere
        // other than the API this client was configured to trust.
        var target = new Uri(_apiBaseUrl, $"jobs/{jobId}/content");
        using var request = new HttpRequestMessage(HttpMethod.Put, target) { Content = content };
        await AuthorizeAsync(request, ct);

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

    public async Task DownloadAnnotatedImageAsync(ScanResultDocument result, string outputPath, CancellationToken ct)
    {
        var expected = $"jobs/{Uri.EscapeDataString(result.JobId)}/annotated-image";
        if (result.AnnotatedImagePath != expected)
            throw new InvalidOperationException("The scan returned an unexpected annotated-image route.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBaseUrl, expected));
        await AuthorizeAsync(request, ct);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "download the annotated image", ct);
        if (response.Content.Headers.ContentType?.MediaType != "image/png")
            throw new InvalidDataException("The scan API did not return a PNG image.");
        const int maxBytes = 64 * 1024 * 1024;
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > maxBytes)
                throw new InvalidDataException("The annotated image exceeds the 64 MB download limit.");
            buffer.Write(block, 0, count);
        }
        if (buffer.Length < 8 || !buffer.GetBuffer().AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The annotated image has an invalid PNG signature.");
        await File.WriteAllBytesAsync(outputPath, buffer.ToArray(), ct);
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

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Maps the result document onto the CSV rows the importer already understands. Cards the
    /// agents identified are exported; the rest are reported as failures. Exposed for the
    /// self-tests.
    /// </summary>
    /// <remarks>
    /// A card may be identified without its collector number having been settled, and that row is
    /// still written — with the number column empty. The alternative is dropping the row, which
    /// loses the name too, and the name is the field a person can actually check against the
    /// binder. An empty number is a gap someone can fill; a missing row is one they have to find
    /// first.
    /// </remarks>
    public static IReadOnlyList<BinderScanService.ScannedCard> MapCards(ScanResultDocument result) =>
        (result?.Cards ?? Array.Empty<IdentifiedCard>())
            .Where(c => c.IsIdentified)
            .Select(c => new BinderScanService.ScannedCard(
                c.Set!, c.CollectorNumber ?? string.Empty, c.Name!, c.Language ?? "en", c.Finish))
            .ToList();
}

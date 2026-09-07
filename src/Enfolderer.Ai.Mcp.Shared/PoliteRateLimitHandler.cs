using System.Net;
using System.Net.Http.Headers;

namespace Enfolderer.Ai.Mcp;

/// <summary>
/// Keeps the catalogue servers inside the public APIs' published rate limits (Scryfall asks for
/// ~10 requests/second with a descriptive user agent) and adds a single retry on 429.
/// </summary>
public sealed class PoliteRateLimitHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumInterval;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public PoliteRateLimitHandler(TimeSpan minimumInterval) => _minimumInterval = minimumInterval;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendThrottledAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        response.Dispose();
        await Task.Delay(retryAfter, cancellationToken);

        using var retry = await CloneAsync(request, cancellationToken);
        return await SendThrottledAsync(retry, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendThrottledAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + _minimumInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        return await base.SendAsync(request, ct);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Shared HTTP defaults for the catalogue MCP servers.</summary>
public static class CatalogueHttp
{
    public const string UserAgent = "Enfolderer-CardCatalogMcp/1.0 (+https://github.com/dbcopas/enfolderer)";

    public static void ApplyDefaults(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromSeconds(30);
    }
}

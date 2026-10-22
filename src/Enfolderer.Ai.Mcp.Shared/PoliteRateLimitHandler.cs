using System.Net;
using System.Net.Http.Headers;

namespace Enfolderer.Ai.Mcp;

/// <summary>
/// Keeps the catalogue servers inside the public APIs' published rate limits (Scryfall asks for
/// ~10 requests/second with a descriptive user agent), with bounded retries for transient failures.
/// </summary>
public sealed class PoliteRateLimitHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumInterval;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _operationTimeout;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public PoliteRateLimitHandler(TimeSpan minimumInterval, TimeSpan? requestTimeout = null, TimeSpan? operationTimeout = null)
    {
        _minimumInterval = minimumInterval;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(2);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_operationTimeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var retry = attempt == 0 ? null : await CloneAsync(request, budget.Token);
                HttpResponseMessage response;
                try
                {
                    response = await SendThrottledAsync(retry ?? request, budget.Token);
                }
                catch (HttpRequestException ex) when (request.Method == HttpMethod.Get && attempt < 2
                    && (ex.StatusCode is null || IsTransient(ex.StatusCode.Value)))
                {
                    await DeferAsync(TimeSpan.FromSeconds(2 * (1 << attempt)), budget.Token);
                    continue;
                }
                if (request.Method != HttpMethod.Get || !IsTransient(response.StatusCode)) return response;
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(2 * (1 << attempt));
                try { await DeferAsync(delay, budget.Token); }
                catch
                {
                    response.Dispose();
                    throw;
                }
                if (attempt == 2) return response;
                response.Dispose();
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Catalogue request exhausted its cooldown/retry budget.", ex, HttpStatusCode.RequestTimeout);
        }
    }

    private async Task DeferAsync(TimeSpan delay, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = DateTimeOffset.UtcNow + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
            if (next > _notBefore) _notBefore = next;
        }
        finally { _gate.Release(); }
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private async Task<HttpResponseMessage> SendThrottledAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = _lastRequest + _minimumInterval;
            if (_notBefore > next) next = _notBefore;
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_requestTimeout);
        try { return await base.SendAsync(request, attempt.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Catalogue network request timed out.", ex, HttpStatusCode.RequestTimeout);
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            // Without these the retry can negotiate a different protocol from the original, which
            // makes a 429 that only reproduces on retry very hard to read.
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

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
        client.Timeout = Timeout.InfiniteTimeSpan;
    }
}

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Enfolderer.Ai.Mcp;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class CatalogueHttpChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var status in new[] { 408, 429, 500, 502, 503, 504 })
        {
            using var stub = new Stub((HttpStatusCode)status, HttpStatusCode.OK);
            using var client = Create(stub);
            using var response = await client.GetAsync("https://api.scryfall.com/cards/test");
            check(response.IsSuccessStatusCode && stub.Calls == 2,
                $"Catalogue HTTP retries transient {status} independently.");
        }

        foreach (var status in new[] { 400, 401, 403, 404 })
        {
            using var stub = new Stub((HttpStatusCode)status);
            using var client = Create(stub);
            using var response = await client.GetAsync("https://api.scryfall.com/cards/test");
            check((int)response.StatusCode == status && stub.Calls == 1,
                $"Catalogue HTTP does not retry permanent {status}.");
        }

        using (var stub = new Stub(HttpStatusCode.TooManyRequests))
        using (var client = Create(stub))
        using (var response = await client.GetAsync("https://api.scryfall.com/cards/test"))
            check(response.StatusCode == HttpStatusCode.TooManyRequests && stub.Calls == 3,
                "Catalogue HTTP preserves final error after two retries.");

        using (var stub = new Stub(HttpStatusCode.ServiceUnavailable))
        using (var client = Create(stub))
        using (var response = await client.PostAsync("https://api.scryfall.com/cards/test", null))
            check(stub.Calls == 1, "Catalogue HTTP does not replay writes.");

        using (var stub = new Stub(HttpStatusCode.TooManyRequests) { RetryDelay = TimeSpan.FromSeconds(10) })
        using (var client = Create(stub))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try
            {
                using var response = await client.GetAsync("https://api.scryfall.com/cards/test", cancellation.Token);
                check(false, "Catalogue Retry-After wait honors cancellation.");
            }
            catch (OperationCanceledException)
            {
                check(stub.Calls == 1, "Catalogue Retry-After wait honors cancellation.");
            }
        }

        using (var stub = new Stub(HttpStatusCode.TooManyRequests, HttpStatusCode.OK, HttpStatusCode.OK)
            { RetryDelay = TimeSpan.FromMilliseconds(80) })
        using (var client = Create(stub))
        {
            var timer = Stopwatch.StartNew();
            var first = client.GetAsync("https://api.scryfall.com/cards/one");
            var second = client.GetAsync("https://api.scryfall.com/cards/two");
            var responses = await Task.WhenAll(first, second);
            foreach (var response in responses) response.Dispose();
            check(timer.Elapsed >= TimeSpan.FromMilliseconds(70) && stub.Calls == 3
                && stub.SentAt.Skip(1).All(t => t >= TimeSpan.FromMilliseconds(70)),
                "Catalogue Retry-After delays subsequent requests as well as retries.");
        }

        using (var stub = new Stub(HttpStatusCode.TooManyRequests, HttpStatusCode.OK)
            { RetryDate = DateTimeOffset.UtcNow.AddMilliseconds(100) })
        using (var client = Create(stub))
        using (var response = await client.GetAsync("https://api.scryfall.com/cards/test"))
            check(response.IsSuccessStatusCode && stub.SentAt.ElementAt(1) >= TimeSpan.FromMilliseconds(80),
                "Catalogue HTTP honors date-based Retry-After.");

        using (var stub = new Stub(HttpStatusCode.OK))
        using (var client = Create(stub))
        {
            var catalogue = new ScryfallCatalogue(client);
            var first = await catalogue.SearchPrintingsAsync("Ugin's Labyrinth", "mh3", null, CancellationToken.None);
            var second = await catalogue.SearchPrintingsAsync("Ugin's Labyrinth", "mh3", null, CancellationToken.None);
            check(first.Count == 1 && second.SequenceEqual(first) && stub.Calls == 1,
                "Repeated printing searches reuse successful results without changing candidates.");
            await catalogue.SearchPrintingsAsync("Ugin's Labyrinth", null, null, CancellationToken.None);
            check(stub.Calls == 2, "Printing search cache keeps set-specific and all-set results separate.");
        }

        using (var stub = new Stub(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK))
        using (var client = Create(stub))
        {
            var catalogue = new ScryfallCatalogue(client);
            try
            {
                await catalogue.SearchPrintingsAsync("Ugin's Labyrinth", "mh3", null, CancellationToken.None);
                check(false, "Transient search errors are surfaced.");
            }
            catch (HttpRequestException e)
            {
                check(e.StatusCode == HttpStatusCode.ServiceUnavailable, "Transient search errors are surfaced.");
            }
            var recovered = await catalogue.SearchPrintingsAsync("Ugin's Labyrinth", "mh3", null, CancellationToken.None);
            check(recovered.Count == 1 && stub.Calls == 4, "Transient search errors are never cached.");
        }

        using (var stub = new Stub(HttpStatusCode.Unauthorized))
        using (var client = Create(stub))
        {
            var catalogue = new ScryfallCatalogue(client);
            try
            {
                await catalogue.LookupBySetAndNumberAsync("mh3", "233", CancellationToken.None);
                check(false, "Catalogue authorization errors are not mistaken for missing printings.");
            }
            catch (HttpRequestException e)
            {
                check(e.StatusCode == HttpStatusCode.Unauthorized,
                    "Catalogue authorization errors are not mistaken for missing printings.");
            }
        }
    }

    private static HttpClient Create(HttpMessageHandler stub) =>
        new(new PoliteRateLimitHandler(TimeSpan.Zero) { InnerHandler = stub });

    private sealed class Stub(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public TimeSpan RetryDelay { get; init; } = TimeSpan.Zero;
        public DateTimeOffset? RetryDate { get; init; }
        public ConcurrentQueue<TimeSpan> SentAt { get; } = new();
        private readonly Stopwatch _timer = Stopwatch.StartNew();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = statuses[Math.Min(Interlocked.Increment(ref _calls) - 1, statuses.Length - 1)];
            SentAt.Enqueue(_timer.Elapsed);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("""
                    {"data":[{"set":"mh3","collector_number":"233","name":"Ugin's Labyrinth","lang":"en"}],"has_more":false}
                    """)
            };
            response.Headers.RetryAfter = RetryDate is { } date
                ? new RetryConditionHeaderValue(date)
                : new RetryConditionHeaderValue(RetryDelay);
            return Task.FromResult(response);
        }
    }
}

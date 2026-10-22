using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class ScannedCollectionChecks
{
    public static async Task RunAsync(Action<bool, string> check, HttpClient client, IJobStore jobs,
        ICollectionSnapshotStore snapshots, string tenant, string userA, string userB)
    {
        Console.WriteLine("\nScanned collections: exact printing quantities, idempotency and isolation");
        void Caller(string? user)
        {
            client.DefaultRequestHeaders.Remove("X-Test-User");
            if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        }
        async Task<JsonElement> View(string suffix = "")
        {
            using var response = await client.GetAsync("/scanned-collection/" + suffix);
            response.EnsureSuccessStatusCode();
            check(response.Headers.CacheControl?.NoStore == true, "scanned collection responses prohibit shared caching");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        async Task<string> Export(string suffix = "")
        {
            using var response = await client.GetAsync("/scanned-collection/cards.csv" + suffix);
            response.EnsureSuccessStatusCode();
            check(response.Headers.CacheControl?.NoStore == true, "collection CSV prohibits caching");
            check(response.Content.Headers.ContentType?.MediaType == "text/csv"
                && response.Content.Headers.ContentType.CharSet == "utf-8"
                && response.Content.Headers.ContentDisposition?.DispositionType == "attachment",
                "collection CSV is a UTF-8 attachment");
            return await response.Content.ReadAsStringAsync();
        }
        IdentifiedCard Card(string set, string number, string language = "en") => new()
        {
            Game = CardGames.Magic, Set = set, CollectorNumber = number, Language = language, Name = "Swords to Plowshares"
        };
        var jobId = Guid.NewGuid().ToString("N");
        var job = new ScanJobDocument
        {
            Id = jobId, JobId = jobId, OwnerTenantId = tenant, OwnerObjectId = userA, Status = ScanJobStatus.Completed,
            Result = new ScanResultDocument
            {
                Cards =
                [
                    Card("CMM", "123"), Card("cmm", "0123"), Card("CMM", "123"),
                    Card("CMM", "124"), Card("STA", "010"), Card("CMM", "123", "ja"),
                    Card("CMM", "125") with { Error = "Identification failed." },
                    Card("CMM", ""), Card("CMM", "127") with { Game = CardGames.Unknown }
                ]
            }
        };
        await jobs.CreateAsync(job);
        foreach (var caller in new[] { "", "missing-scope", "missing-claims" })
        {
            Caller(caller.Length == 0 ? null : caller);
            using var read = await client.GetAsync("/scanned-collection/");
            using var export = await client.GetAsync("/scanned-collection/cards.csv");
            using var add = await client.PostAsync($"/jobs/{jobId}/collection", null);
            check(read.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                && add.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                && export.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "scanned collection requires authentication, identity claims and delegated scan scope");
        }
        Caller(userA);
        var empty = await View();
        check(empty.GetProperty("totalQuantity").GetInt64() == 0 && empty.GetProperty("printings").GetArrayLength() == 0,
            "new scanned collection is empty without requiring a desktop snapshot");
        check(await Export() == "Name;Set;Number;Language;Quantity\r\n", "empty collection exports a header-only CSV");
        var escaped = MobileWebEndpoints.Csv(new ScannedCollectionPrinting[]
        {
            new("CMM", "12", "ja", "名前; \"quoted\"\r\nnext", 2),
            new("CMM", "-1", "en", " \t=SUM(1)", 1),
            new("CMM", "@number", "en", "+formula", 1)
        });
        check(escaped.Contains("\"名前; \"\"quoted\"\"\r\nnext\";CMM;12;ja;2\r\n", StringComparison.Ordinal)
            && escaped.Contains("' \t=SUM(1);CMM;'-1;en;1\r\n", StringComparison.Ordinal)
            && escaped.Contains("'+formula;CMM;'@number;en;1\r\n", StringComparison.Ordinal),
            "collection CSV preserves Unicode, quotes separators/newlines and neutralizes spreadsheet formulas");
        var snapshot = new CollectionSnapshot { Printings = [new("CMM", "123", 99, 99)] };
        await snapshots.WriteAsync(tenant, userA, snapshot);
        var balanceBefore = await client.GetFromJsonAsync<JsonElement>("/allowance/");
        using var notYetAdded = await client.GetAsync($"/jobs/{jobId}/collection");
        check(!(await notYetAdded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetBoolean(),
            "completed scan reports whether it has been added");
        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => client.PostAsJsonAsync($"/jobs/{jobId}/collection", new { quantity = 999, ownerObjectId = userB })));
        try
        {
            var added = 0;
            foreach (var response in responses)
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.GetProperty("added").GetBoolean()) added++;
                check(body.GetProperty("quantity").GetInt32() == 6 && body.GetProperty("skipped").GetInt32() == 3,
                    "collection addition uses stored identified results, not client-supplied quantities");
            }
            check(added == 1, "twelve concurrent clicks add one scan exactly once");
        }
        finally { foreach (var response in responses) response.Dispose(); }
        var first = await View();
        check(first.GetProperty("totalPrintings").GetInt32() == 4 && first.GetProperty("totalQuantity").GetInt64() == 6,
            "same names in different sets, collector numbers and languages remain distinct printings");
        var cmm = first.GetProperty("printings").EnumerateArray()
            .Single(p => p.GetProperty("set").GetString() == "CMM" && p.GetProperty("number").GetString() == "123"
                && p.GetProperty("language").GetString() == "en");
        check(cmm.GetProperty("quantity").GetInt32() == 3,
            "case and leading-zero normalization groups copies of only the same printing");
        var secondId = Guid.NewGuid().ToString("N");
        await jobs.CreateAsync(job with
        {
            Id = secondId, JobId = secondId,
            Result = new ScanResultDocument { Cards = [Card("CMM", "123")] }
        });
        using var secondAdd = await client.PostAsync($"/jobs/{secondId}/collection", null);
        secondAdd.EnsureSuccessStatusCode();
        var filtered = await View("?set=cmm");
        check(filtered.GetProperty("totalQuantity").GetInt64() == 7
            && filtered.GetProperty("printings").GetArrayLength() == 3
            && filtered.GetProperty("printings").EnumerateArray().All(p => p.GetProperty("set").GetString() == "CMM")
            && filtered.GetProperty("sets").GetArrayLength() == 2,
            "a separate scan increases exact printing quantity and filtering retains all available set choices");
        var noMatches = await View("?set=ZZZ");
        var allCsv = await Export();
        check(allCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 5
            && allCsv.Contains("Swords to Plowshares;CMM;123;en;4\r\n", StringComparison.Ordinal)
            && allCsv.Contains("Swords to Plowshares;CMM;123;ja;1\r\n", StringComparison.Ordinal)
            && allCsv.Contains("Swords to Plowshares;STA;10;en;1\r\n", StringComparison.Ordinal),
            "CSV exports exact saved quantities and language rows without uploaded snapshot quantities");
        var setCsv = await Export("?set=%20cmm%20");
        check(setCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 4
            && !setCsv.Contains(";STA;", StringComparison.Ordinal),
            "CSV uses the same normalized set filter as browsing");
        check(await Export("?set=ZZZ") == "Name;Set;Number;Language;Quantity\r\n",
            "unknown CSV set produces a header-only download");
        using var invalidCsv = await client.GetAsync("/scanned-collection/cards.csv?set=%3Cscript%3E");
        check(invalidCsv.StatusCode == HttpStatusCode.BadRequest, "CSV rejects malformed set filters");
        using var invalidSet = await client.GetAsync("/scanned-collection/?set=%3Cscript%3E");
        check(noMatches.GetProperty("printings").GetArrayLength() == 0 && invalidSet.StatusCode == HttpStatusCode.BadRequest,
            "unknown sets have no matches and malformed set filters are rejected");
        using var alreadyAdded = await client.GetAsync($"/jobs/{jobId}/collection");
        check((await alreadyAdded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetBoolean(),
            "reloading the page can restore the already-added button state");
        var unchangedSnapshot = await snapshots.ReadAsync(tenant, userA);
        check(unchangedSnapshot?.Printings.Single().Quantity == 99
            && unchangedSnapshot.ExportedAt == snapshot.ExportedAt,
            "scanned additions never mutate or merge uploaded snapshot quantities");
        await snapshots.DeleteAsync(tenant, userA);
        check((await View()).GetProperty("totalQuantity").GetInt64() == 7,
            "deleting an uploaded snapshot preserves scanned additions");
        var collectionState = await client.GetFromJsonAsync<JsonElement>("/collection/");
        check(!collectionState.GetProperty("available").GetBoolean()
            && collectionState.GetProperty("scannedPrintings").GetInt32() == 4
            && collectionState.GetProperty("scannedQuantity").GetInt64() == 7,
            "collection metadata exposes scanned-only ownership without pretending an uploaded snapshot exists");
        using var scannedOnly = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest
        {
            UseCollection = true, AutoAddToCollection = true
        });
        scannedOnly.EnsureSuccessStatusCode();
        var created = await scannedOnly.Content.ReadFromJsonAsync<CreateJobResponse>();
        var stored = await jobs.GetAsync(created!.JobId);
        check(stored is { UseCollection: true, AutoAddToCollection: true },
            "scanned-only users can enable ownership and persist auto-add independently on a new scan");
        var balanceAfter = await client.GetFromJsonAsync<JsonElement>("/allowance/");
        check(balanceBefore.GetProperty("remaining").GetInt64() == balanceAfter.GetProperty("remaining").GetInt64(),
            "saving and browsing a collection do not consume scan credits");

        var unfinished = Guid.NewGuid().ToString("N");
        await jobs.CreateAsync(job with { Id = unfinished, JobId = unfinished, Status = ScanJobStatus.Identifying });
        using var premature = await client.PostAsync($"/jobs/{unfinished}/collection", null);
        var emptyId = Guid.NewGuid().ToString("N");
        await jobs.CreateAsync(job with { Id = emptyId, JobId = emptyId, Result = new ScanResultDocument() });
        using var emptyAdd = await client.PostAsync($"/jobs/{emptyId}/collection", null);
        check(premature.StatusCode == HttpStatusCode.Conflict && emptyAdd.StatusCode == HttpStatusCode.BadRequest,
            "unfinished scans and scans without identified printings cannot be saved");

        Caller(userB);
        check(await Export() == "Name;Set;Number;Language;Quantity\r\n",
            "CSV cannot export another user's collection");
        using var stolen = await client.PostAsync($"/jobs/{jobId}/collection", null);
        using var stolenStatus = await client.GetAsync($"/jobs/{jobId}/collection");
        check(stolen.StatusCode == HttpStatusCode.NotFound && stolenStatus.StatusCode == HttpStatusCode.NotFound
            && (await View()).GetProperty("totalQuantity").GetInt64() == 0,
            "another user cannot save or inspect the owner's scan or collection");
        var bId = Guid.NewGuid().ToString("N");
        await jobs.CreateAsync(job with
        {
            Id = bId, JobId = bId, OwnerObjectId = userB,
            Result = new ScanResultDocument { Cards = [Card("STA", "10")] }
        });
        using var bAdd = await client.PostAsync($"/jobs/{bId}/collection", null);
        check(bAdd.IsSuccessStatusCode && (await View()).GetProperty("totalQuantity").GetInt64() == 1,
            "users with no scan credits can still save completed scans to their own separate collection");
        check(await Export() == "Name;Set;Number;Language;Quantity\r\nSwords to Plowshares;STA;10;en;1\r\n",
            "users with no scan credits can export only their own collection");
        Caller(userA);
        check((await View()).GetProperty("totalQuantity").GetInt64() == 7, "different users' additions never mix");
        await BlobChecks(check, tenant, userA, userB, job);
    }

    private static async Task BlobChecks(Action<bool, string> check, string tenant, string userA, string userB, ScanJobDocument job)
    {
        using var wire = new BlobWire();
        using var http = new HttpClient(wire);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var service = new BlobServiceClient(new Uri("https://collection.test"), options);
        var firstReplica = new BlobScannedCollectionStore(service);
        var secondReplica = new BlobScannedCollectionStore(service);
        var addition = ScannedCollectionAddition.FromResult(job.JobId, job.Result!);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? firstReplica : secondReplica).AddAsync(tenant, userA, addition)));
        check(outcomes.Count(v => v) == 1 && wire.Documents.Count == 1 && wire.ConditionalWrites == 12,
            "real Blob SDK sends create-if-absent writes so concurrent API replicas persist one scan receipt");
        check(BlobScannedCollectionStore.BlobName(tenant, userA, job.JobId)
            != BlobScannedCollectionStore.BlobName(tenant, userB, job.JobId)
            && !wire.Documents.Keys.Single().EndsWith("/collection.json"),
            "blob receipts are owner-scoped and cannot overwrite desktop snapshot blobs");
        var persisted = await new BlobScannedCollectionStore(service).ReadAsync(tenant, userA);
        check(persisted.Count == 4 && persisted.Sum(p => p.Quantity) == 6,
            "a fresh Blob-backed store lists and reads durable scan receipts with exact quantities");
        check((await firstReplica.ReadAsync(tenant, userA, excludingJobId: job.JobId)).Count == 0,
            "Blob-backed ownership reads exclude a retried scan's own already-saved contribution");
        await secondReplica.AddAsync(tenant, userB, addition);
        var isolated = await firstReplica.ReadAsync(tenant, userA);
        check(isolated.Count == 4 && isolated.Sum(p => p.Quantity) == 6
            && await firstReplica.ContainsAsync(tenant, userA, job.JobId),
            "Blob prefix listing isolates users and the saved-scan status survives new store instances");
        wire.Fail = true;
        try
        {
            await firstReplica.AddAsync(tenant, userA, addition);
            check(false, "blob authorization errors must not become already-added success responses");
        }
        catch (RequestFailedException e)
        {
            check(e.Status == 403, "blob authorization errors are surfaced rather than reported as successful additions");
        }
    }

    private sealed class BlobWire : HttpMessageHandler
    {
        public ConcurrentDictionary<string, byte[]> Documents { get; } = new();
        private int _conditionalWrites;
        public int ConditionalWrites => _conditionalWrites;
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return Error(HttpStatusCode.Forbidden, "AuthorizationFailure");
            if (request.Method == HttpMethod.Put)
            {
                if (request.Headers.IfNoneMatch.Any(t => t.Tag == "*")) Interlocked.Increment(ref _conditionalWrites);
                if (!Documents.TryAdd(request.RequestUri!.AbsolutePath, await request.Content!.ReadAsByteArrayAsync(ct)))
                    return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                return Success(HttpStatusCode.Created, []);
            }
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.Method == HttpMethod.Get && query.TryGetValue("comp", out var comp) && comp == "list")
            {
                var prefix = "/collections/" + query["prefix"];
                var listing = new XElement("EnumerationResults",
                    new XAttribute("ServiceEndpoint", "https://collection.test/"),
                    new XAttribute("ContainerName", "collections"),
                    new XElement("Blobs", Documents.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(p => new XElement("Blob",
                            new XElement("Name", p.Key["/collections/".Length..]),
                            new XElement("Properties",
                                new XElement("Last-Modified", DateTimeOffset.UtcNow.ToString("R")),
                                new XElement("Etag", "\"receipt\""),
                                new XElement("Content-Length", p.Value.Length),
                                new XElement("BlobType", "BlockBlob"))))),
                    new XElement("NextMarker", ""));
                return Success(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(listing.ToString()));
            }
            if (!Documents.TryGetValue(request.RequestUri.AbsolutePath, out var content))
                return Error(HttpStatusCode.NotFound, "BlobNotFound");
            if (request.Method == HttpMethod.Head)
            {
                var properties = Success(HttpStatusCode.OK, []);
                properties.Content.Headers.ContentLength = content.Length;
                return properties;
            }
            if (request.Method == HttpMethod.Get)
            {
                var download = Success(HttpStatusCode.PartialContent, content);
                download.Content.Headers.ContentRange = new(0, content.Length - 1, content.Length);
                return download;
            }
            throw new InvalidOperationException("Unexpected blob operation in storage test.");
        }

        private static HttpResponseMessage Success(HttpStatusCode status, byte[] content)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(content) };
            response.Headers.ETag = new("\"receipt\"");
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent($"<Error><Code>{code}</Code><Message>Test storage response.</Message></Error>",
                    System.Text.Encoding.UTF8, "application/xml")
            };
            response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            return response;
        }
    }
}

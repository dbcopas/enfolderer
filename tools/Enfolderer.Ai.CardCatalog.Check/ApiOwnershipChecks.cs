using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Allowances;
using Enfolderer.Ai.Infrastructure.Storage;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: WebApplicationFactoryContentRoot("Enfolderer.Ai.Api", ".", "Enfolderer.Ai.Api.dll", "0")]
[assembly: WebApplicationFactoryContentRoot("Enfolderer.Ai.Mcp.CardCatalog.Mtg", ".", "Enfolderer.Ai.Mcp.CardCatalog.Mtg.dll", "0")]

namespace Enfolderer.Ai.CardCatalog.Check;

internal static class ApiOwnershipChecks
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string UserA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string UserB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var timestamp = new DateTimeOffset(2026, 10, 20, 14, 9, 21, 995, TimeSpan.FromHours(2));
        var prefix = ScanBlobPaths.BuildJobPrefix("job", timestamp, "Douglas");
        check(prefix == "Douglas-261020-1209-job"
            && prefix == ScanBlobPaths.BuildJobPrefix("job", timestamp.ToUniversalTime(), "Douglas")
            && string.CompareOrdinal(prefix, ScanBlobPaths.BuildJobPrefix("job", timestamp.AddMinutes(1), "Douglas")) < 0,
            "named blob prefixes use the exact compact UTC format and sort by minute within a creator");
        check(prefix == ScanBlobPaths.BuildJobPrefix("job", timestamp.AddMilliseconds(1), "Douglas")
            && ScanBlobPaths.BuildJobPrefix("one", timestamp, "Douglas") != ScanBlobPaths.BuildJobPrefix("two", timestamp, "Douglas"),
            "minute-precision names omit seconds while job IDs prevent same-minute collisions");
        check(ScanBlobPaths.BuildJobPrefix("job", new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(2)), "Douglas")
            == "Douglas-251231-2300-job", "compact dates follow the UTC day and year, not local midnight");
        check(ScanBlobPaths.BuildJobPrefix("job", timestamp, "../../D\u00f6uglas/..") == prefix
            && ScanBlobPaths.SanitizePersonName(new string('A', 200)).Length == 64,
            "creator labels normalize accents, strip path characters and bound name length");
        try
        {
            ScanBlobPaths.BuildJobPrefix("job", timestamp, "../");
            check(false, "an unusable creator label must not silently create an unnamed prefix");
        }
        catch (ArgumentException) { check(true, "an unusable creator label fails explicitly before storage"); }
        foreach (var (claims, expected) in new (Claim[], string?)[]
        {
            ([new("given_name", "Douglas"), new("name", "Wrong Person")], "Douglas"),
            ([new(ClaimTypes.GivenName, "Felix"), new("name", "Wrong Person")], "Felix"),
            ([new("given_name", " "), new("name", "  Alvero  Example")], "Alvero"),
            ([new(ClaimTypes.Name, "  Adrien  Example")], "Adrien"),
            ([new("given_name", "../"), new("name", "  Jos\u00e9 Example")], "Jose"),
            ([new("name", "person@example.com")], null),
            ([new("name", "\u6771\u4eac")], null),
            ([], null)
        })
            check(JobOwnerFilter.ReadFirstName(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))) == expected,
                "creator labels use trusted given-name claims, then a usable first display-name word, never email");
        check(JobOwnerFilter.ReadFirstName(new ClaimsPrincipal(new ClaimsIdentity([new Claim("given_name", "Douglas")]))) is null,
            "unauthenticated name claims cannot supply a creator label");
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            check(ScanBlobPaths.BuildJobPrefix("job", timestamp, "Douglas") == prefix,
                "blob date prefixes are independent of the machine's culture and calendar");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        var oldDocument = System.Text.Json.JsonSerializer.Deserialize<ScanJobDocument>("""{"jobId":"legacy"}""")!;
        check(oldDocument.BlobPrefix is null
            && ScanBlobPaths.BuildAnnotatedBlobPath(oldDocument) == "outputs/legacy/priced.png"
            && ScanBlobPaths.BuildCropBlobName(oldDocument, 0) == "legacy/card-000.png",
            "persisted legacy jobs without a blob prefix retain their original output and crop paths");
        var timestampOnly = oldDocument with { BlobPrefix = "20261020T120921995Z-legacy" };
        check(ScanBlobPaths.BuildAnnotatedBlobPath(timestampOnly) == "outputs/20261020T120921995Z-legacy/priced.png"
            && ScanBlobPaths.BuildCropBlobName(timestampOnly, 0) == "20261020T120921995Z-legacy/card-000.png",
            "existing long-timestamp folders keep their persisted paths unchanged");
        Console.WriteLine("\nReal API routes: two-user ownership isolation");
        var store = new InMemoryJobStore();
        var images = new Images();
        var queue = new Queue();
        var allowances = new InMemoryScanAllowanceStore();
        await allowances.AdjustAsync(ScanAllowanceOwner.Key(Tenant, UserA), Guid.NewGuid(), 10, false,
            UserA, "Test grant");
        await using var factory = new WebApplicationFactory<JobOwnerFilter>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Directory.GetCurrentDirectory());
            builder.UseWebRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Enfolderer.Ai.Api", "wwwroot"));
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", Tenant);
            builder.UseSetting("AzureAd:ClientId", UserA);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJobStore>();
                services.RemoveAll<IScanImageStore>();
                services.RemoveAll<IJobQueue>();
                services.AddSingleton<IJobStore>(store);
                services.AddSingleton<IScanImageStore>(images);
                services.AddSingleton<IJobQueue>(queue);
                services.RemoveAll<IScanAllowanceStore>();
                services.AddSingleton<IScanAllowanceStore>(allowances);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "OwnershipTest";
                    options.DefaultChallengeScheme = "OwnershipTest";
                }).AddScheme<AuthenticationSchemeOptions, TestIdentity>("OwnershipTest", _ => { });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var website = await client.GetStringAsync("/");
        check(website.Contains("Magic: The Gathering") && !website.Contains("id=\"game\"")
            && !website.Contains("pokemon", StringComparison.OrdinalIgnoreCase),
            "mobile UI advertises MTG without offering a game selector");
        check(website.Contains("Welcome to the beta")
            && website.Contains("Accept your invitation first, then use the same email address")
            && website.Contains("Sign-in may use your existing account or a code sent to your email")
            && !website.Contains("invited Microsoft account") && !website.Contains("Outlook.com users"),
            "mobile welcome explains invited-email sign-in without requiring a Microsoft account");
        check(website.Contains("id=\"instructions\"") && website.Contains("id=\"accessRequestForm\"")
            && !website.Contains("tenant", StringComparison.OrdinalIgnoreCase)
            && !website.Contains("outlook", StringComparison.OrdinalIgnoreCase),
            "beta guidance is collapsible, includes an access form and avoids provider/account-structure jargon");
        check(website.Contains("MTG Studio database") && website.Contains("Enfolderer desktop client")
            && website.Contains("Export Mobile Collection Snapshot") && website.Contains("upload the exported JSON file")
            && website.Contains("No MTG Studio database? You can still save newly scanned cards"),
            "collection guidance explains MTG Studio database export/upload prerequisites without blocking scanned-only saving");
        using var scriptResponse = await client.GetAsync("/app.js?v=6");
        scriptResponse.EnsureSuccessStatusCode();
        var mobileScript = await scriptResponse.Content.ReadAsStringAsync();
        check(website.Contains("src=\"/app.js?v=6\"")
            && scriptResponse.Headers.CacheControl?.NoCache == true,
            "mobile script bypasses the old cached URL and requires revalidation on subsequent visits");
        check(mobileScript.Contains("gameHint: \"mtg\"") && !mobileScript.Contains("$(\"game\")"),
            "mobile requests always specify MTG without referencing a removed selector");
        check(mobileScript.Contains("$(\"signIn\").textContent = \"Sign in with your invited account\"")
            && !mobileScript.Contains("Sign in with Microsoft"),
            "initialized sign-in button uses the same invited-account wording");
        check(website.Contains("id=\"accessRequestActions\"")
            && mobileScript.Contains("$(\"accessRequestActions\").hidden = Boolean(account);")
            && mobileScript.Contains("$(\"requestAccess\").hidden = Boolean(account);"),
            "resolved sign-in state hides both access request controls for signed-in users and shows them for visitors");
        using var anonymous = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest());
        check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous callers cannot create jobs");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
        client.DefaultRequestHeaders.Add("X-Test-Given-Name", "Douglas");
        client.DefaultRequestHeaders.Add("X-Test-Name", "Different Person");
        foreach (var hint in new[] { "pokemon", "pokémon", "ptcg", "yugioh", "lorcana", "invalid" })
        {
            using var unsupported = await client.PostAsJsonAsync("/jobs/",
                new CreateJobRequest { FileName = "scan.jpg", GameHint = hint });
            check(unsupported.StatusCode == HttpStatusCode.BadRequest
                && (await unsupported.Content.ReadAsStringAsync()).Contains("Only Magic: The Gathering"),
                $"{hint}: unsupported scans are explicitly rejected before creating work");
        }
        using var created = await client.PostAsJsonAsync("/jobs/", new
        {
            fileName = "scan.jpg",
            firstName = "Forged",
            blobPrefix = "Forged-000000-0000",
            ownerTenantId = UserB,
            ownerObjectId = UserB
        });
        created.EnsureSuccessStatusCode();
        var createdJob = await created.Content.ReadFromJsonAsync<CreateJobResponse>()
            ?? throw new InvalidOperationException("API did not return a created job.");
        var job = await store.GetAsync(createdJob.JobId)
            ?? throw new InvalidOperationException("API did not store a created job.");
        check(job.OwnerTenantId == Tenant && job.OwnerObjectId == UserA,
            "job creation persists the authenticated caller, not request-body ownership");
        check(job.GameHint == CardGames.Magic, "jobs without a hint default explicitly to MTG");
        check(Guid.TryParseExact(job.JobId, "N", out _)
            && job.BlobPrefix == ScanBlobPaths.BuildJobPrefix(job.JobId, job.CreatedAt, "Douglas")
            && job.BlobPath == $"scans/{job.BlobPrefix}/scan.jpg",
            "new jobs persist the trusted creator's compact prefix, ignoring request-body names and preserving the public job ID");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ScanJobDocument>(
            System.Text.Json.JsonSerializer.Serialize(job))!;
        check(roundTrip.BlobPrefix == job.BlobPrefix
            && ScanBlobPaths.BuildAnnotatedBlobPath(roundTrip) == $"outputs/{job.BlobPrefix}/priced.png",
            "the exact blob prefix survives job persistence rather than being recomputed on retries");
        foreach (var hint in new[] { " MTG ", "magic", "magic-the-gathering", "" })
        {
            client.DefaultRequestHeaders.Remove("X-Test-Given-Name");
            client.DefaultRequestHeaders.Remove("X-Test-Name");
            if (hint == " MTG ") client.DefaultRequestHeaders.Add("X-Test-Given-Name", "Douglas");
            if (hint == "magic") client.DefaultRequestHeaders.Add("X-Test-Name", "Felix Example");
            if (hint == "") client.DefaultRequestHeaders.Add("X-Test-Name", "person@example.com");
            using var accepted = await client.PostAsJsonAsync("/jobs/",
                new CreateJobRequest { FileName = "scan.jpg", GameHint = hint });
            accepted.EnsureSuccessStatusCode();
            var result = await accepted.Content.ReadFromJsonAsync<CreateJobResponse>();
            check((await store.GetAsync(result!.JobId))?.GameHint == CardGames.Magic,
                $"'{hint}': compatible MTG hints are normalized");
            var label = hint == " MTG " ? "Douglas" : hint == "magic" ? "Felix" : $"user-{UserA}";
            var acceptedJob = (await store.GetAsync(result!.JobId))!;
            check(acceptedJob.BlobPrefix == ScanBlobPaths.BuildJobPrefix(result.JobId, acceptedJob.CreatedAt, label),
                "real job routes use the given/display name or the explicit owner-ID fallback");
        }

        foreach (var caller in new[] { UserB, "other-tenant", "missing-claims", "" })
        {
            client.DefaultRequestHeaders.Remove("X-Test-User");
            if (caller.Length > 0) client.DefaultRequestHeaders.Add("X-Test-User", caller);
            foreach (var (method, suffix) in new[]
            {
                (HttpMethod.Get, ""), (HttpMethod.Put, "/content"),
                (HttpMethod.Post, "/submit"), (HttpMethod.Get, "/annotated-image"), (HttpMethod.Get, "/cards.csv")
            })
            {
                using var request = new HttpRequestMessage(method, $"/jobs/{job.JobId}{suffix}");
                if (method == HttpMethod.Put) request.Content = new ByteArrayContent([1, 2, 3]);
                using var response = await client.SendAsync(request);
                check(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
                    $"actual {method} {suffix}: non-owner, wrong tenant or missing identity denied");
            }
        }
        check(images.Writes == 0 && images.Reads == 0 && queue.Messages == 0,
            "denied requests never access image data or enqueue work");

        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
        using var upload = await client.PutAsync($"/jobs/{job.JobId}/content", new ByteArrayContent([1, 2, 3]));
        using var submit = await client.PostAsync($"/jobs/{job.JobId}/submit", null);
        using var status = await client.GetAsync($"/jobs/{job.JobId}");
        check(upload.StatusCode == HttpStatusCode.Accepted && submit.StatusCode == HttpStatusCode.Accepted
            && status.StatusCode == HttpStatusCode.OK && images.Writes == 1 && queue.Messages == 1
            && images.LastWritePath == job.BlobPath,
            "owner retains normal upload, submission and polling behavior");
        await store.UpsertAsync(job with
        {
            Status = ScanJobStatus.Completed,
            CardsDetected = 1,
            Result = new ScanResultDocument { AnnotatedImagePath = $"jobs/{job.JobId}/annotated-image" }
        });
        using var download = await client.GetAsync($"/jobs/{job.JobId}/annotated-image");
        check(download.StatusCode == HttpStatusCode.OK && images.Reads == 1
            && images.LastReadPath == $"outputs/{job.BlobPrefix}/priced.png",
            "owner can download the completed annotated image from its dated folder");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserB);
        using var stolenImage = await client.GetAsync($"/jobs/{job.JobId}/annotated-image");
        using var stolenResults = await client.GetAsync($"/jobs/{job.JobId}");
        check(stolenImage.StatusCode == HttpStatusCode.NotFound && stolenResults.StatusCode == HttpStatusCode.NotFound
            && images.Reads == 1, "non-owner cannot download an existing completed image or results");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
        var legacyId = Guid.NewGuid().ToString("N");
        var legacyOwned = job with
        {
            Id = legacyId, JobId = legacyId, BlobPrefix = null, BlobPath = $"scans/{legacyId}/scan.jpg",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), Status = ScanJobStatus.Completed, CardsDetected = 1,
            Result = new ScanResultDocument { AnnotatedImagePath = $"jobs/{legacyId}/annotated-image" }
        };
        await store.CreateAsync(legacyOwned);
        using var legacyImage = await client.GetAsync($"/jobs/{legacyId}/annotated-image");
        check(legacyImage.IsSuccessStatusCode && images.LastReadPath == $"outputs/{legacyId}/priced.png",
            "owned historical scans still download from undated folders");
        await store.CreateAsync(new ScanJobDocument { Id = "legacy", JobId = "legacy" });
        using var legacy = await client.GetAsync("/jobs/legacy");
        check(legacy.StatusCode == HttpStatusCode.NotFound, "actual API denies legacy ownerless jobs");

        using var disabledDiagnostics = await client.GetAsync("/diagnostics/scans/");
        check(disabledDiagnostics.StatusCode == HttpStatusCode.Forbidden,
            "diagnostics is disabled when no operator allowlist is configured");
        await using (var diagnosticsFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MobileWeb:AllowedUserObjectIds", $"{UserA},{UserB}");
            builder.UseSetting("Diagnostics:AllowedUserObjectIds", UserA);
        }))
        {
            using var diagnostics = diagnosticsFactory.CreateClient();
            var foreign = job with
            {
                Id = Guid.NewGuid().ToString("N"), JobId = Guid.NewGuid().ToString("N"),
                OwnerObjectId = UserB, Status = ScanJobStatus.Failed, Error = "Test failure",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1), CardsDetected = 1
            };
            foreign = foreign with { Id = foreign.JobId };
            await store.CreateAsync(foreign);
            var excluded = foreign with { JobId = "removed", Id = "removed", OwnerObjectId = UserB.Replace('b', 'c') };
            await store.CreateAsync(excluded);
            foreach (var caller in new[] { "", UserB, "other-tenant", "missing-scope", "missing-claims" })
            {
                diagnostics.DefaultRequestHeaders.Remove("X-Test-User");
                if (caller.Length > 0) diagnostics.DefaultRequestHeaders.Add("X-Test-User", caller);
                foreach (var path in new[] { "/diagnostics/scans/", $"/diagnostics/scans/{foreign.JobId}",
                    $"/diagnostics/scans/{foreign.JobId}/original", $"/diagnostics/scans/{foreign.JobId}/annotated-image",
                    $"/diagnostics/scans/{foreign.JobId}/crops/0", $"/diagnostics/scans/{foreign.JobId}/cards.csv" })
                {
                    using var denied = await diagnostics.GetAsync(path);
                    check(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                        $"diagnostics rejects non-operator/tenant/scope/identity on {path}");
                }
            }
            diagnostics.DefaultRequestHeaders.Remove("X-Test-User");
            diagnostics.DefaultRequestHeaders.Add("X-Test-User", UserA);
            using var recent = await diagnostics.GetAsync("/diagnostics/scans/?hours=24&limit=1");
            var recentJobs = await recent.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            check(recent.IsSuccessStatusCode && recentJobs.GetArrayLength() == 1
                && recentJobs[0].GetProperty("jobId").GetString() == foreign.JobId,
                "operator finds the latest uploaded pilot attempt, including failure, not a newer pending or removed-user scan");
            foreach (var path in new[] { $"/diagnostics/scans/{foreign.JobId}", $"/diagnostics/scans/{foreign.JobId}/original",
                $"/diagnostics/scans/{foreign.JobId}/crops/0", $"/diagnostics/scans/{job.JobId}/annotated-image",
                $"/diagnostics/scans/{job.JobId}/cards.csv" })
            {
                using var evidence = await diagnostics.GetAsync(path);
                check(evidence.IsSuccessStatusCode && evidence.Headers.CacheControl?.NoStore == true,
                    $"operator can read pilot evidence with no-store: {path}");
            }
            foreach (var (document, folder) in new[] { (job, job.BlobPrefix!), (legacyOwned, legacyId) })
            {
                using var crop = await diagnostics.GetAsync($"/diagnostics/scans/{document.JobId}/crops/0");
                check(crop.IsSuccessStatusCode && images.LastReadPath == $"crops/{folder}/card-000.png",
                    "diagnostics reads crops from the persisted dated folder or legacy undated folder");
                using var annotated = await diagnostics.GetAsync($"/diagnostics/scans/{document.JobId}/annotated-image");
                check(annotated.IsSuccessStatusCode && images.LastReadPath == $"outputs/{folder}/priced.png",
                    "diagnostics reads annotations from the persisted dated folder or legacy undated folder");
            }
            foreach (var path in new[] { "/diagnostics/scans/?hours=169", "/diagnostics/scans/?limit=0" })
            {
                using var invalid = await diagnostics.GetAsync(path);
                check(invalid.StatusCode == HttpStatusCode.BadRequest, "diagnostic query bounds are enforced");
            }
            foreach (var path in new[] { "/diagnostics/scans/legacy", "/diagnostics/scans/removed",
                $"/diagnostics/scans/{foreign.JobId}/crops/-1", $"/diagnostics/scans/{foreign.JobId}/crops/1" })
            {
                using var absent = await diagnostics.GetAsync(path);
                check(absent.StatusCode == HttpStatusCode.NotFound, "operator cannot read ownerless/non-pilot jobs or out-of-range crops");
            }
            using var normalRoute = await diagnostics.GetAsync($"/jobs/{foreign.JobId}");
            using var writeDiagnostic = await diagnostics.PostAsync($"/diagnostics/scans/{foreign.JobId}", null);
            check(normalRoute.StatusCode == HttpStatusCode.NotFound && writeDiagnostic.StatusCode == HttpStatusCode.MethodNotAllowed,
                "diagnostic privilege neither bypasses normal ownership checks nor grants write operations");
        }

        var snapshot = new CollectionSnapshot { Printings = [new("aaa", "001", 2, 12), new("aaa", "1a", 0, 12)] };
        using var savedSnapshot = await client.PutAsJsonAsync("/collection/", snapshot);
        check(savedSnapshot.StatusCode == HttpStatusCode.OK, "approved user can upload an ownership snapshot");
        using var metadata = await client.GetAsync("/collection/");
        var metadataText = await metadata.Content.ReadAsStringAsync();
        check(metadataText.Contains("\"available\":true") && !metadataText.Contains("quantity"),
            "collection status returns metadata, not inventory contents");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserB);
        using var otherSnapshot = await client.GetAsync("/collection/");
        using var deleteOther = await client.DeleteAsync("/collection/");
        check((await otherSnapshot.Content.ReadAsStringAsync()).Contains("\"available\":false")
            && deleteOther.IsSuccessStatusCode, "another user cannot read or delete the owner's collection snapshot");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
        using var retainedSnapshot = await client.GetAsync("/collection/");
        check((await retainedSnapshot.Content.ReadAsStringAsync()).Contains("\"available\":true"),
            "deleting a different user's snapshot leaves the owner's data intact");
        using var invalidSnapshot = await client.PutAsJsonAsync("/collection/", snapshot with
        {
            Printings = [new("aaa", "1", 2, 1)]
        });
        using var duplicatedSnapshot = await client.PutAsJsonAsync("/collection/", snapshot with
        {
            Printings = [new("aaa", "001", 2, 12), new("AAA", "1", 2, 12)]
        });
        check(invalidSnapshot.StatusCode == HttpStatusCode.BadRequest
            && duplicatedSnapshot.StatusCode == HttpStatusCode.BadRequest, "negative/inconsistent quantities and normalized duplicate printing keys are rejected");
        foreach (var legacyEntries in new IReadOnlyList<CollectionGathererPrinting>[]
        {
            [new(3718, 4, 3)], [new(0, 4, 4)], [new(3718, 4, 4), new(3718, 4, 4)]
        })
        {
            using var invalidLegacy = await client.PutAsJsonAsync("/collection/", snapshot with { GathererPrintings = legacyEntries });
            check(invalidLegacy.StatusCode == HttpStatusCode.BadRequest,
                "legacy snapshot quantities, public IDs and duplicate IDs are validated before replacing the collection");
        }
        using var withCollection = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest { UseCollection = true });
        withCollection.EnsureSuccessStatusCode();
        var collectionJob = await withCollection.Content.ReadFromJsonAsync<CreateJobResponse>();
        check((await store.GetAsync(collectionJob!.JobId))!.UseCollection,
            "web collection opt-in persists on the job without changing desktop defaults");
        using var removedSnapshot = await client.DeleteAsync("/collection/");
        using var absentCollection = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest { UseCollection = true });
        check(removedSnapshot.StatusCode == HttpStatusCode.NoContent && absentCollection.StatusCode == HttpStatusCode.BadRequest,
            "snapshot deletion is supported and collection scans require an uploaded snapshot");
        await ScannedCollectionChecks.RunAsync(check, client, store,
            factory.Services.GetRequiredService<ICollectionSnapshotStore>(), Tenant, UserA, UserB);
        var csv = MobileWebEndpoints.Csv(new ScanResultDocument
        {
            Cards = [new IdentifiedCard { Set = "aaa", Name = "=Formula;\"Title\"", CollectorNumber = "1", Language = "en" }]
        });
        check(csv.StartsWith("Rank;Card position;Name;Set;Number;Language;Nonfoil EUR;Foil EUR;Ranking EUR;")
            && csv.Contains("\r\n1;1;\"'=Formula;\"\"Title\"\"\";AAA;1;en;"),
            "ranked web CSV has explicit price headers, quotes delimiters and neutralizes spreadsheet formulas");
        var rankedCsv = MobileWebEndpoints.Csv(new ScanResultDocument
        {
            Cards =
            [
                new() { Index = 2, Set = "vis", Name = "No price", CollectorNumber = "2" },
                new() { Index = 1, Set = "vis", Name = "Zero nonfoil", CollectorNumber = "3",
                    Prices = new CardPrices { Nonfoil = 0, Foil = 100 } },
                new() { Index = 0, Set = "vis", Name = "Foil only", CollectorNumber = "1",
                    Prices = new CardPrices { Foil = 10.5m }, Ownership = new(1, 4, 2) }
            ]
        }).Split("\r\n");
        check(rankedCsv[1].StartsWith("1;1;Foil only;VIS;1;;;10.50;10.50;1;4;")
            && rankedCsv[2].StartsWith("2;2;Zero nonfoil;VIS;3;;0.00;100.00;0.00;")
            && rankedCsv[3].StartsWith("3;3;No price;VIS;2;"),
            "ranked report sorts foil fallback above real zero and missing prices, with invariant prices, original positions and exact ownership");
        await using (var pilotFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("MobileWeb:AllowedUserObjectIds", UserA)))
        {
            using var pilotClient = pilotFactory.CreateClient();
            pilotClient.DefaultRequestHeaders.Add("X-Test-User", UserB);
            using var deniedPilot = await pilotClient.GetAsync("/collection/");
            check(deniedPilot.StatusCode == HttpStatusCode.Forbidden, "private pilot rejects an authenticated but unapproved tenant user");
            using var deniedScanned = await pilotClient.GetAsync("/scanned-collection/");
            check(deniedScanned.StatusCode == HttpStatusCode.Forbidden, "scanned collection also enforces the pilot allowlist");
        }
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "missing-scope");
        using var noScope = await client.GetAsync("/collection/");
        check(noScope.StatusCode == HttpStatusCode.Forbidden, "user identity alone is insufficient without delegated Scan.Submit permission");

        await using var mcpFactory = new WebApplicationFactory<MtgCatalogueTools>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Directory.GetCurrentDirectory());
            builder.UseSetting("McpServer:TenantId", Tenant);
            builder.UseSetting("McpServer:Audience", "api://test");
            builder.UseSetting("McpServer:AllowedCallerObjectIds", UserA);
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "OwnershipTest";
                    options.DefaultChallengeScheme = "OwnershipTest";
                }).AddScheme<AuthenticationSchemeOptions, TestIdentity>("OwnershipTest", _ => { }));
        });
        using var mcpClient = mcpFactory.CreateClient();
        mcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (caller, expected) in new[]
        {
            ("", HttpStatusCode.Unauthorized), (UserB, HttpStatusCode.Forbidden), (UserA, HttpStatusCode.OK)
        })
        {
            mcpClient.DefaultRequestHeaders.Remove("X-Test-User");
            if (caller.Length > 0) mcpClient.DefaultRequestHeaders.Add("X-Test-User", caller);
            using var initialize = await mcpClient.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "security-check", version = "1" } }
            });
            check(initialize.StatusCode == expected,
                $"actual MCP initialize: expected {(int)expected}, received {(int)initialize.StatusCode}");
        }
        foreach (var settings in new[]
        {
            ("", ""), (Tenant, "")
        })
        {
            await using var unconfigured = new WebApplicationFactory<JobOwnerFilter>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(Directory.GetCurrentDirectory());
                builder.UseSetting("AzureAd:TenantId", settings.Item1);
                builder.UseSetting("AzureAd:ClientId", settings.Item2);
            });
            try
            {
                using var unexpected = unconfigured.CreateClient();
                check(false, "API must refuse startup without complete authentication configuration");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("refuses to start"))
            {
                check(true, "actual API host refuses startup with missing authentication configuration");
            }
        }
        await CheckAllowancesAsync(check);
        await CheckAccessRequestsAsync(check);
    }

    private static async Task CheckAccessRequestsAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nBeta access requests: anonymous submission, durable inbox isolation and rate limits");
        var requests = new InMemoryAccessRequestStore();
        await using var factory = new WebApplicationFactory<JobOwnerFilter>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Directory.GetCurrentDirectory());
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", Tenant);
            builder.UseSetting("AzureAd:ClientId", UserA);
            builder.UseSetting("MobileWeb:AllowedUserObjectIds", $"{UserA},{UserB}");
            builder.UseSetting("ScanAllowances:AdminUserObjectIds", UserA);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAccessRequestStore>();
                services.AddSingleton<IAccessRequestStore>(requests);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "AccessRequestTest";
                    options.DefaultChallengeScheme = "AccessRequestTest";
                }).AddScheme<AuthenticationSchemeOptions, TestIdentity>("AccessRequestTest", _ => { });
            });
        });
        using var client = factory.CreateClient();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-User", UserA);
        using var anonymousRead = await client.GetAsync("/admin/access-requests/");
        check(anonymousRead.StatusCode == HttpStatusCode.Unauthorized, "anonymous callers cannot read access request emails");
        using var invalid = await client.PostAsJsonAsync("/access-requests", new { name = "", email = "bad", note = "" });
        check(invalid.StatusCode == HttpStatusCode.BadRequest && (await requests.ReadAsync()).Count == 0,
            "invalid request fields are rejected without persistence");
        using var oversized = await client.PostAsync("/access-requests",
            new StringContent(new string('x', 4097), System.Text.Encoding.UTF8, "application/json"));
        check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "public requests enforce the actual body size limit");
        var request = new { name = "<b>Beta tester</b>", email = "beta@example.com", note = "Please invite me." };
        using var sent = await client.PostAsJsonAsync("/access-requests", request);
        check(sent.StatusCode == HttpStatusCode.Accepted && sent.Headers.CacheControl?.NoStore == true,
            "anonymous users can save an access request without sign-in or granting themselves access");
        using var duplicate = await client.PostAsJsonAsync("/access-requests", new
        {
            name = "Different name", email = " BETA@EXAMPLE.COM ", note = "Changed note"
        });
        var saved = (await requests.ReadAsync()).Single();
        check(duplicate.StatusCode == HttpStatusCode.Accepted
            && await sent.Content.ReadAsStringAsync() == await duplicate.Content.ReadAsStringAsync()
            && saved.Name == request.name && saved.Email == request.email && saved.Note == request.note,
            "case/whitespace email duplicates neither disclose nor overwrite pending requests");
        using var malformed = await client.PostAsync("/access-requests",
            new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        check(malformed.StatusCode == HttpStatusCode.BadRequest, "malformed request JSON is rejected explicitly");
        using var limited = await client.PostAsJsonAsync("/access-requests", request);
        check(limited.StatusCode == HttpStatusCode.TooManyRequests
            && limited.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
            "the sixth request is rate limited with retry guidance");
        foreach (var caller in new[] { UserB, "other-tenant", "missing-scope", "missing-claims" })
        {
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", caller);
            using var denied = await client.GetAsync("/admin/access-requests/");
            using var deniedDelete = await client.DeleteAsync("/admin/access-requests/" + saved.Id);
            var status = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/access-requests/admin");
            check(denied.StatusCode == HttpStatusCode.Forbidden && deniedDelete.StatusCode == HttpStatusCode.Forbidden
                && !status.GetProperty("allowed").GetBoolean(),
                "ordinary users, wrong accounts and missing scopes cannot discover or remove request details");
        }
        using var inbox = await admin.GetAsync("/admin/access-requests/");
        var rows = await inbox.Content.ReadFromJsonAsync<AccessRequest[]>();
        check(inbox.IsSuccessStatusCode && inbox.Headers.CacheControl?.NoStore == true && rows?.Single() == saved,
            "only the configured admin can read the private no-store inbox");
        using var invalidId = await admin.DeleteAsync("/admin/access-requests/bad");
        check(invalidId.StatusCode == HttpStatusCode.BadRequest, "admin deletion rejects non-hash request IDs");
        using var removed = await admin.DeleteAsync("/admin/access-requests/" + saved.Id);
        check(removed.StatusCode == HttpStatusCode.NoContent && (await requests.ReadAsync()).Count == 0,
            "admin can remove a reviewed request without changing account access");
        await using var validationFactory = factory.WithWebHostBuilder(_ => { });
        using var validationClient = validationFactory.CreateClient();
        foreach (var fields in new[]
        {
            new { name = new string('x', 101), email = "valid@example.com", note = "" },
            new { name = "Valid", email = "Display Name <valid@example.com>", note = "" },
            new { name = "Valid", email = "valid@example.com\r\nInjected: true", note = "" },
            new { name = "Valid", email = "valid@example.com", note = new string('x', 501) },
            new { name = "Valid", email = "valid@example.com", note = "hidden\0control" }
        })
        {
            using var rejected = await validationClient.PostAsJsonAsync("/access-requests", fields);
            check(rejected.StatusCode == HttpStatusCode.BadRequest && (await requests.ReadAsync()).Count == 0,
                "overlong fields, email display/header injection and invalid controls cannot enter the access inbox");
        }
    }

    private static async Task CheckAllowancesAsync(Action<bool, string> check)
    {
        Console.WriteLine("\nScan allowances: real routes, concurrency, exhaustion and dispatch recovery");
        var store = new InMemoryJobStore();
        var allowances = new InMemoryScanAllowanceStore();
        var queue = new Queue();
        var images = new Images();
        await using var factory = new WebApplicationFactory<JobOwnerFilter>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Directory.GetCurrentDirectory());
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", Tenant);
            builder.UseSetting("AzureAd:ClientId", UserA);
            builder.UseSetting("MobileWeb:AllowedUserObjectIds", $"{UserA},{UserB}");
            builder.UseSetting("ScanAllowances:AdminUserObjectIds", UserA);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJobStore>();
                services.RemoveAll<IScanImageStore>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IScanAllowanceStore>();
                services.AddSingleton<IJobStore>(store);
                services.AddSingleton<IScanImageStore>(images);
                services.AddSingleton<IJobQueue>(queue);
                services.AddSingleton<IScanAllowanceStore>(allowances);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "QuotaTest";
                    options.DefaultChallengeScheme = "QuotaTest";
                }).AddScheme<AuthenticationSchemeOptions, TestIdentity>("QuotaTest", _ => { });
            });
        });
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-User", UserA);
        using var guest = factory.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Test-User", UserB);
        async Task<string> JobAsync()
        {
            using var response = await guest.PostAsJsonAsync("/jobs/", new CreateJobRequest { FileName = "test.jpg" });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;
        }
        var ungrantedJob = await JobAsync();
        using var denied = await guest.PostAsync($"/jobs/{ungrantedJob}/submit", null);
        check(denied.StatusCode == HttpStatusCode.Forbidden && queue.Messages == 0,
            "approved users have no implicit scan grant, even when using direct API requests");
        using var forbiddenAdmin = await guest.PostAsJsonAsync($"/admin/allowances/{UserB}", new
        {
            operationId = Guid.NewGuid(), delta = 100, unlimited = true, reason = "Self grant"
        });
        check(forbiddenAdmin.StatusCode == HttpStatusCode.Forbidden,
            "guest cannot grant credits or unlimited access to themselves");
        var grant = new { operationId = Guid.NewGuid(), delta = 10, unlimited = false, reason = "Initial guest grant" };
        using var granted = await admin.PostAsJsonAsync($"/admin/allowances/{UserB}", grant);
        granted.EnsureSuccessStatusCode();
        using var repeatedGrant = await admin.PostAsJsonAsync($"/admin/allowances/{UserB}", grant);
        repeatedGrant.EnsureSuccessStatusCode();
        var key = ScanAllowanceOwner.Key(Tenant, UserB);
        check((await allowances.GetAsync(key)).CreditsRemaining == 10,
            "an admin adjustment retried with the same operation ID grants only once");
        var missingUpload = await JobAsync();
        images.Exists = false;
        using var missingUploadResponse = await guest.PostAsync($"/jobs/{missingUpload}/submit", null);
        check(missingUploadResponse.StatusCode == HttpStatusCode.BadRequest
            && (await allowances.GetAsync(key)).CreditsRemaining == 10,
            "submission without an uploaded photo consumes no credits");
        images.Exists = true;
        using var changedGrant = await admin.PostAsJsonAsync($"/admin/allowances/{UserB}", new
        {
            grant.operationId, delta = 20, unlimited = false, reason = grant.reason
        });
        check(changedGrant.StatusCode == HttpStatusCode.Conflict,
            "reusing an adjustment ID with changed parameters is rejected");
        var jobs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => JobAsync()));
        var responses = await Task.WhenAll(jobs.Select(id => guest.PostAsync($"/jobs/{id}/submit", null)));
        try
        {
            check(responses.Count(r => r.StatusCode == HttpStatusCode.Accepted) == 10
                && responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden) == 2
                && queue.Messages == 10 && (await allowances.GetAsync(key)).CreditsRemaining == 0,
                "twelve simultaneous submissions spend exactly ten credits and dispatch exactly ten jobs");
            var acceptedId = jobs[Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.Accepted)];
            using var retried = await guest.PostAsync($"/jobs/{acceptedId}/submit", null);
            using var oldResult = await guest.GetAsync($"/jobs/{acceptedId}");
            check(retried.StatusCode == HttpStatusCode.Accepted && queue.Messages == 10
                && oldResult.StatusCode == HttpStatusCode.OK,
                "exhausted users can retry the same job and read existing results without another charge");
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var ownBalance = await guest.GetAsync("/allowance/");
        var balance = await ownBalance.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        check(balance.GetProperty("remaining").GetInt64() == 0 && !balance.GetProperty("unlimited").GetBoolean(),
            "remaining balance is available to the signed-in user");
        using var topup = await admin.PostAsJsonAsync($"/admin/allowances/{UserB}", new
        {
            operationId = Guid.NewGuid(), delta = 1, unlimited = false, reason = "Service failure refund"
        });
        topup.EnsureSuccessStatusCode();
        var failedDispatchId = await JobAsync();
        queue.Fail = true;
        using var failedDispatch = await guest.PostAsync($"/jobs/{failedDispatchId}/submit", null);
        check(failedDispatch.StatusCode == HttpStatusCode.InternalServerError
            && (await allowances.GetAsync(key)).CreditsRemaining == 0,
            "queue outage retains an atomic paid reservation rather than silently restoring or losing the credit");
        queue.Fail = false;
        await factory.Services.GetRequiredService<ScanDispatchService>().RecoverAsync(default);
        await factory.Services.GetRequiredService<ScanDispatchService>().RecoverAsync(default);
        check(queue.Messages == 11
            && (await store.GetAsync(failedDispatchId))?.Status == ScanJobStatus.Uploaded,
            "dispatch recovery sends reserved work after an outage without another charge or repeated recovery send");
        var unlimitedId = Guid.NewGuid();
        await allowances.AdjustAsync(key, unlimitedId, 0, true, UserA, "Unlimited test");
        await guest.PostAsync($"/jobs/{await JobAsync()}/submit", null);
        check((await allowances.GetAsync(key)) is { Unlimited: true, CreditsRemaining: 0 } && queue.Messages == 12,
            "explicit unlimited grants allow scanning without a finite credit deduction");
        await allowances.AdjustAsync(key, Guid.NewGuid(), 0, false, UserA, "Return to finite");
        try
        {
            await allowances.AdjustAsync(key, Guid.NewGuid(), -1, false, UserA, "Invalid negative balance");
            check(false, "negative balances must be rejected");
        }
        catch (InvalidOperationException) { check(true, "administration cannot create a negative credit balance"); }
        var duplicateKey = ScanAllowanceOwner.Key(Tenant, Guid.NewGuid().ToString("D"));
        await allowances.AdjustAsync(duplicateKey, Guid.NewGuid(), 1, false, UserA, "One scan");
        var duplicateMessage = new ScanJobMessage(Guid.NewGuid().ToString("N"), "scans/test.jpg", null);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => allowances.ReserveAsync(duplicateKey, duplicateMessage)));
        check(reservations.All(r => r.Allowed)
            && reservations.Count(r => r.Outcome == ScanReservationOutcome.Reserved) == 1
            && (await allowances.GetAsync(duplicateKey)) is { CreditsRemaining: 0, TotalReserved: 1 },
            "thirty concurrent retries of one job reserve and charge exactly once");
    }

    private sealed class TestIdentity(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            Claim[] claims = user == "missing-claims" ? [] :
                [new("tid", user == "other-tenant" ? UserB : Tenant),
                 new("oid", user is "other-tenant" or "missing-scope" ? UserA : user),
                 new("scp", user == "missing-scope" ? "Other.Permission" : "Scan.Submit")];
            if (user != "missing-claims")
            {
                if (Request.Headers.TryGetValue("X-Test-Given-Name", out var given))
                    claims = [.. claims, new("given_name", given.ToString())];
                if (Request.Headers.TryGetValue("X-Test-Name", out var display))
                    claims = [.. claims, new("name", display.ToString())];
            }
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Images : IScanImageStore
    {
        public bool Exists { get; set; } = true;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public string? LastReadPath { get; private set; }
        public string? LastWritePath { get; private set; }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            Reads++;
            LastReadPath = path;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        }
        public Task WriteAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        {
            Writes++;
            LastWritePath = path;
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(Exists);
    }

    private sealed class Queue : IJobQueue
    {
        private int _messages;
        public int Messages => _messages;
        public bool Fail { get; set; }
        public Task EnqueueAsync(ScanJobMessage message, CancellationToken ct = default)
        {
            if (Fail) throw new IOException("Simulated queue outage");
            Interlocked.Increment(ref _messages);
            return Task.CompletedTask;
        }
    }
}

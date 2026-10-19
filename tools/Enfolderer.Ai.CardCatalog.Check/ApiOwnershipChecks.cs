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
        var mobileScript = await client.GetStringAsync("/app.js");
        check(mobileScript.Contains("gameHint: \"mtg\"") && !mobileScript.Contains("$(\"game\")"),
            "mobile requests always specify MTG without referencing a removed selector");
        using var anonymous = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest());
        check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous callers cannot create jobs");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
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
        foreach (var hint in new[] { " MTG ", "magic", "magic-the-gathering", "" })
        {
            using var accepted = await client.PostAsJsonAsync("/jobs/",
                new CreateJobRequest { FileName = "scan.jpg", GameHint = hint });
            accepted.EnsureSuccessStatusCode();
            var result = await accepted.Content.ReadFromJsonAsync<CreateJobResponse>();
            check((await store.GetAsync(result!.JobId))?.GameHint == CardGames.Magic,
                $"'{hint}': compatible MTG hints are normalized");
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
            && status.StatusCode == HttpStatusCode.OK && images.Writes == 1 && queue.Messages == 1,
            "owner retains normal upload, submission and polling behavior");
        await store.UpsertAsync(job with
        {
            Status = ScanJobStatus.Completed,
            Result = new ScanResultDocument { AnnotatedImagePath = ScanBlobPaths.BuildAnnotatedBlobPath(job.JobId) }
        });
        using var download = await client.GetAsync($"/jobs/{job.JobId}/annotated-image");
        check(download.StatusCode == HttpStatusCode.OK && images.Reads == 1,
            "owner can download the completed annotated image");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserB);
        using var stolenImage = await client.GetAsync($"/jobs/{job.JobId}/annotated-image");
        using var stolenResults = await client.GetAsync($"/jobs/{job.JobId}");
        check(stolenImage.StatusCode == HttpStatusCode.NotFound && stolenResults.StatusCode == HttpStatusCode.NotFound
            && images.Reads == 1, "non-owner cannot download an existing completed image or results");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
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
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Images : IScanImageStore
    {
        public bool Exists { get; set; } = true;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        }
        public Task WriteAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        {
            Writes++;
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

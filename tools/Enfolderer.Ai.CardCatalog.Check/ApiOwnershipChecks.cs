using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
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
        await using var factory = new WebApplicationFactory<JobOwnerFilter>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Directory.GetCurrentDirectory());
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
        using var anonymous = await client.PostAsJsonAsync("/jobs/", new CreateJobRequest());
        check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous callers cannot create jobs");
        client.DefaultRequestHeaders.Add("X-Test-User", UserA);
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

        foreach (var caller in new[] { UserB, "other-tenant", "missing-claims", "" })
        {
            client.DefaultRequestHeaders.Remove("X-Test-User");
            if (caller.Length > 0) client.DefaultRequestHeaders.Add("X-Test-User", caller);
            foreach (var (method, suffix) in new[]
            {
                (HttpMethod.Get, ""), (HttpMethod.Put, "/content"),
                (HttpMethod.Post, "/submit"), (HttpMethod.Get, "/annotated-image")
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
                 new("oid", user == "other-tenant" ? UserA : user)];
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Images : IScanImageStore
    {
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
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class Queue : IJobQueue
    {
        public int Messages { get; private set; }
        public Task EnqueueAsync(ScanJobMessage message, CancellationToken ct = default)
        {
            Messages++;
            return Task.CompletedTask;
        }
    }
}

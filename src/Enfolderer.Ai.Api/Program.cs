using Azure;
using Azure.Core;
using Azure.Identity;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Allowances;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;

// 64 MB: comfortably above a phone photograph, well below anything that would exhaust the
// container's 1Gi memory limit while being relayed to blob storage.
const long MaxScanImageBytes = 64L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddScanPlatform(builder.Configuration);
builder.Services.AddSingleton<ScanDispatchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanDispatchService>());

// Entra ID protection. The desktop client signs in interactively and presents a user token; it
// never holds a client secret or a storage key.
if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"])
    || string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]))
    throw new InvalidOperationException("AzureAd:TenantId and AzureAd:ClientId are required. The scan API refuses to start without authentication.");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
var webClientId = builder.Configuration["MobileWeb:ClientId"];
var pilotUsers = (builder.Configuration["MobileWeb:AllowedUserObjectIds"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (!string.IsNullOrWhiteSpace(webClientId)
    && (!Guid.TryParse(webClientId, out var browserId) || browserId == Guid.Empty || pilotUsers.Length == 0
        || pilotUsers.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty)))
    throw new InvalidOperationException("Mobile web access requires an explicit pilot user allowlist.");

var diagnosticUsers = (builder.Configuration["Diagnostics:AllowedUserObjectIds"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (diagnosticUsers.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty
        || !pilotUsers.Contains(value, StringComparer.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Diagnostics operators must be valid users in the explicit scan pilot allowlist.");

var allowanceAdmins = (builder.Configuration["ScanAllowances:AdminUserObjectIds"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (allowanceAdmins.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty
        || !pilotUsers.Contains(value, StringComparer.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Allowance administrators must be valid users in the explicit scan pilot allowlist.");

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; " +
        "img-src 'self' blob:; connect-src 'self' https://login.microsoftonline.com; " +
        "frame-src 'self' https://login.microsoftonline.com https://login.live.com; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/jobs") || context.Request.Path.StartsWithSegments("/collection")
        || context.Request.Path.StartsWithSegments("/diagnostics")
        || context.Request.Path.StartsWithSegments("/allowance") || context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path == "/app.js")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapMobileWeb();
app.MapScanDiagnostics();
app.MapScanAllowances();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// 1. Create a job and hand back a short-lived, write-only upload grant for exactly one blob.
var jobs = app.MapGroup("/jobs");
jobs.RequireAuthorization();
jobs.AddEndpointFilter<JobOwnerFilter>();

jobs.MapPost("/", async (
    CreateJobRequest request,
    HttpContext context,
    IJobStore store,
    ICollectionSnapshotStore collections,
    ILoggerFactory loggerFactory,
    TokenCredential credential,
    ScanPlatformOptions platform,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(request.GameHint) && !CardGames.IsSupported(CardGames.Normalize(request.GameHint)))
        return Results.BadRequest(new { error = "Only Magic: The Gathering (mtg) scans are supported." });

    var jobId = Guid.NewGuid().ToString("n");
    var owner = JobOwnerFilter.ReadOwner(context.User)
        ?? throw new InvalidOperationException("The job ownership filter must validate the caller before job creation.");
    if (request.UseCollection && await collections.ReadAsync(owner.TenantId, owner.ObjectId, ct) is null)
        return Results.BadRequest(new { error = "Upload a collection snapshot before enabling ownership." });

    // The storage account has no public endpoint, so there is no URL that would let the client
    // write the blob itself. The path is derived here only so that the job document and the worker
    // agree on where the image will land once PUT /jobs/{id}/content relays it.
    var createdAt = DateTimeOffset.UtcNow;
    var blobPrefix = ScanBlobPaths.BuildJobPrefix(jobId, createdAt);
    var blobPath = $"{ScanBlobPaths.ScansContainer}/{ScanBlobPaths.BuildScanBlobName(blobPrefix, request.FileName)}";
    var uploadExpiresAt = createdAt.Add(platform.UploadWindow);

    try
    {
        var job = new ScanJobDocument
        {
            Id = jobId,
            JobId = jobId,
            OwnerTenantId = owner.TenantId,
            OwnerObjectId = owner.ObjectId,
            UseCollection = request.UseCollection,
            Status = ScanJobStatus.Pending,
            BlobPrefix = blobPrefix,
            BlobPath = blobPath,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            UploadExpiresAt = uploadExpiresAt,
            GameHint = CardGames.Magic
        };

        await store.CreateAsync(job, ct);
    }
    catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    {
        // Cosmos reports its own failures through its own exception type, so a missing data-plane
        // role assignment would otherwise still surface as a bare 500. Only a refusal is read as a
        // permissions problem: throttling and outages use the same exception and must not send the
        // reader off to check role assignments.
        //
        // Name the principal, as the upload handler does. "Grant the role" is actively misleading
        // once the role is already granted, which is the common case: a host carrying several
        // identities presents a different one than the role was granted to, and the only way to
        // tell is to read the object id back off the token that was actually issued.
        var cosmosLog = loggerFactory.CreateLogger("Jobs");

        var cosmosPrincipal = await IdentityDiagnostics.DescribeAsync(
            credential, "https://cosmos.azure.com/.default", cosmosLog, ct);

        cosmosLog.LogError(
            ex, "Cosmos refused job {JobId} from {Principal} (configured client id {ClientId}): {StatusCode}/{SubStatusCode}",
            jobId, cosmosPrincipal, platform.ManagedIdentityClientId ?? "(unset)", ex.StatusCode, ex.SubStatusCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure Cosmos DB returned {(int)ex.StatusCode}/{ex.SubStatusCode} for the identity "
                  + $"{cosmosPrincipal}. Either that principal lacks the Cosmos DB Built-in Data "
                  + "Contributor role on the account, or the account was unreachable over the "
                  + "network — Cosmos reports both the same way, so check both. The role is a "
                  + "data-plane assignment: it never appears in `az role assignment list` or the "
                  + "portal's IAM blade, and is listed only by `az cosmosdb sql role assignment "
                  + "list`. For the network, an account with public access disabled needs a private "
                  + "endpoint, or nothing can reach it at all. See the API log for Cosmos's own "
                  + "message, which does distinguish the two.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (CosmosException ex)
    {
        // Anything else Cosmos reports — throttling, a conflict, an outage — is relayed as-is
        // rather than guessed at.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not record job {JobId}: {StatusCode}/{SubStatusCode}", jobId, ex.StatusCode, ex.SubStatusCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure Cosmos DB returned {(int)ex.StatusCode}/{ex.SubStatusCode}. See the API log for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (AuthenticationFailedException ex)
    {
        // Azure never saw the request: the identity could not get a token at all. That is a
        // different fault from a refused one, and distinguishing them here saves guessing whether
        // to look at role assignments or at the site's identity configuration. The usual cause is
        // a site with more than one identity assigned and no ScanPlatform__ManagedIdentityClientId
        // to say which to present, which leaves the choice ambiguous.
        // The message is logged but deliberately not returned: DefaultAzureCredential enumerates
        // every credential it tried, which describes the inside of the host to its callers.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not acquire a token to create job {JobId}", jobId);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: "The API could not acquire a managed identity token, so Azure was never called. "
                  + "Check that the site has the expected user-assigned identity and that "
                  + "ScanPlatform__ManagedIdentityClientId names it. See the API log for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new CreateJobResponse
    {
        JobId = jobId,
        BlobPath = blobPath,
        UploadExpiresAt = uploadExpiresAt
    });
});

// 2. Accept the image. The storage account is private, so the client cannot write to it directly;
// the API is the one public entry point and relays the bytes on the caller's behalf.
jobs.MapPut("/{jobId}/content", async (
    string jobId,
    HttpRequest request,
    IJobStore store,
    IScanImageStore images,
    ILoggerFactory loggerFactory,
    TokenCredential credential,
    ScanPlatformOptions platform,
    CancellationToken ct) =>
{
    // Photographs of a full binder page are larger than Kestrel's 30 MB default, and the failure
    // mode is an opaque 413 mid-upload, so the ceiling is raised deliberately rather than removed.
    var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeLimit is { IsReadOnly: false })
        sizeLimit.MaxRequestBodySize = MaxScanImageBytes;

    var job = await store.GetAsync(jobId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status != ScanJobStatus.Pending)
        return Results.Conflict(new { error = $"Job is already {job.Status}." });

    // The upload window advertised by POST /jobs has to mean something, or a pending job would
    // accept an image indefinitely. Jobs created before this field existed carry default(DateTimeOffset),
    // which is not a deadline anyone was given, so it is not enforced.
    if (job.UploadExpiresAt != default && DateTimeOffset.UtcNow > job.UploadExpiresAt)
        return Results.Problem(
            title: "The upload window for this job has closed.",
            detail: $"The upload window closed at {job.UploadExpiresAt:O}. Create a new job with POST /jobs.",
            statusCode: StatusCodes.Status410Gone);

    try
    {
        await images.WriteAsync(job.BlobPath, request.Body, request.ContentType ?? "application/octet-stream", ct);
    }
    catch (RequestFailedException ex) when (ex.Status is 401 or 403)
    {
        // This is the only place the API writes a blob, so a misconfigured or not-yet-propagated
        // role assignment surfaces here. Only a refusal is read as a permissions problem: a missing
        // container, a conflict or an outage arrive as the same exception type and must not send
        // the reader off to check role assignments. Report the storage error code rather than an unhandled
        // 500, which says only that something went wrong somewhere.
        //
        // Name the principal too. "The managed identity needs role X" is unhelpful when the real
        // fault is that a different identity was presented than the one the role was granted to,
        // which is the failure a host carrying several identities actually produces.
        var log = loggerFactory.CreateLogger("Jobs");

        var principal = await IdentityDiagnostics.DescribeAsync(
            credential, "https://storage.azure.com/.default", log, ct);

        log.LogError(
            ex, "Azure refused the image for job {JobId} from {Principal} (configured client id {ClientId}): {ErrorCode}",
            jobId, principal, platform.ManagedIdentityClientId ?? "(unset)", ex.ErrorCode);

        return Results.Problem(
            title: "Could not store the scan image.",
            detail: $"Azure Storage returned {ex.Status} {ex.ErrorCode} for the identity {principal}. "
                  + "That principal needs Storage Blob Data Contributor on the scans container. "
                  + "Compare the object id against `az role assignment list`: "
                  + "if it does not match, the site is presenting a different identity than the one "
                  + "the roles were granted to. A newly granted role can take several minutes to "
                  + "take effect.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (RequestFailedException ex)
    {
        // Anything else Storage reports is relayed as-is rather than guessed at.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not store the image for job {JobId}: {Status} {ErrorCode}", jobId, ex.Status, ex.ErrorCode);

        return Results.Problem(
            title: "Could not store the scan image.",
            detail: $"Azure Storage returned {ex.Status} {ex.ErrorCode}. See the API log for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (AuthenticationFailedException ex)
    {
        // Azure never saw the request: the identity could not get a token at all. The message is
        // logged but deliberately not returned, because DefaultAzureCredential enumerates every
        // credential it tried, which describes the inside of the host to its callers.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not acquire a token to store the image for job {JobId}", jobId);

        return Results.Problem(
            title: "Could not store the scan image.",
            detail: "The API could not acquire a managed identity token, so Azure was never called. "
                  + "Check that the app has the expected user-assigned identity and that "
                  + "ScanPlatform__ManagedIdentityClientId names it. See the API log for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Accepted();
});

// 3. Confirm the upload and queue the job for the worker.
jobs.MapPost("/{jobId}/submit", async (
    string jobId,
    IJobStore store,
    IScanAllowanceStore allowances,
    ScanDispatchService dispatcher,
    IScanImageStore images,
    HttpContext http,
    CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    if (job is null) return Results.NotFound();

    if (job.Status != ScanJobStatus.Pending)
        return Results.Accepted($"/jobs/{jobId}", JobMapping.ToStatusResponse(job));

    if (!await images.ExistsAsync(job.BlobPath, ct))
        return Results.BadRequest(new { error = "Image has not been uploaded yet." });

    var reserved = await allowances.ReserveAsync(ScanAllowanceEndpoints.OwnerKey(http),
        new ScanJobMessage(job.JobId, job.BlobPath, job.GameHint), ct);
    if (!reserved.Allowed) return ScanAllowanceEndpoints.Exhausted();
    await dispatcher.DispatchAsync(reserved.Receipt!, ct);
    var submitted = await store.GetAsync(jobId, ct)
        ?? throw new InvalidOperationException("Reserved job disappeared after dispatch.");

    return Results.Accepted($"/jobs/{jobId}", JobMapping.ToStatusResponse(submitted));
});

// 4. Poll for status and, once complete, the versioned result document.
jobs.MapGet("/{jobId}", async (string jobId, IJobStore store, CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    return job is null ? Results.NotFound() : Results.Ok(JobMapping.ToStatusResponse(job));
});

jobs.MapGet("/{jobId}/annotated-image", async (
    string jobId, IJobStore store, IScanImageStore images, CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    if (job?.Status != ScanJobStatus.Completed || job.Result?.AnnotatedImagePath is null)
        return Results.NotFound();
    var stream = await images.OpenReadAsync(ScanBlobPaths.BuildAnnotatedBlobPath(job), ct);
    return Results.File(stream, "image/png", $"scan-{job.JobId}-priced.png");
});

jobs.MapGet("/{jobId}/cards.csv", async (string jobId, IJobStore store, CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    if (job?.Status != ScanJobStatus.Completed || job.Result is null) return Results.NotFound();
    return Results.File(System.Text.Encoding.UTF8.GetBytes(MobileWebEndpoints.Csv(job.Result)),
        "text/csv; charset=utf-8", $"scan-{job.JobId}.csv");
});

app.Run();

/// <summary>Exposed so integration tests can construct the API host.</summary>
public partial class Program;

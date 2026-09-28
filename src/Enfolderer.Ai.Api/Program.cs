using Azure;
using Azure.Core;
using Azure.Identity;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;

// 64 MB: comfortably above a phone photograph, well below anything that would exhaust the
// B1 plan's memory while being relayed to blob storage.
const long MaxScanImageBytes = 64L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddScanPlatform(builder.Configuration, registerUploadUrlIssuer: true);

// Entra ID protection. The desktop client signs in interactively and presents a user token; it
// never holds a client secret or a storage key.
var entraConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (entraConfigured)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}
builder.Services.AddAuthorization();

var app = builder.Build();

if (entraConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else
{
    app.Logger.LogWarning(
        "AzureAd:TenantId is not configured; the scan API is running unauthenticated. " +
        "This is only acceptable for local development.");
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// 1. Create a job and hand back a short-lived, write-only upload grant for exactly one blob.
var jobs = app.MapGroup("/jobs");
if (entraConfigured) jobs.RequireAuthorization();

jobs.MapPost("/", async (
    CreateJobRequest request,
    IJobStore store,
    IUploadUrlIssuer issuer,
    ILoggerFactory loggerFactory,
    TokenCredential credential,
    ScanPlatformOptions platform,
    CancellationToken ct) =>
{
    var jobId = Guid.NewGuid().ToString("n");

    UploadTarget target;
    try
    {
        target = await issuer.IssueAsync(jobId, request.FileName, ct);

        var job = new ScanJobDocument
        {
            Id = jobId,
            JobId = jobId,
            Status = ScanJobStatus.Pending,
            BlobPath = target.BlobPath,
            GameHint = string.IsNullOrWhiteSpace(request.GameHint) ? null : CardGames.Normalize(request.GameHint)
        };

        // Recording the job authenticates with the same identity, so it can fail the same two ways
        // and belongs under the same handling.
        await store.CreateAsync(job, ct);
    }
    catch (RequestFailedException ex)
    {
        // Creating a job is the first thing that touches Azure, so a misconfigured or
        // not-yet-propagated role assignment surfaces here. Report the storage error code rather
        // than an unhandled 500, which says only that something went wrong somewhere.
        //
        // Name the principal too. "The managed identity needs role X" is unhelpful when the real
        // fault is that a different identity was presented than the one the role was granted to,
        // which is the failure a host carrying several identities actually produces.
        var principal = await IdentityDiagnostics.DescribeAsync(
            credential, "https://storage.azure.com/.default", ct);

        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Azure refused job {JobId} for {Principal} (configured client id {ClientId}): {ErrorCode}",
            jobId, principal, platform.ManagedIdentityClientId ?? "(unset)", ex.ErrorCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure Storage returned {ex.Status} {ex.ErrorCode} for the identity {principal}. "
                  + "That principal needs Storage Blob Delegator on the account and write access to "
                  + "the scans container. Compare the object id against `az role assignment list`: "
                  + "if it does not match, the site is presenting a different identity than the one "
                  + "the roles were granted to. A newly granted role can take several minutes to "
                  + "take effect.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    {
        // Cosmos reports its own failures through its own exception type, so a missing data-plane
        // role assignment would otherwise still surface as a bare 500. Only a refusal is read as a
        // permissions problem: throttling and outages use the same exception and must not send the
        // reader off to check role assignments.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Cosmos refused job {JobId}: {StatusCode}/{SubStatusCode}", jobId, ex.StatusCode, ex.SubStatusCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure Cosmos DB returned {(int)ex.StatusCode}/{ex.SubStatusCode}. The API's "
                  + "managed identity needs the Cosmos DB Built-in Data Contributor role on the "
                  + "account; that is a data-plane assignment, so it does not appear in "
                  + "`az role assignment list` and is not granted by Owner.",
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
        UploadUrl = target.UploadUrl,
        BlobPath = target.BlobPath,
        UploadExpiresAt = target.ExpiresAt
    });
});

// 2. Accept the image. The storage account is private, so the client cannot write to it directly;
// the API is the one public entry point and relays the bytes on the caller's behalf.
jobs.MapPut("/{jobId}/content", async (
    string jobId,
    HttpRequest request,
    IJobStore store,
    IScanImageStore images,
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

    await images.WriteAsync(job.BlobPath, request.Body, request.ContentType ?? "application/octet-stream", ct);
    return Results.Accepted();
});

// 3. Confirm the upload and queue the job for the worker.
jobs.MapPost("/{jobId}/submit", async (
    string jobId,
    IJobStore store,
    IJobQueue queue,
    IScanImageStore images,
    CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    if (job is null) return Results.NotFound();

    if (job.Status is not ScanJobStatus.Pending)
        return Results.Conflict(new { error = $"Job is already {job.Status}." });

    if (!await images.ExistsAsync(job.BlobPath, ct))
        return Results.BadRequest(new { error = "Image has not been uploaded yet." });

    var submitted = await store.UpsertAsync(job with { Status = ScanJobStatus.Uploaded }, ct);
    await queue.EnqueueAsync(new ScanJobMessage(submitted.JobId, submitted.BlobPath, submitted.GameHint), ct);

    return Results.Accepted($"/jobs/{jobId}", JobMapping.ToStatusResponse(submitted));
});

// 4. Poll for status and, once complete, the versioned result document.
jobs.MapGet("/{jobId}", async (string jobId, IJobStore store, CancellationToken ct) =>
{
    var job = await store.GetAsync(jobId, ct);
    return job is null ? Results.NotFound() : Results.Ok(JobMapping.ToStatusResponse(job));
});

app.Run();

/// <summary>Exposed so integration tests can construct the API host.</summary>
public partial class Program;

using Azure;
using Azure.Identity;
using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;

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
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not create job {JobId}: {ErrorCode}", jobId, ex.ErrorCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure returned {ex.Status} {ex.ErrorCode}. The API's managed identity needs "
                  + "Storage Blob Delegator on the account, write access to the scans container, "
                  + "and the Cosmos data contributor role; a newly granted role can take several "
                  + "minutes to take effect.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (CosmosException ex)
    {
        // Cosmos reports its own failures through its own exception type, so a missing data-plane
        // role assignment would otherwise still surface as a bare 500.
        loggerFactory.CreateLogger("Jobs").LogError(
            ex, "Could not record job {JobId}: {StatusCode}", jobId, ex.StatusCode);

        return Results.Problem(
            title: "Could not create the scan job.",
            detail: $"Azure Cosmos DB returned {(int)ex.StatusCode}. The API's managed identity "
                  + "needs the Cosmos DB Built-in Data Contributor role on the account; that is a "
                  + "data-plane assignment, so it does not appear in `az role assignment list`.",
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

// 2. Local development only: accept the image directly when no storage account is configured.
jobs.MapPut("/{jobId}/content", async (
    string jobId,
    HttpRequest request,
    IJobStore store,
    IUploadUrlIssuer issuer,
    IScanImageStore images,
    CancellationToken ct) =>
{
    if (issuer is not LocalUploadUrlIssuer)
        return Results.NotFound();

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

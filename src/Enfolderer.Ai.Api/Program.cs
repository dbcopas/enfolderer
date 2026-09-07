using Enfolderer.Ai.Api;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
    CancellationToken ct) =>
{
    var jobId = Guid.NewGuid().ToString("n");
    var target = await issuer.IssueAsync(jobId, request.FileName, ct);

    var job = new ScanJobDocument
    {
        Id = jobId,
        JobId = jobId,
        Status = ScanJobStatus.Pending,
        BlobPath = target.BlobPath,
        GameHint = string.IsNullOrWhiteSpace(request.GameHint) ? null : CardGames.Normalize(request.GameHint)
    };
    await store.CreateAsync(job, ct);

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

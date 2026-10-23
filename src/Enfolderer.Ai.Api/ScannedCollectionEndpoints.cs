using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Api;

public static class ScannedCollectionEndpoints
{
    public static void MapScannedCollection(this WebApplication app, RouteGroupBuilder jobs)
    {
        var collection = app.MapGroup("/scanned-collection").RequireAuthorization();
        collection.AddEndpointFilter<JobOwnerFilter>();
        collection.MapGet("/", async (string? set, HttpContext http, IScannedCollectionStore store,
            IScanImageStore images, ILoggerFactory logs, CancellationToken ct) => await ReadAsync(set, http, store, images, logs, false, ct));
        collection.MapGet("/cards.csv", async (string? set, HttpContext http, IScannedCollectionStore store,
            IScanImageStore images, ILoggerFactory logs, CancellationToken ct) => await ReadAsync(set, http, store, images, logs, true, ct));

        jobs.MapGet("/{jobId}/collection", async (string jobId, HttpContext http,
            IScannedCollectionStore collections, CancellationToken ct) =>
        {
            var owner = JobOwnerFilter.ReadOwner(http.User)!.Value;
            return Results.Ok(new { added = await collections.ContainsAsync(owner.TenantId, owner.ObjectId, jobId, ct) });
        });

        jobs.MapPost("/{jobId}/collection", async (string jobId, HttpContext http, IJobStore jobsStore,
            IScannedCollectionStore collections, ILoggerFactory logs, CancellationToken ct) =>
        {
            var job = await jobsStore.GetAsync(jobId, ct);
            if (job?.Status != ScanJobStatus.Completed || job.Result is null)
                return Results.Conflict(new { error = "Only a completed scan can be added to your scanned collection." });
            var addition = ScannedCollectionAddition.FromResult(jobId, job.Result);
            if (addition.Printings.Count == 0)
                return Results.BadRequest(new { error = "This scan has no identified cards with set and collector number to add." });
            var owner = JobOwnerFilter.ReadOwner(http.User)!.Value;
            var added = await collections.AddAsync(owner.TenantId, owner.ObjectId, addition, ct);
            logs.CreateLogger("ScannedCollection").LogInformation(
                "Scan {JobId} collection addition: {Outcome}, {Printings} printings.",
                jobId, added ? "added" : "already added", addition.Printings.Count);
            return Results.Ok(new
            {
                added,
                quantity = addition.Printings.Sum(p => p.Quantity),
                printings = addition.Printings.Count,
                skipped = job.Result.Cards.Count - addition.Printings.Sum(p => p.Quantity)
            });
        });
    }

    private static async Task<IResult> ReadAsync(string? set, HttpContext http, IScannedCollectionStore store,
        IScanImageStore images, ILoggerFactory logs, bool csv, CancellationToken ct)
    {
        set = set?.Trim().ToUpperInvariant();
        if (set is { Length: > 0 } && (set.Length > 12 || !set.All(char.IsAsciiLetterOrDigit)))
        {
            logs.CreateLogger("ScannedCollection").LogWarning("Rejected invalid scanned collection set filter.");
            return Results.BadRequest(new { error = "Choose a valid set code." });
        }
        var owner = JobOwnerFilter.ReadOwner(http.User)!.Value;
        var printings = await store.ReadAsync(owner.TenantId, owner.ObjectId, ct);
        var filtered = string.IsNullOrEmpty(set) ? printings
            : printings.Where(p => string.Equals(p.Set, set, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (csv)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(ScanCsv.Format(filtered));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var path = $"collections/{owner.TenantId}/{owner.ObjectId}/exports/scanned/{(string.IsNullOrEmpty(set) ? "all" : set)}-{hash}.csv";
            await using var content = new MemoryStream(bytes, writable: false);
            await images.WriteAsync(path, content, "text/csv; charset=utf-8", ct);
            logs.CreateLogger("ScannedCollection").LogInformation("Archived collection CSV {Path}.", path);
            return Results.File(bytes, "text/csv; charset=utf-8",
                $"scanned-collection{(string.IsNullOrEmpty(set) ? "" : "-" + set)}.csv");
        }
        return Results.Ok(new
        {
            sets = printings.Select(p => p.Set.ToUpperInvariant()).Distinct().Order().ToArray(),
            totalPrintings = printings.Count,
            totalQuantity = printings.Sum(p => (long)p.Quantity),
            printings = filtered
        });
    }
}

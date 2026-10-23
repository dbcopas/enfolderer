using System.Text;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Api;

public static class ScanArchiveEndpoints
{
    public static void MapScanArchives(this RouteGroupBuilder jobs)
    {
        jobs.MapGet("/{jobId}/identification.json", async (string jobId, IJobStore store,
            IScanImageStore images, ILoggerFactory logs, CancellationToken ct) =>
            await EvidenceAsync(await store.GetAsync(jobId, ct), null, images, logs, ct));
        jobs.MapGet("/{jobId}/identification/{index:int}", async (string jobId, int index, IJobStore store,
            IScanImageStore images, ILoggerFactory logs, CancellationToken ct) =>
            await EvidenceAsync(await store.GetAsync(jobId, ct), index, images, logs, ct));
    }

    public static async Task<IResult> CsvAsync(ScanJobDocument job, IScanImageStore images, CancellationToken ct)
    {
        if (job.Result is not { } result) return Results.NotFound();
        var name = $"scan-{job.JobId}.csv";
        if (result.CsvBlobPath is not null)
            return Results.File(await images.OpenReadAsync(ScanBlobPaths.BuildCsvBlobPath(job), ct),
                "text/csv; charset=utf-8", name);
        return Results.File(Encoding.UTF8.GetBytes(ScanCsv.Format(result)), "text/csv; charset=utf-8", name);
    }

    public static async Task<IResult> EvidenceAsync(ScanJobDocument? job, int? index,
        IScanImageStore images, ILoggerFactory logs, CancellationToken ct)
    {
        if (job is null) return Results.NotFound();
        var path = ScanBlobPaths.BuildEvidenceBlobPath(job, index);
        if (index is < 0 || index >= job.CardsDetected || !await images.ExistsAsync(path, ct))
        {
            logs.CreateLogger("ScanArchives").LogWarning(
                "Identification evidence unavailable for job {JobId}, card {Index}.", job.JobId, index);
            return Results.NotFound(new { error = "Identification evidence was not archived for this scan or card." });
        }
        return Results.File(await images.OpenReadAsync(path, ct), "application/json",
            index is { } card ? $"scan-{job.JobId}-card-{card:D3}.json" : $"scan-{job.JobId}-identification.json");
    }
}

using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Api;

/// <summary>Projects the stored job document onto the wire contract returned to clients.</summary>
public static class JobMapping
{
    public static JobStatusResponse ToStatusResponse(ScanJobDocument job) => new()
    {
        JobId = job.JobId,
        Status = job.Status,
        CardsDetected = job.CardsDetected,
        CardsIdentified = job.CardsIdentified,
        Error = job.Error,
        // The result document is only meaningful once the pipeline finished.
        Result = job.Status == ScanJobStatus.Completed ? job.Result : null
    };
}

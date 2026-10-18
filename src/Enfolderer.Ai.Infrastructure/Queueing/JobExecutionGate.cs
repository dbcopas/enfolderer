using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Infrastructure.Queueing;

public interface IJobExecutionGate
{
    Task RunAsync(string jobId, Func<CancellationToken, Task> action, CancellationToken ct);
}

public sealed class LocalJobExecutionGate : IJobExecutionGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public async Task RunAsync(string jobId, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(jobId, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try { await action(ct); }
        finally { gate.Release(); }
    }
}

public sealed class BlobJobExecutionGate(BlobServiceClient blobs, ILogger<BlobJobExecutionGate> log) : IJobExecutionGate
{
    public async Task RunAsync(string jobId, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var blob = blobs.GetBlobContainerClient("outputs").GetBlobClient($"execution/{Guid.Parse(jobId):N}");
        try { await blob.UploadAsync(BinaryData.FromString(""), overwrite: false, cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.ErrorCode == "BlobAlreadyExists") { }
        var lease = blob.GetBlobLeaseClient();
        try { await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.ErrorCode == "LeaseAlreadyPresent")
        {
            log.LogInformation("Job {JobId} is already being processed; ignoring duplicate delivery.", jobId);
            return;
        }
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task RenewAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), renewal.Token);
                    await lease.RenewAsync(cancellationToken: renewal.Token);
                }
            }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Lost execution lease for job {JobId}; cancelling processing.", jobId);
                work.Cancel();
                throw;
            }
        }
        var renewTask = RenewAsync();
        try { await action(work.Token); }
        finally
        {
            renewal.Cancel();
            try { await renewTask; }
            finally
            {
                try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); }
                catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
                {
                    log.LogWarning(ex, "Execution lease for job {JobId} was already lost.", jobId);
                }
            }
        }
    }
}

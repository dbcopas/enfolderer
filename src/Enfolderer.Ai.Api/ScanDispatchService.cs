using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Allowances;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;

namespace Enfolderer.Ai.Api;

public sealed class ScanDispatchService(IScanAllowanceStore allowances, IJobStore jobs, IJobQueue queue,
    ILogger<ScanDispatchService> log) : BackgroundService
{
    public async Task DispatchAsync(ScanDispatchReceipt receipt, CancellationToken ct)
    {
        if (receipt.DispatchedAt is not null) return;
        var job = await jobs.GetAsync(receipt.Message.JobId, ct)
            ?? throw new InvalidOperationException($"Reserved scan {receipt.Message.JobId} is absent; inspect the receipt and refund if appropriate.");
        if (job.Status is not (ScanJobStatus.Completed or ScanJobStatus.Failed))
        {
            await jobs.MarkUploadedAsync(job.JobId, ct);
            await queue.EnqueueAsync(receipt.Message, ct);
        }
        await allowances.MarkDispatchedAsync(receipt.OwnerKey, receipt.Message.JobId, ct);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await foreach (var receipt in allowances.ListPendingAsync(ct))
        {
            try { await DispatchAsync(receipt, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reserved job {JobId} could not be dispatched; receipt retained for retry.",
                    receipt.Message.JobId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                await RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Scan dispatch recovery failed; retrying on the next interval."); }
        }
    }
}

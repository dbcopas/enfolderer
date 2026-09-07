using Enfolderer.Ai.Worker.Pipeline;
using Enfolderer.Ai.Worker.Queueing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker;

/// <summary>Drains the job queue and runs the two-project agent pipeline for each message.</summary>
public sealed class ScanJobWorker : BackgroundService
{
    private readonly IJobQueueConsumer _consumer;
    private readonly ScanJobProcessor _processor;
    private readonly ScanPipelineOptions _options;
    private readonly ILogger<ScanJobWorker> _log;

    public ScanJobWorker(
        IJobQueueConsumer consumer,
        ScanJobProcessor processor,
        ScanPipelineOptions options,
        ILogger<ScanJobWorker> log)
    {
        _consumer = consumer;
        _processor = processor;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Scan worker started. Geometry project: {Geometry}. Identification project: {Identification}.",
            _options.GeometryProjectEndpoint ?? "(stub)",
            _options.IdentificationProjectEndpoint ?? "(stub)");

        while (!stoppingToken.IsCancellationRequested)
        {
            bool handled;
            try
            {
                handled = await _consumer.TryDequeueAsync(_processor.ProcessAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Queue polling failed; backing off.");
                handled = false;
            }

            if (!handled)
            {
                try { await Task.Delay(_options.QueuePollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}

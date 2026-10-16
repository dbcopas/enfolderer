using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;

namespace Enfolderer.Ai.Worker.Queueing;

/// <summary>Pull side of the job queue. Mirrors <see cref="IJobQueue"/>.</summary>
public interface IJobQueueConsumer
{
    /// <summary>
    /// Receives the next message, invoking <paramref name="handler"/> before deleting it. Returns
    /// false when the queue was empty.
    /// </summary>
    Task<bool> TryDequeueAsync(Func<ScanJobMessage, CancellationToken, Task> handler, CancellationToken ct);
}

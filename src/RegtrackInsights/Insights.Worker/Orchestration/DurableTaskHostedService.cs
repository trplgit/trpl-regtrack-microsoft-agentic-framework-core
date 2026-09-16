using DurableTask.Core;
using Microsoft.Extensions.Hosting;

namespace Insights.Worker.Orchestration;

/// <summary>
/// TaskHubWorker is not natively IHostedService-shaped (confirmed Task 1) - this wrapper starts
/// it with the generic host and stops it gracefully on shutdown. Owns the worker's lifetime only;
/// TaskHubClient (used to enqueue runs, Task 16) is registered separately since it has no
/// start/stop lifecycle of its own.
///
/// [ADDED] Flips DurableTaskWorkerReadiness so /health/ready can answer "is this pod dequeuing"
/// without polling anything itself - true only once worker.StartAsync() has actually returned,
/// false again the moment StopAsync begins so a draining pod correctly reports not-ready.
/// </summary>
public sealed class DurableTaskHostedService(TaskHubWorker worker, DurableTaskWorkerReadiness readiness) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await worker.StartAsync();
        readiness.IsReady = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        readiness.IsReady = false;
        await worker.StopAsync(isForced: false);
    }
}

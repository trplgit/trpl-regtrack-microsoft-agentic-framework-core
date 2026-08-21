using DurableTask.Core;
using Microsoft.Extensions.Hosting;

namespace Insights.Worker.Orchestration;

/// <summary>
/// TaskHubWorker is not natively IHostedService-shaped (confirmed Task 1) - this wrapper starts
/// it with the generic host and stops it gracefully on shutdown. Owns the worker's lifetime only;
/// TaskHubClient (used to enqueue runs, Task 16) is registered separately since it has no
/// start/stop lifecycle of its own.
/// </summary>
public sealed class DurableTaskHostedService(TaskHubWorker worker) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) => await worker.StartAsync();

    public async Task StopAsync(CancellationToken cancellationToken) => await worker.StopAsync(isForced: false);
}

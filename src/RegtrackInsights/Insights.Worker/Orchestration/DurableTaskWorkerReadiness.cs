namespace Insights.Worker.Orchestration;

/// <summary>
/// One shared flag, flipped by <see cref="DurableTaskHostedService"/> and read by
/// DurableTaskWorkerHealthCheck (Insights.Worker.HealthChecks). This is the only signal
/// /health/ready needs for orchestration readiness: RegisterSqlOrchestrationService already blocks
/// on <c>CreateIfNotExistsAsync()</c> during DI resolution (WorkerRegistration.cs), so by the time
/// <see cref="DurableTaskHostedService.StartAsync"/> returns, the task-hub database connection is
/// already proven - there is nothing left to poll.
///
/// Deliberately NOT a health check that resolves SqlOrchestrationService itself: doing that on a
/// request thread would risk re-running that same blocking schema call. This is a plain in-memory
/// flag, singleton-registered, set once at startup and cleared once at shutdown.
///
/// Defaults to true when Insights:ClientOnly=true - see AddInsightsHealthChecks's own doc comment.
/// There is deliberately no Durable Task worker in that mode, so a check that stays false forever
/// would be a readiness probe that fails by design, and a probe that fails by design gets deleted.
/// </summary>
public sealed class DurableTaskWorkerReadiness
{
    public DurableTaskWorkerReadiness(bool clientOnly)
    {
        IsReady = clientOnly;
    }

    public bool IsReady { get; set; }
}

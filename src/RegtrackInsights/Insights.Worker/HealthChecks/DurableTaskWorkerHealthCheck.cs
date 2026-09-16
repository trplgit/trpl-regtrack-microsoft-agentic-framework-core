using Insights.Worker.Orchestration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Insights.Worker.HealthChecks;

/// <summary>
/// Readiness (tag "ready") only - reads the in-process DurableTaskWorkerReadiness flag, never
/// resolves SqlOrchestrationService itself. That singleton's factory blocks on
/// CreateIfNotExistsAsync().GetAwaiter().GetResult() (WorkerRegistration.cs) - a health check that
/// triggered that on a request thread would risk re-running schema DDL from an HTTP call.
///
/// Deliberately does NOT check SQL, the LLM, or blob storage - see AddInsightsHealthChecks's own
/// doc comment for why those are out of scope for now.
/// </summary>
public sealed class DurableTaskWorkerHealthCheck(DurableTaskWorkerReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("Durable Task worker is dequeuing.")
            : HealthCheckResult.Unhealthy("Durable Task worker has not started (or is shutting down)."));
}

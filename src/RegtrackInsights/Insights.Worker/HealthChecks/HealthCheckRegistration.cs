using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker.HealthChecks;

/// <summary>
/// /health, /health/live, /health/ready - same three paths and the same X-Health-Token gate as
/// the sibling RegTrack API, so ops has one mental model, one Secret shape and one probe idiom
/// across both services. Call this once from Program.cs, independent of every other AddInsights*
/// registration - health checks must never depend on (or be blocked by) anything they are meant
/// to report on.
///
/// Scope, deliberately narrow:
///   /health/live  -> "self" only, always healthy, touches nothing. Liveness must never depend on
///                     anything restartable, or a transient dependency outage becomes a crash loop.
///   /health/ready -> "self" + "durable_task_worker" (DurableTaskWorkerHealthCheck). NOT SQL, NOT
///                     the LLM, NOT blob storage - a SQL check would be new recurring database
///                     traffic that needs its own sign-off (the codebase's DB-change rule), and an
///                     LLM check would bill tokens and restart a healthy pod over a vendor outage
///                     the whole pipeline is already designed to degrade around instead of crash on.
///   /health       -> everything, for humans. Token-gated the same as the other two; never probed
///                     by Kubernetes itself.
/// </summary>
public static class HealthCheckRegistration
{
    public static IServiceCollection AddInsightsHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HealthCheckAuthConfig>(configuration.GetSection(HealthCheckAuthConfig.SectionName));

        // Same clientOnly read Program.cs already makes for AddInsightsOrchestration - kept as a
        // second, independent read rather than threaded through as a parameter, so this file has
        // no dependency on Program.cs's local variable layout. In ClientOnly mode there genuinely
        // is no Durable Task worker to become ready, so the flag starts (and stays) true rather
        // than modelling a readiness condition that mode can never satisfy.
        var clientOnly = configuration.GetValue("Insights:ClientOnly", false);
        services.AddSingleton(new DurableTaskWorkerReadiness(clientOnly));

        services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DurableTaskWorkerHealthCheck>("durable_task_worker", tags: ["ready"]);

        return services;
    }
}

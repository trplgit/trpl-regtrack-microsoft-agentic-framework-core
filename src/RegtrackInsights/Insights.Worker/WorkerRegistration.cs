using Insights.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Worker-side services that are not specific to one tier.
///
/// Call AFTER AddInsightsData - the publish gate depends on IScopeRepository for its post-flight
/// audit.
/// </summary>
public static class WorkerRegistration
{
    public static IServiceCollection AddInsightsWorker(this IServiceCollection services)
    {
        /*  The publish gate (build order step 8) - the deterministic arbiter that runs after
            narrative reflection and before anything renders. Nothing reaches a customer without
            passing it.

            Registered as the concrete type because it has no interface: it is a pure decision
            over inputs it is handed, with one dependency, and there is nothing to substitute.  */
        services.AddScoped<PublishGate>();

        return services;
    }
}

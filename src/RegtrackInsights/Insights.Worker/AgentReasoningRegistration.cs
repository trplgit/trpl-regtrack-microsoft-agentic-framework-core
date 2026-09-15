using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Registers IAgentReasoningRecorder (sql/32_agent_reasoning_log.sql). The three activities that
/// consume it (NarrateActivity, ReflectOnNarrativeActivity, RenderHtmlActivity) are registered by
/// AddInsightsOrchestrationWorker like every other activity - call this BEFORE that.
///
/// Call AFTER AddInsightsData (needs ConnectionStrings:RegTrack).
///
/// Same ConnectionStrings:RegTrackReportsWrite-first fallback as TenantTokenBudgetRegistration -
/// this table is a WRITE, same least-privilege split reasoning.
/// </summary>
public static class AgentReasoningRegistration
{
    public static IServiceCollection AddInsightsAgentReasoning(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ConnectionStrings:RegTrackReportsWrite"]
            ?? Require(configuration, "ConnectionStrings:RegTrack");
        services.AddScoped<IAgentReasoningRecorder>(_ => new SqlAgentReasoningRecorder(connectionString));

        return services;
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}

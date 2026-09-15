using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Registers design doc Sec.12.3's per-tenant monthly circuit breaker and 80%-alert:
/// ITenantTokenBudgetRepository and TenantTokenBudgetSettings. The two activities that consume
/// them (CheckTenantTokenBudgetActivity, RecordTenantTokenUsageActivity) are registered by
/// AddInsightsOrchestrationWorker like every other activity - call this BEFORE that, same ordering
/// PaidKeepWarmRegistration documents for its own dependencies.
///
/// Call AFTER AddInsightsData (needs ConnectionStrings:RegTrack).
///
/// [FIX, 2026-09-11] InsightsTenantTokenUsage is a WRITE, same table class as GeneratedReport -
/// same ConnectionStrings:RegTrackReportsWrite override RegisterReportsDbContext already falls
/// back to (WorkerRegistration.cs), for the same reason: least-privilege DB accounts split reads
/// (broad EXECUTE on the usp_Insights_* procs) from writes (INSERT/UPDATE on specific tables
/// only) - confirmed live when the write-scoped account's first real run failed with EXECUTE
/// permission denied on usp_Insights_EligibleTenants, a READ this repository has no business
/// running under a write-only credential in the first place.
/// </summary>
public static class TenantTokenBudgetRegistration
{
    public static IServiceCollection AddInsightsTenantTokenBudget(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ConnectionStrings:RegTrackReportsWrite"]
            ?? Require(configuration, "ConnectionStrings:RegTrack");
        services.AddScoped<ITenantTokenBudgetRepository>(_ => new SqlTenantTokenBudgetRepository(connectionString));

        /*  Eager, not a factory lambda - same trap PaidKeepWarmRegistration/FreeDigestRegistration
            already call out: a missing key must throw while the host starts, not on the first run
            of the month that happens to need this check.                                          */
        var settings = new TenantTokenBudgetSettings
        {
            MonthlyCeiling = configuration.GetValue<long?>("Budget:PerTenantMonthlyTokenCeiling")
                ?? throw new InvalidOperationException("Budget:PerTenantMonthlyTokenCeiling is not configured."),
            AlertAtPercent = configuration.GetValue<int?>("Budget:AlertAtPercentOfCeiling")
                ?? throw new InvalidOperationException("Budget:AlertAtPercentOfCeiling is not configured."),
        };
        services.AddSingleton(settings);

        return services;
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}

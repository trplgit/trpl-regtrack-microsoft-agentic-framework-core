using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Registers the paid keep-warm lane (design doc Sec.4.2-4.5): IPaidTenantRepository and the
/// scheduler itself.
///
/// Call AFTER AddInsightsData (needs ConnectionStrings:RegTrack) and AddInsightsOrchestration
/// (needs IInsightsRunEnqueuer, and InsightsReportsDbContext - registered by
/// AddInsightsOrchestrationWorker, which AddInsightsOrchestration calls).
/// </summary>
public static class PaidKeepWarmRegistration
{
    public static IServiceCollection AddInsightsPaidKeepWarm(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = Require(configuration, "ConnectionStrings:RegTrack");
        services.AddScoped<IPaidTenantRepository>(_ => new SqlPaidTenantRepository(connectionString));

        /*  Same trap FreeDigestRegistration's own comment calls out: a factory lambda is not
            startup validation. Every required value is read HERE, eagerly, so a missing key
            throws while the host is still starting rather than on the first due tenant.        */
        var settings = new PaidKeepWarmSettings
        {
            ScheduleEnabled = configuration.GetValue("Schedule:PaidKeepWarm:Enabled", false),
            ScheduleCheckInterval = TimeSpan.FromMinutes(configuration.GetValue("Schedule:PaidKeepWarm:CheckIntervalMinutes", 60)),
            ScheduleRunHourUtc = configuration.GetValue("Schedule:PaidKeepWarm:RunHourUtc", 6),
            SchedulePerTenantDelay = TimeSpan.FromMilliseconds(configuration.GetValue("Schedule:PaidKeepWarm:PerTenantDelayMs", 250)),
            AnchorModulo = configuration.GetValue<int?>("Schedule:PaidAnchorModulo")
                ?? throw new InvalidOperationException("Schedule:PaidAnchorModulo is not configured."),
            KeepWarmWindowDays = configuration.GetValue<int?>("Reports:KeepWarmWindowDays")
                ?? throw new InvalidOperationException("Reports:KeepWarmWindowDays is not configured."),
            CooldownDays = configuration.GetValue<int?>("Reports:CooldownDays")
                ?? throw new InvalidOperationException("Reports:CooldownDays is not configured."),
        };
        services.AddSingleton(settings);

        /*  Registered unconditionally but INERT unless Schedule:PaidKeepWarm:Enabled is true -
            same reasoning as FreeDigestRegistration: a worker started for any other reason must
            not begin regenerating (and re-billing tokens for) paid reports because it booted.   */
        services.AddHostedService<PaidKeepWarmScheduler>();

        return services;
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}

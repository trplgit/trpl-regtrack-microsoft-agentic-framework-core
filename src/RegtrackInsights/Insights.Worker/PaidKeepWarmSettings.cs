namespace Insights.Worker;

/// <summary>
/// Everything the paid keep-warm lane needs from configuration (design doc Sec.4.2-4.5). Bound
/// from Schedule:PaidKeepWarm:*, Schedule:PaidAnchorModulo and Reports:* - see
/// PaidKeepWarmRegistration.
/// </summary>
public sealed class PaidKeepWarmSettings
{
    /// <summary>Schedule:PaidKeepWarm:Enabled. Off by default - a worker must not start regenerating paid reports merely because it booted.</summary>
    public bool ScheduleEnabled { get; init; }

    /// <summary>How often the batch lane wakes to look for due tenants.</summary>
    public TimeSpan ScheduleCheckInterval { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Hour (UTC) before which the lane will not run, so refreshes do not land overnight unattended.</summary>
    public int ScheduleRunHourUtc { get; init; } = 6;

    /// <summary>Pause between tenants - spreads arrival so a multi-tenant day does not enqueue as one burst (Sec.4.4).</summary>
    public TimeSpan SchedulePerTenantDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Schedule:PaidAnchorModulo (Sec.4.2) - day_of_month = hash(tenantId) % this, spread across the month.</summary>
    public required int AnchorModulo { get; init; }

    /// <summary>Reports:KeepWarmWindowDays (Sec.4.3) - only refresh a key whose latest report was viewed within this many days.</summary>
    public required int KeepWarmWindowDays { get; init; }

    /// <summary>Reports:CooldownDays (Sec.4.5) - never refresh a key generated more recently than this.</summary>
    public required int CooldownDays { get; init; }
}

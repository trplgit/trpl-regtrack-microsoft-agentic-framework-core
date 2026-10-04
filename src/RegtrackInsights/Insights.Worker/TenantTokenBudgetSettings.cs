namespace Insights.Worker;

/// <summary>
/// Design doc Sec.12.3's two per-tenant safety valves: refuse outright once a tenant's
/// month-to-date spend hits <see cref="MonthlyCeiling"/>, and log a warning once it crosses
/// <see cref="AlertAtPercent"/> of that ceiling so ops sees it coming before the hard stop.
/// Bound from Budget:PerTenantMonthlyTokenCeiling / Budget:AlertAtPercentOfCeiling /
/// Budget:PerRunTokenCeiling - see TenantTokenBudgetRegistration.
/// </summary>
public sealed class TenantTokenBudgetSettings
{
    public required long MonthlyCeiling { get; init; }
    public required int AlertAtPercent { get; init; }

    /// <summary>
    /// Budget:PerRunTokenCeiling - the per-run ceiling InsightsReportOrchestrator's ChargeAndCheck
    /// enforces. The orchestrator cannot read configuration itself (its body must replay
    /// deterministically), so CheckTenantTokenBudgetActivity hands this value to it in its output.
    /// </summary>
    public long PerRunCeiling { get; init; } = 250_000;
}

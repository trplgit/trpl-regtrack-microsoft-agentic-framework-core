using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// The paid keep-warm scheduler's first-pass tenant filter (design doc Sec.4.2-4.3) - every
/// customer currently entitled to RegInsights Pro. Cheap, and NOT the real gate: the orchestration
/// itself re-checks entitlement and scope for the actual triggering user at run time
/// (GatherScopeActivity), same as every other trigger path. This only avoids enqueueing candidates
/// for a tenant whose paid entitlement is obviously gone.
/// </summary>
public interface IPaidTenantRepository
{
    Task<IReadOnlyList<PaidKeepWarmTenant>> GetEntitledTenantsAsync(CancellationToken cancellationToken = default);
}

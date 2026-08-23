using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps dbo.usp_Insights_EligibleTenants (sql/17) - the IDOR guard.
///
/// [TRAP] This is the ONLY authority on which tenants a user may see. Every endpoint that takes
/// a client-supplied tenantId must call <see cref="IsEligibleAsync"/> for that exact id on that
/// exact request, and refuse when it returns false. Resolving eligibility once at login and
/// trusting it afterwards is the defect this interface exists to prevent: 53 users legitimately
/// span more than one customer, so a tenant switch and an edited URL look identical at the HTTP
/// layer. Only a fresh query tells them apart.
/// </summary>
public interface ITenantDirectoryRepository
{
    /// <summary>
    /// Every tenant this user may see Insights for. An empty list is the secure-deny state
    /// (spec §5.6.3), never an error and never "all tenants".
    /// </summary>
    Task<IReadOnlyList<EligibleTenant>> GetEligibleTenantsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Membership test for one client-supplied tenant id. Returns the tenant when the user is
    /// eligible for it, null otherwise - null means REFUSE, and callers must not distinguish
    /// "does not exist" from "not yours" in what they return to the caller.
    /// </summary>
    Task<EligibleTenant?> IsEligibleAsync(int userId, int customerId, CancellationToken cancellationToken = default);
}

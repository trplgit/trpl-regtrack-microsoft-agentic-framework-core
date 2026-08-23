namespace Insights.Domain;

/// <summary>
/// One tenant an authenticated user is allowed to see Insights for
/// (API_CONTRACTS.md §1). Produced only by dbo.usp_Insights_EligibleTenants -
/// never assembled in C# from a client-supplied id.
/// </summary>
/// <param name="TenantId">Customer.ID. The value the client may then send back as tenantId.</param>
/// <param name="Name">Customer.Name, for the tenant picker.</param>
/// <param name="Tier">
/// <c>basic</c> (product 18) or <c>pro</c> (product 19), resolved PAID-WINS. A tenant
/// mid-transition is briefly mapped to both; reporting basic then would downgrade a paying
/// customer's UI for the duration.
/// </param>
/// <param name="ScopeClass">
/// This user's scope within this tenant. <see cref="Domain.ScopeClass.Deny"/> can never appear
/// here - zero scope fails eligibility, so such a tenant is absent from the list entirely rather
/// than present and denied.
/// </param>
public sealed record EligibleTenant(int TenantId, string Name, EntitlementTier Tier, ScopeClass ScopeClass);

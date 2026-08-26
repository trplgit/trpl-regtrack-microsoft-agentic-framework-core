namespace Insights.Domain;

/// <summary>The universe the paid keep-warm scheduler picks its due-today tenants from (design doc Sec.4.2-4.3).</summary>
public sealed record PaidKeepWarmTenant(int CustomerId, string TenantName);

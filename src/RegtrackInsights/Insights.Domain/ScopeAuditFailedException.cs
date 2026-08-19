namespace Insights.Domain;

/// <summary>
/// Thrown when usp_Insights_AuditScope finds out-of-scope rows (error 51010).
/// The post-flight net behind pre-flight scope constraints (spec Section 14 / D3) -
/// a caller catching this MUST refuse to publish, never degrade to a warning.
/// </summary>
public sealed class ScopeAuditFailedException(int userId, int customerId, Exception inner)
    : Exception($"Scope audit failed for user {userId}, tenant {customerId} - out-of-scope rows detected. Refusing to publish.", inner)
{
    public int UserId { get; } = userId;
    public int CustomerId { get; } = customerId;
}

namespace Insights.Domain;

public enum EntitlementTier
{
    Free,
    Paid,
}

/// <summary>Gate order is cheapest-first, so an unentitled tenant costs literally nothing (sql/04).</summary>
public enum EntitlementDecision
{
    Proceed,

    /// <summary>Tenant disabled, or not mapped-and-enabled for this tier.</summary>
    ExitZeroCost,

    /// <summary>Free tier only: paid tier is active, free digest self-skips.</summary>
    ExitSuperseded,

    /// <summary>No enabled recipients - exit before any aggregation or LLM spend.</summary>
    ExitNoRecipients,
}

/// <summary>
/// Result of usp_Insights_EvaluateGate. [TRAP] ProductMapping.IsActive is INVERTED -
/// 0 means ENABLED. That inversion lives entirely in the SQL; this wrapper only
/// types the already-computed decision, it never re-derives entitlement itself.
/// </summary>
public sealed record EntitlementGateResult(
    int CustomerId, EntitlementTier Tier, EntitlementDecision Decision,
    int RecipientCount, string Reason, bool ShouldProceed);

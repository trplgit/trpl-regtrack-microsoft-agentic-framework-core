namespace Insights.Domain;

/// <summary>
/// Result of usp_Insights_FreeDigestGate (sql/06). Same decision vocabulary as the general
/// entitlement gate, evaluated at JOB EXECUTION TIME rather than cached at schedule time -
/// that is what makes mid-cycle transitions correct, so a tenant upgraded on Wednesday does not
/// receive Thursday's free digest.
/// </summary>
public sealed record FreeDigestGateResult(
    int CustomerId, EntitlementDecision Decision, int RecipientCount, string Reason, bool ShouldProceed);

/// <summary>
/// The complete LLM input for one free weekly digest - fifteen integers and nothing else.
/// RAW ROWS ARE NEVER PASSED TO THE MODEL.
///
/// That is the whole cost architecture: deterministic SQL collapses the window to these numbers,
/// so a 30-day analysis costs exactly what a 7-day one does. Choose the window for VALUE, never
/// for cost.
///
/// -- BANNED CLAIMS (spec 10.6, non-negotiable) ----------------------------------------------
/// A completion RATIO over a recent window must NEVER be computed from these figures. Raw
/// "missed last week" numbers look catastrophic - one tenant showed 272 of 322 not closed, ~84% -
/// but that is RECENCY LAG, not failure: an item due three days ago and still inside its normal
/// review cycle has not been missed. This is why the only backward-looking value here is
/// <see cref="CompletedLast7"/>, an ABSOLUTE COUNT with no denominator. Do not give it one.
///
/// The word "overdue" as a level is reserved for the paid engine, as is any per-location,
/// per-user or per-Act attribution.
///
/// -- THE CONVERSION BOUNDARY (spec 10.7) -----------------------------------------------------
/// Free shows the WHAT and never the WHERE / WHO / WHY. The gap between the number and its
/// explanation is the sales pitch.
/// </summary>
public sealed record FreeDigestAggregates(
    int CustomerId,
    DateTime GeneratedAt,

    // context
    int TotalActiveObligations,

    // this week - next 7 days
    int DueNext7,
    int CriticalDueNext7,
    int ImprisonmentDueNext7,
    int BranchesInScope,

    // severity radar - next 30 days, the conversion hook
    int DueNext30,
    int ImprisonmentDueNext30,
    int LicencesLapsingNext30,
    int CriticalDueNext30,

    /// <summary>ABSOLUTE COUNT ONLY. Never turn this into a rate - see the type remarks.</summary>
    int CompletedLast7,

    // shape hints for the email template
    int DueNext14,
    int DistinctImprisonmentObligations,
    int BranchesWithUpcoming);

/// <summary>
/// Thrown when the free digest cannot resolve the RiskType value meaning Critical from the
/// dictionary (SQL error 51040).
///
/// This matters more here than anywhere else in the system: a wrong literal would tell a
/// compliance manager they have 2 critical obligations due when they have 140 - silently, with no
/// error, in an email that goes to roughly 600 tenants. The proc refuses rather than guess, and
/// so must the caller. Send nothing.
/// </summary>
public sealed class FreeDigestDictionaryGapException(int customerId, Exception inner)
    : Exception($"Free digest for tenant {customerId} cannot resolve the Critical RiskType from the dictionary. Refusing to send a digest carrying a critical-risk count.", inner)
{
    public int CustomerId { get; } = customerId;
}

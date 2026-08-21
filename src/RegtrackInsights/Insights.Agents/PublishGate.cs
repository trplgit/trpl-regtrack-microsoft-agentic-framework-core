using Insights.Data;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The deterministic arbiter (design doc 3.5, 11.3) - runs after narrative reflection approves,
/// before anything renders. Two checks, both non-negotiable, neither an LLM call:
///
///   1. Claim-checker - every assertion id a narrative block declares in AssertionIdsUsed must
///      exist in the assertion pool actually supplied for this report. This is a set-membership
///      check, not text parsing - "complete by construction" per Section 6.9, because the
///      narrative agent is contractually required to declare its sources rather than have this
///      gate try to extract claims from free text.
///
///   2. Scope post-flight audit - delegates to IScopeRepository.AuditScopeAsync, which re-queries
///      the database independently rather than re-checking rows already held in memory. This
///      catches a scope-RESOLUTION bug at the source; it is deliberately not a per-row check
///      against the dimension results already fetched for this report, which would only catch a
///      composition-time filtering bug and give a false sense of coverage for the more dangerous
///      class of error.
///
/// A refusal here is fail-closed, not fail-soft: reflection catches judgement errors (wrong
/// emphasis, an overclaiming story), but only this layer catches a hallucinated number or an
/// out-of-scope row, and reflection cannot substitute for it (Section 3.5 - "both are required").
/// </summary>
public sealed class PublishGate(IScopeRepository scopeRepository)
{
    public async Task<PublishGateResult> EvaluateAsync(
        int userId,
        int customerId,
        NarrativeResult narrative,
        IReadOnlyList<Assertion> assertionPool,
        CancellationToken cancellationToken = default)
    {
        var knownAssertionIds = assertionPool.Select(a => a.AssertionId).ToHashSet(StringComparer.Ordinal);
        var diagnostics = new List<string>();

        foreach (var block in narrative.Blocks)
            foreach (var assertionId in block.AssertionIdsUsed)
                if (!knownAssertionIds.Contains(assertionId))
                    diagnostics.Add($"block '{block.Block}' cites assertion id '{assertionId}', which is not in the supplied assertion pool");

        // Fail fast on an unmapped claim - no reason to spend a DB round trip on the scope
        // audit if the report is already refused.
        if (diagnostics.Count > 0)
            return PublishGateResult.Refuse(diagnostics);

        try
        {
            await scopeRepository.AuditScopeAsync(userId, customerId, cancellationToken);
        }
        catch (ScopeAuditFailedException ex)
        {
            return PublishGateResult.Refuse([ex.Message]);
        }

        return PublishGateResult.Approve;
    }
}

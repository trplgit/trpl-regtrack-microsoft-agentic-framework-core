namespace Insights.Domain;

/// <summary>
/// [SURFACES AN EXISTING RULE - 2026-09-11] The "Entity redirects to fixed_holistic" decision
/// itself was already made and implemented in InsightsReportOrchestrator.RunTask (2026-09-09,
/// see that rewrite's own doc comment for the full reasoning: the real Angular product has no
/// per-dimension Entity view at all, so Sambram never built a dimension_selection template for
/// it). This class does NOT introduce a new product decision - it moves the SAME exact-match
/// condition (RequestedDimensions is exactly ["Entity"], nothing combined) one layer earlier.
///
/// [REVERTED 2026-09-11] Briefly widened to "Entity anywhere in the list redirects the WHOLE
/// request, dropping every other requested dimension" - reverted same day: that silently discarded
/// the other picks, which was never the intent. The real ask (still to be designed) is a combined
/// document where Entity's own section renders in the fixed_holistic 6-tab style and every other
/// requested dimension keeps its normal dimension_selection section, in ONE output - a bigger
/// change than this router can express (it only chooses one ReportType for the whole run). Back to
/// Entity-ALONE-only until that design lands.
///
/// Why earlier matters: the orchestrator's own rewrite happens inside RunTask, AFTER
/// RunEndpoints.cs (or InsightsRunOnceWorker's CLI) has already computed the cooldown key and
/// the deterministic run id from the ORIGINAL (dimension_selection, ["Entity"]) pair. That means
/// a plain-Entity request was consuming a DIFFERENT cooldown slot than a genuine fixed_holistic
/// request, even though the two produce the byte-identical report. Resolving here, before either
/// of those are computed, closes that gap. The orchestrator's own check stays in place
/// unchanged - it is not dead code, it is still the only thing that catches a caller who invokes
/// the orchestrator directly (e.g. a lab test), bypassing both of the real trigger paths below.
///
/// Pure and tiny on purpose - the two places that can start a run (RunEndpoints.cs's
/// POST /api/insights/reports, and InsightsRunOnceWorker's CLI) each call this ONCE, before
/// computing the cooldown/run-id key, so both agree with what the orchestrator will end up doing.
/// </summary>
public static class ReportTypeRouter
{
    /// <summary>
    /// Resolves the caller's requested (reportType, requestedDimensions) to what will actually
    /// run. Matches InsightsReportOrchestrator.RunTask's own condition exactly: only an
    /// Entity-ALONE request redirects. Entity combined with other dimensions (e.g.
    /// ["Entity","Location"]) is NOT this rule's concern - deliberately narrower than "any list
    /// containing Entity" so this stays a single source of truth with the orchestrator's own
    /// check, not a second, divergent product decision.
    /// </summary>
    public static (string ReportType, IReadOnlyList<string>? RequestedDimensions) Resolve(
        string reportType, IReadOnlyList<string>? requestedDimensions)
    {
        if (reportType == DimensionSelectionComposition.ReportType
            && requestedDimensions is ["Entity"])
        {
            return (FixedHolisticComposition.ReportType, null);
        }

        return (reportType, requestedDimensions);
    }
}

namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-08] A caller-driven report shape: exactly the dimensions the caller named,
/// one pane each, in the order requested - for testing/inspecting how one dimension (or an
/// arbitrary combination) renders and narrates on its own, without forcing it into
/// FixedHolisticComposition's fixed 6-tab hero (which assumes all fourteen dimensions are
/// present and has its own fixed pane meanings - Coverage is always pane 3, etc. - neither of
/// which holds here).
///
/// Zero LLM involvement in structure, same reasoning as FixedHolisticComposition: which
/// dimensions appear and in what order is not a judgement call once the caller has already named
/// them explicitly - there is nothing for a composition agent to decide.
///
/// Each dimension becomes its own composition block, named after the dimension itself (e.g.
/// "Location", "Nature") - NarrativeResult.Blocks/NarrativeBlockResult are already block-name-
/// keyed generically (confirmed by reading NarrateActivity/03_narrative.md: neither hardcodes a
/// known block-name enum apart from the special-cased "composite_score"), so this needs no changes
/// to Narrate at all. FindingIds is left empty for every block, same simplification
/// FixedHolisticComposition.Build() already uses for all six of its own blocks - DimensionResult's
/// per-dimension Findings are typed per-dimension-generic (DimensionResult&lt;TControlTotals,TRow&gt;
/// closes differently per dimension), so precisely attributing finding ids to one block here would
/// need a 14-way type switch for a field nothing downstream currently reads from a CompositionPlan
/// block (the render agent works from the full flattened Assertions/Findings lists instead, exactly
/// as it already does for every fixed_holistic pane).
/// </summary>
public static class DimensionSelectionComposition
{
    /// <summary>
    /// The InsightsReportOrchestrationInput.ReportType value that selects this path. A request
    /// using this ReportType MUST also set RequestedDimensions - see InsightsReportOrchestrator's
    /// own guard, which throws OrchestrationRefusedException rather than silently falling back to
    /// "all fourteen" if a caller forgets it (CLAUDE.md non-negotiable #2 - fail closed, fail loud).
    /// </summary>
    public const string ReportType = "dimension_selection";

    public static CompositionPlan Build(IReadOnlyList<string> dimensions)
    {
        if (dimensions.Count == 0)
            throw new ArgumentException("At least one dimension must be selected for a dimension_selection report.", nameof(dimensions));

        var hero = new CompositionHero(
            dimensions[0],
            $"First requested dimension ('{dimensions[0]}') leads - the caller's own ordering, not a judgement call.");

        var blocks = dimensions
            .Select(d => new CompositionBlockPlan(d, "medium", (IReadOnlyList<string>)[]))
            .ToList();

        return new CompositionPlan(hero, blocks, [], []);
    }
}

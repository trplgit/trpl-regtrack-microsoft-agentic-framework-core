namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-14, EXTENDED 2026-09-14] The dimension_selection dimensions that get a real
/// LLM composition call (ComposeFreehandDimensionActivity) instead of
/// DimensionSelectionComposition.Build's fixed single-block plan - explicit product decision to
/// replace Sambram's fixed single-section template with agent-decided structure/hero/emphasis.
/// Originally four (Act/BacklogAging/Departments/Licence); Location joined the same day once its
/// own real Sambram template was confirmed stable enough to replace. Users and Entity are
/// deliberately NOT here - they keep their existing, unchanged dedicated templates. This is
/// CLAUDE.md non-negotiable #1's documented exception path ("if a future report type needs the
/// LLM to choose block order again, reinstate this rule for that type explicitly") applied to
/// five specific dimensions, not a whole report type. See the v1 release scope memory
/// (freehand-dimensions-v1-scope) for which 7 dimensions ship this release.
/// </summary>
public static class FreehandDimensions
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(["Act", "BacklogAging", "Departments", "Licence", "Location"]);
}

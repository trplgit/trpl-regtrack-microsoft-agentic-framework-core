namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-14] The dimension_selection dimensions that get a real LLM composition call
/// (ComposeFreehandDimensionActivity) instead of DimensionSelectionComposition.Build's fixed
/// single-block plan - explicit product decision to replace Sambram's fixed single-section
/// template for exactly these four with agent-decided structure/hero/emphasis. Location, Users
/// and Entity are deliberately NOT here - they keep their existing, unchanged dedicated
/// templates. This is CLAUDE.md non-negotiable #1's documented exception path ("if a future
/// report type needs the LLM to choose block order again, reinstate this rule for that type
/// explicitly") applied to four specific dimensions, not a whole report type.
/// </summary>
public static class FreehandDimensions
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(["Act", "BacklogAging", "Departments", "Licence"]);
}

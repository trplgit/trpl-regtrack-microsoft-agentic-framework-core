namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-14, EXTENDED 2026-09-14, EXTENDED 2026-09-22 x2, EXTENDED 2026-09-23] The
/// dimension_selection dimensions that get a real LLM composition call
/// (ComposeFreehandDimensionActivity) instead of DimensionSelectionComposition.Build's fixed
/// single-block plan - explicit product decision to replace a fixed template with agent-decided
/// structure/hero/emphasis. Originally four (Act/BacklogAging/Departments/Licence); Location joined
/// the same day once its own real Sambram template was confirmed stable enough to replace. Risk,
/// Nature, Internal, then Event joined 2026-09-22. Users joined 2026-09-23 - lab-tested first
/// (UsersFullFreehandLabTest, deleted after review), real Minda output showed the composition agent
/// genuinely choosing a different, tenant-specific hero (a 100% sole-reviewer-dependency finding)
/// the fixed template never surfaced - user decision: retire the fixed template entirely, same
/// freehand pattern as every dimension above it (02_composition_freehand_{name}.md,
/// 05_report_html_dimension_selection_{name}.md), not a new mechanism.
///
/// Entity is the only real dimension deliberately NOT here - Entity-alone requests redirect to
/// fixed_holistic before this set is ever consulted (ReportTypeRouter.cs), so membership here would
/// be unreachable for it regardless; that redirect is a separate, still-open product decision
/// (see FixedHolisticComposition's own doc comment), not something this class controls.
/// </summary>
public static class FreehandDimensions
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(["Act", "BacklogAging", "Departments", "Licence", "Location", "Risk", "Nature", "Internal", "Event", "Users"]);
}

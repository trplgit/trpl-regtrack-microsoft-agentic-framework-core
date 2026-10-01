using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

// [ADDED 2026-10-01] EntityCompositeScore/EntityScoreComponents: trailing optional, replay-safe -
// same shape as BuildReasoningTraceInput's own trailing-optional fields. Reuses this ALREADY
// unconditionally-scheduled node instead of adding a new one, so no orchestrator version bump -
// the set and order of context.ScheduleTask calls in RunTask is unchanged, only the input this one
// call already builds gets two more fields. Populated only for fixed_holistic (Entity); null/empty
// for every other ReportType, same narrow-scope-on-purpose pattern Figures already uses for Licence.
// [ADDED 2026-10-01] EntityTileFigures: same trailing-optional, same reasoning - every known
// headline tile/card percentage (Tab1 Snapshot, Tab2 Risk & licences, Tab4 Operations) also gets a
// real fraction formula hover-link, keyed by the tile/card's own real label text. See
// EntityTilePercentageInjector's own doc comment for exactly which tiles this covers this pass.
// [ADDED 2026-10-01] EntityPane2KpiPairFigures/EntityPane4FybarFigures/EntityPane4StackLegendFigures:
// positional (NOT label-keyed) figures for Tab2/Tab4 tiles whose own label text is free prose, not
// a fixed catalog - see EntityTilePercentageInjector.InjectPositional's own doc comment for why
// label matching broke live across two real renders of the same report.
public sealed record InjectNumberFormulaInput(
    string Html, IReadOnlyList<NumberFormulaInjector.Figure> Figures,
    decimal? EntityCompositeScore = null, IReadOnlyList<EntityScoreFormulaInjector.ScoreComponent>? EntityScoreComponents = null,
    IReadOnlyDictionary<string, EntityTilePercentageInjector.FractionFigure>? EntityTileFigures = null,
    IReadOnlyList<EntityTilePercentageInjector.FractionFigure?>? EntityPane2KpiPairFigures = null,
    IReadOnlyList<EntityTilePercentageInjector.FractionFigure?>? EntityPane4FybarFigures = null,
    IReadOnlyList<EntityTilePercentageInjector.FractionFigure?>? EntityPane4StackLegendFigures = null);
public sealed record InjectNumberFormulaOutput(string Html);

/// <summary>
/// Node 8i, runs right after InjectForwardLookCssActivity and before the first Normalize call -
/// same deterministic-injection treatment as every other Inject*Activity in this pipeline. Wraps
/// NumberFormulaInjector.Inject - see that class's doc comment for why this exists as a
/// deterministic post-generation step rather than something the render agent does itself.
/// No-op (returns the input HTML unchanged) when Figures is empty - today that is every
/// ReportType/dimension combination other than dimension_selection:Licence, see
/// InsightsReportOrchestrator's own node 8i comment for how Figures gets built.
///
/// [ADDED 2026-10-01] Also runs EntityScoreFormulaInjector.Inject over the result, wrapping the
/// fixed_holistic Hero's composite score and score-component numbers as their own hover-links -
/// same no-op-by-absence pattern (EntityCompositeScore null for every ReportType other than
/// fixed_holistic).
/// </summary>
public sealed class InjectNumberFormulaActivity : AsyncTaskActivity<InjectNumberFormulaInput, InjectNumberFormulaOutput>
{
    protected override Task<InjectNumberFormulaOutput> ExecuteAsync(TaskContext context, InjectNumberFormulaInput input) => RunAsync(input);

    internal Task<InjectNumberFormulaOutput> RunAsync(InjectNumberFormulaInput input)
    {
        var html = NumberFormulaInjector.Inject(input.Html, input.Figures);
        html = EntityScoreFormulaInjector.Inject(html, input.EntityCompositeScore, input.EntityScoreComponents ?? []);
        html = EntityTilePercentageInjector.Inject(html, input.EntityTileFigures ?? new Dictionary<string, EntityTilePercentageInjector.FractionFigure>());
        if (input.EntityPane2KpiPairFigures is { Count: > 0 } pane2Figures)
            html = EntityTilePercentageInjector.InjectPositional(html, "di-pane-2", EntityTilePercentageInjector.TilePattern.KpiPair, pane2Figures);
        if (input.EntityPane4FybarFigures is { Count: > 0 } pane4Fybars)
            html = EntityTilePercentageInjector.InjectPositional(html, "di-pane-4", EntityTilePercentageInjector.TilePattern.Fybar, pane4Fybars);
        if (input.EntityPane4StackLegendFigures is { Count: > 0 } pane4StackLegend)
            html = EntityTilePercentageInjector.InjectPositional(html, "di-pane-4", EntityTilePercentageInjector.TilePattern.StackLegend, pane4StackLegend);
        return Task.FromResult(new InjectNumberFormulaOutput(html));
    }
}

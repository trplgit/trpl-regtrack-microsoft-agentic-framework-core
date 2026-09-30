using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectNumberFormulaInput(string Html, IReadOnlyList<NumberFormulaInjector.Figure> Figures);
public sealed record InjectNumberFormulaOutput(string Html);

/// <summary>
/// Node 8i, runs right after InjectForwardLookCssActivity and before the first Normalize call -
/// same deterministic-injection treatment as every other Inject*Activity in this pipeline. Wraps
/// NumberFormulaInjector.Inject - see that class's doc comment for why this exists as a
/// deterministic post-generation step rather than something the render agent does itself.
/// No-op (returns the input HTML unchanged) when Figures is empty - today that is every
/// ReportType/dimension combination other than dimension_selection:Licence, see
/// InsightsReportOrchestrator's own node 8i comment for how Figures gets built.
/// </summary>
public sealed class InjectNumberFormulaActivity : AsyncTaskActivity<InjectNumberFormulaInput, InjectNumberFormulaOutput>
{
    protected override Task<InjectNumberFormulaOutput> ExecuteAsync(TaskContext context, InjectNumberFormulaInput input) => RunAsync(input);

    internal Task<InjectNumberFormulaOutput> RunAsync(InjectNumberFormulaInput input) =>
        Task.FromResult(new InjectNumberFormulaOutput(NumberFormulaInjector.Inject(input.Html, input.Figures)));
}

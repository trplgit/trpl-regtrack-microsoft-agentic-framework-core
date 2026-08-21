using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record RenderHtmlInput(CompositionPlan Plan, NarrativeResult Narrative, string TenantName, string ReportType, DateTime GeneratedAt);
public sealed record RenderHtmlOutput(string Html);

/// <summary>Node 7, runs AFTER the publish gate (spec 3) - no point rendering a refused narrative.</summary>
public sealed class RenderHtmlActivity(IReportHtmlAgent htmlAgent) : AsyncTaskActivity<RenderHtmlInput, RenderHtmlOutput>
{
    protected override Task<RenderHtmlOutput> ExecuteAsync(TaskContext context, RenderHtmlInput input) => RunAsync(input);

    internal async Task<RenderHtmlOutput> RunAsync(RenderHtmlInput input)
    {
        var html = await htmlAgent.RenderAsync(input.Plan, input.Narrative, input.TenantName, input.ReportType, input.GeneratedAt, CancellationToken.None);
        return new RenderHtmlOutput(html);
    }
}

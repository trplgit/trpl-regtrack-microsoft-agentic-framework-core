using System.Text.Json;
using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeInput(
    IReadOnlyDictionary<string, string> DimensionResults,
    string TenantShape,
    string ReportType,
    CompositionPlan? PreviousPlan,
    IReadOnlyList<CompositionReflectionIssue>? Issues);

public sealed record ComposeOutput(CompositionPlan Plan);

/// <summary>
/// Node 5. Idempotency (CLAUDE.md 6, spec 7): keyed by (run_id, node_id) is DTFx's job, not this
/// activity's - DTFx does not re-run a COMPLETED activity on replay, only one that crashed
/// mid-execution before recording completion.
/// </summary>
public sealed class ComposeActivity(ICompositionAgent compositionAgent) : AsyncTaskActivity<ComposeInput, ComposeOutput>
{
    protected override Task<ComposeOutput> ExecuteAsync(TaskContext context, ComposeInput input) => RunAsync(input);

    internal async Task<ComposeOutput> RunAsync(ComposeInput input)
    {
        // Parsed back to JsonElement here, locally - input.DimensionResults crossed the DTFx
        // activity boundary as plain strings (see FetchDimensionsActivity's doc comment for why),
        // never as JsonElement itself. CompositionAgent only re-serializes whatever it is handed,
        // so a JsonElement value serializes identically to the original DimensionResult.
        var dimensionResults = input.DimensionResults.ToDictionary(
            kv => kv.Key, kv => (object)JsonSerializer.Deserialize<JsonElement>(kv.Value));
        (CompositionPlan, IReadOnlyList<CompositionReflectionIssue>)? revision =
            input.PreviousPlan is not null && input.Issues is not null ? (input.PreviousPlan, input.Issues) : null;

        var plan = await compositionAgent.ComposeAsync(dimensionResults, input.TenantShape, input.ReportType, revision, CancellationToken.None);
        return new ComposeOutput(plan);
    }
}

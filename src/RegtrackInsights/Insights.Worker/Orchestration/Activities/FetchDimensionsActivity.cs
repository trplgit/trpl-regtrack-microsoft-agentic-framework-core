using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record FetchDimensionsInput(int UserId, int CustomerId);

// Trailing default, not required - every existing construction site (tests, manual runs) predates
// design doc Sec.11.4 and already meant "nothing failed" implicitly. Matches
// InsightsReportOrchestrationInput.Priority's same reasoning elsewhere in this codebase.
public sealed record FetchDimensionsOutput(
    IReadOnlyDictionary<string, string> DimensionResults,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> FailedDimensions)
{
    public FetchDimensionsOutput(
        IReadOnlyDictionary<string, string> dimensionResults, IReadOnlyList<Assertion> assertions, IReadOnlyList<Finding> findings)
        : this(dimensionResults, assertions, findings, Array.Empty<string>())
    {
    }
}

/// <summary>
/// Nodes 3-4: the nine dimension calls, each of which already returns its own Assertions/Findings
/// (already reconciled, already validated - see DimensionResult.Validate, called automatically by
/// SqlDimensionRepository). This is "validating" in API_CONTRACTS.md's stage vocabulary because
/// the reconciliation THROWs happen inside these calls, not as a separate step.
///
/// Each DimensionResult&lt;TControlTotals,TRow&gt; is serialized to a JSON string, not a
/// System.Text.Json.JsonElement as first tried. A generic type closed differently per dimension
/// does not survive Durable Task's own round-trip as `object` (no discriminator for the
/// deserializer to pick the right closed type back up) - JsonElement was meant to fix that, but
/// classic DTFx's default DataConverter wraps Newtonsoft.Json, which cannot reconstruct an
/// STJ-specific JsonElement struct. Confirmed live: a real run against tenant 29 failed at the
/// composing stage with "Operation is not valid due to the current state of the object" - exactly
/// where DimensionResults first crosses an activity boundary. A plain string round-trips through
/// any serializer; ComposeActivity parses it back to a JsonElement locally, after DTFx's own
/// deserialization has already happened, never inside the cross-boundary payload itself.
///
/// PARTIAL GENERATION (design doc Sec.11.4): each of the nine calls is wrapped individually via
/// <see cref="TryFetchAsync{TControlTotals,TRow}"/>. DimensionReconciliationException and
/// DimensionContractViolationException are caught - both are a bug local to ONE dimension's own
/// procedure (see IDimensionRepository's doc comment) - and that dimension is recorded in
/// FailedDimensions instead of appearing in DimensionResults/Assertions/Findings at all: its data
/// never reaches anything downstream in any form, successful or degraded. Composition/narrative
/// therefore cannot reference a failed dimension even accidentally, since they are simply never
/// given it - PartialDimensionPlaceholder (Insights.Presentation), not any agent, is what states a
/// dimension failed, deterministically, later in the pipeline. DimensionScopeDeniedException and
/// DimensionDictionaryGapException are NOT caught here - both propagate and fail the whole run,
/// per their own doc comments.
/// </summary>
public sealed class FetchDimensionsActivity(IDimensionRepository dimensionRepository, IDimensionFailureRecorder? failureRecorder = null)
    : AsyncTaskActivity<FetchDimensionsInput, FetchDimensionsOutput>
{
    private readonly IDimensionFailureRecorder failureRecorder = failureRecorder ?? IDimensionFailureRecorder.Null;

    protected override Task<FetchDimensionsOutput> ExecuteAsync(TaskContext context, FetchDimensionsInput input) => RunAsync(input);

    internal async Task<FetchDimensionsOutput> RunAsync(FetchDimensionsInput input)
    {
        var dimensionResults = new Dictionary<string, string>();
        var assertions = new List<Assertion>();
        var findings = new List<Finding>();
        var failedDimensions = new List<string>();

        async Task TryFetchAsync<TControlTotals, TRow>(string name, Func<Task<DimensionResult<TControlTotals, TRow>>> fetch)
        {
            try
            {
                var result = await fetch();
                dimensionResults[name] = JsonSerializer.Serialize(result);
                assertions.AddRange(result.Assertions);
                findings.AddRange(result.Findings);
            }
            catch (Exception ex) when (ex is DimensionReconciliationException or DimensionContractViolationException)
            {
                failedDimensions.Add(name);
                failureRecorder.RecordBlockFailure(name);
            }
        }

        await TryFetchAsync("Location", () => dimensionRepository.GetLocationAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Entity", () => dimensionRepository.GetEntityAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Risk", () => dimensionRepository.GetRiskAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Nature", () => dimensionRepository.GetNatureAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Departments", () => dimensionRepository.GetDepartmentsAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Act", () => dimensionRepository.GetActAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Users", () => dimensionRepository.GetUsersAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Internal", () => dimensionRepository.GetInternalAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Event", () => dimensionRepository.GetEventAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));

        // All nine failing is not "partial" - there is nothing left to compose or narrate from,
        // and a report that is nothing but nine placeholders is not the "correct, individually
        // gate-passed numbers still reach the user" outcome Sec.11.4 describes. Fail loudly rather
        // than let this silently become a real-looking but content-free report.
        if (dimensionResults.Count == 0)
        {
            throw new InvalidOperationException(
                $"All {failedDimensions.Count} dimensions failed for tenant {input.CustomerId} - nothing to report. Refusing the whole run rather than publishing an all-placeholder report.");
        }

        return new FetchDimensionsOutput(dimensionResults, assertions, findings, failedDimensions);
    }
}

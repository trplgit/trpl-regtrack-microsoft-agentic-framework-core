using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

/// <summary>
/// RequestedDimensions [ADDED 2026-09-08] - null/empty fetches all fourteen, unchanged from before
/// this field existed. Non-empty restricts to exactly the named dimensions (case-sensitive, must
/// match the literal names TryFetchAsync below uses - "Location", "Entity", ... - same closed set
/// DimensionFailureMetrics's own doc comment already documents). Powers the dimension-selection
/// report type (DimensionSelectionComposition) and any future ad-hoc single/multi-dimension
/// inspection tooling - one filter, not a second fetch path.
/// </summary>
public sealed record FetchDimensionsInput(int UserId, int CustomerId, IReadOnlyList<string>? RequestedDimensions = null);

// Trailing default, not required - every existing construction site (tests, manual runs) predates
// design doc Sec.11.4 and already meant "nothing failed" implicitly. Matches
// InsightsReportOrchestrationInput.Priority's same reasoning elsewhere in this codebase.
//
// [FIX - found live] Two public constructors with neither marked [JsonConstructor] left classic
// DTFx's Newtonsoft.Json-based DataConverter unable to pick one when this type crossed the
// activity boundary: "Unable to find a constructor to use for type FetchDimensionsOutput."
// Confirmed live - the very first real run to reach a genuine dimension failure (Location, via
// SqlDimensionRepository's new catch-all) was also the first to actually exercise this
// deserialization path; every earlier attempt died before FetchDimensionsActivity ever returned.
[method: Newtonsoft.Json.JsonConstructor]
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
/// Nodes 3-4: the fourteen dimension calls, each of which already returns its own Assertions/Findings
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
/// PARTIAL GENERATION (design doc Sec.11.4): each of the ten calls is wrapped individually via
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
        var requested = input.RequestedDimensions;

        async Task TryFetchAsync<TControlTotals, TRow>(string name, Func<Task<DimensionResult<TControlTotals, TRow>>> fetch)
        {
            if (requested is { Count: > 0 } && !requested.Contains(name))
                return; // not one of the caller's requested dimensions - skip the SQL call entirely.

            try
            {
                var result = await fetch();
                dimensionResults[name] = JsonSerializer.Serialize(result);
                assertions.AddRange(result.Assertions);
                findings.AddRange(result.Findings);
            }
            catch (Exception ex) when (ex is DimensionReconciliationException or DimensionContractViolationException)
            {
                // [TEMP DIAGNOSTIC 2026-09-07] This catch previously had NO visible logging at all -
                // a dimension degrading to a placeholder was only observable via the
                // insights.dimension.block_failures_total OTel counter, which nothing in this
                // environment currently exports/reads. Added while diagnosing why tenant 29's
                // Coverage pane had no locationRows to inject - remove once confirmed whether Location
                // is actually failing here and, if so, why.
                Console.Error.WriteLine($"[DIAG] dimension '{name}' degraded to placeholder: {ex.GetType().Name}: {ex.Message}");
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
        await TryFetchAsync("Licence", () => dimensionRepository.GetLicenceAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("BacklogAging", () => dimensionRepository.GetBacklogAgingAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("TimelinessFY", () => dimensionRepository.GetTimelinessFYAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("ForwardPipeline", () => dimensionRepository.GetForwardPipelineAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("EvidenceIntegrity", () => dimensionRepository.GetEvidenceIntegrityAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));

        // All fourteen failing is not "partial" - there is nothing left to compose or narrate from,
        // and a report that is nothing but fourteen placeholders is not the "correct, individually
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

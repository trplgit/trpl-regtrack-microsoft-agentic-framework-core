using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

/// <summary>
/// RequestedDimensions [ADDED 2026-09-08] - null/empty fetches all fifteen, unchanged from before
/// this field existed. Non-empty restricts to exactly the named dimensions (case-sensitive, must
/// match the literal names TryFetchAsync below uses - "Location", "Entity", ... - same closed set
/// DimensionFailureMetrics's own doc comment already documents). Powers the dimension-selection
/// report type (DimensionSelectionComposition) and any future ad-hoc single/multi-dimension
/// inspection tooling - one filter, not a second fetch path.
/// </summary>
/// <param name="WindowStart">Start of the period-picker window for the two windowed dimensions
/// (TimelinessFY, EvidenceIntegrity). Both WindowStart and WindowEnd must be supplied together or
/// both left null; null/null means "current financial year to date" (see RunAsync). The picked
/// dropdown option is resolved to a concrete pair by ReportPeriodResolver upstream.</param>
public sealed record FetchDimensionsInput(
    int UserId, int CustomerId, IReadOnlyList<string>? RequestedDimensions = null,
    DateTime? WindowStart = null, DateTime? WindowEnd = null);

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
/// Nodes 3-4: the fifteen dimension calls, each of which already returns its own Assertions/Findings
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
public sealed class FetchDimensionsActivity(
    IDimensionRepository dimensionRepository, ILogger<FetchDimensionsActivity> logger, IDimensionFailureRecorder? failureRecorder = null)
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

        // The two windowed dimensions (TimelinessFY, EvidenceIntegrity) now REQUIRE a concrete
        // [start, end) window - their deployed procs THROW on NULL. Until the period-picker UI is
        // wired, or when the caller passes nothing, default to CURRENT FINANCIAL YEAR TO DATE
        // (1 April of the current FY -> now). That keeps today's numbers: the proc's own
        // year-over-year comparator then becomes "the same span one year earlier", i.e. the
        // matching slice of the previous FY - equivalent to the old current-FY-vs-previous-FY view
        // when the window is a full FY, and still meaningful mid-year.
        var now = DateTime.UtcNow;
        var (windowStart, windowEnd) = input is { WindowStart: { } ws, WindowEnd: { } we }
            ? (ws, we)
            : (new DateTime(ReportPeriodResolver.CurrentFyStartYear(now), 4, 1, 0, 0, 0, DateTimeKind.Utc), now);

        bool IsRequested(string name) => requested is not { Count: > 0 } || requested.Contains(name);

        async Task TryFetchAsync<TControlTotals, TRow>(string name, Func<Task<DimensionResult<TControlTotals, TRow>>> fetch)
        {
            if (!IsRequested(name))
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
                // [FIX - 2026-09-17] Previously silent beyond the insights.dimension.block_failures_total
                // OTel counter (failureRecorder below, still unexported in this environment) - the real
                // exception (e.g. the sql/05 Location truncation found live tonight) never reached
                // anywhere queryable. Now logged here, with the tenant/dimension, before the exception
                // itself is discarded.
                logger.LogError(ex, "Dimension {Dimension} failed for tenant {CustomerId} - degrading to placeholder.", name, input.CustomerId);
                failedDimensions.Add(name);
                failureRecorder.RecordBlockFailure(name);
            }
        }

        await TryFetchAsync("Location", () => dimensionRepository.GetLocationAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Entity", () => dimensionRepository.GetEntityAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Risk", () => dimensionRepository.GetRiskAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Nature", () => dimensionRepository.GetNatureAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("Departments", () => dimensionRepository.GetDepartmentsAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        // [ADDED 2026-09-25] Act now requires a real caller-supplied window - no FY-to-date
        // fallback, that convention stays specific to TimelinessFY/EvidenceIntegrity (product
        // decision 2026-09-25). If the caller requested Act without a resolvable period, fail
        // this ONE dimension loudly (the same partial-generation path every other dimension
        // failure already uses) rather than fabricating a window or crashing the whole run.
        if (IsRequested("Act"))
        {
            if (input is { WindowStart: { } actWs, WindowEnd: { } actWe })
                await TryFetchAsync("Act", () => dimensionRepository.GetActAsync(input.UserId, input.CustomerId, actWs, actWe, cancellationToken: CancellationToken.None));
            else
            {
                logger.LogWarning("Act requested for tenant {CustomerId} without a resolvable period window - failing this dimension rather than fabricating one.", input.CustomerId);
                failedDimensions.Add("Act");
            }
        }
        // [ADDED 2026-09-15] Patches PerformerUserCount/ReviewerUserCount onto ControlTotals right
        // after the repository call returns - see UsersHeadcountCalculator's own doc comment. Pure
        // C# aggregation over the already-fetched, already-reconciled Rows; no new SQL, no change
        // to sql/12_dimension_users.sql.
        await TryFetchAsync("Users", async () =>
        {
            var result = await dimensionRepository.GetUsersAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
            var (performerUserCount, reviewerUserCount) = UsersHeadcountCalculator.Compute(result.Rows);
            return new DimensionResult<UsersControlTotals, UsersRow>(
                result.Dimension,
                result.ControlTotals with { PerformerUserCount = performerUserCount, ReviewerUserCount = reviewerUserCount },
                result.Rows, result.Detectors, result.Assertions, result.Findings, result.DataQuality);
        });
        await TryFetchAsync("Internal", () => dimensionRepository.GetInternalAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        // [ADDED 2026-09-25] Same real-window-required rule as Act above.
        if (IsRequested("Event"))
        {
            if (input is { WindowStart: { } evWs, WindowEnd: { } evWe })
                await TryFetchAsync("Event", () => dimensionRepository.GetEventAsync(input.UserId, input.CustomerId, evWs, evWe, cancellationToken: CancellationToken.None));
            else
            {
                logger.LogWarning("Event requested for tenant {CustomerId} without a resolvable period window - failing this dimension rather than fabricating one.", input.CustomerId);
                failedDimensions.Add("Event");
            }
        }
        await TryFetchAsync("Licence", () => dimensionRepository.GetLicenceAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("BacklogAging", () => dimensionRepository.GetBacklogAgingAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("TimelinessFY", () => dimensionRepository.GetTimelinessFYAsync(input.UserId, input.CustomerId, windowStart, windowEnd, cancellationToken: CancellationToken.None));
        await TryFetchAsync("ForwardPipeline", () => dimensionRepository.GetForwardPipelineAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
        await TryFetchAsync("EvidenceIntegrity", () => dimensionRepository.GetEvidenceIntegrityAsync(input.UserId, input.CustomerId, windowStart, windowEnd, cancellationToken: CancellationToken.None));
        await TryFetchAsync("ForwardRisk", () => dimensionRepository.GetForwardRiskAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));

        // All fifteen failing is not "partial" - there is nothing left to compose or narrate from,
        // and a report that is nothing but fifteen placeholders is not the "correct, individually
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

using System.Text.Json;
using DurableTask.Core;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComputeScoreInput(IReadOnlyDictionary<string, string> DimensionResults);

/// <summary>
/// <see cref="ScoreDimensionResultJson"/> is OverallHealth serialized, meant to be merged into the
/// SAME DimensionResults dictionary under key "Score" - NOT just carried in Assertions.
/// [BUG FOUND LIVE] ComposeActivity's actual LLM input is exclusively `input.DimensionResults`
/// (see its own doc comment) - the aggregated Assertions/Findings lists are only ever read by
/// ReflectOnComposition/Narrate, never by the initial Compose call. Confirmed live: with the score
/// only merged into Assertions, the composition agent explicitly recorded "No A-SCORE-* assertions
/// were provided in this run" and omitted the composite_score block - correct behavior given what
/// it was actually shown, wrong premise. Merging this into DimensionResults too is what actually
/// gets it in front of the composition step.
/// </summary>
public sealed record ComputeScoreOutput(OverallHealth OverallHealth, IReadOnlyList<Assertion> Assertions, string ScoreDimensionResultJson);

/// <summary>
/// Node 4a, runs right after FetchDimensionsActivity and before ComposeActivity. Deterministic -
/// see CompositeScoreCalculator's own doc comment for why this must never move into an agent.
/// Its output is folded into the SAME assertions list ComposeActivity/NarrateActivity already
/// receive, as ordinary typed Assertion records - the narrative agent can therefore only CITE a
/// score, exactly like every other dimension's numbers, never invent or restate one. No new
/// plumbing needed on ComposeInput/NarrateInput for this.
///
/// TWO KINDS of assertion, both required: 'A-SCORE-composite' (the overall 0-100 score, with
/// Band/Trend in Caveat) and one 'A-SCORE-{domain_kpi}' per available component. [BUG FOUND LIVE]
/// Shipping only the per-component assertions left the overall composite with NO typed fact
/// behind it anywhere - the narrative correctly never stated one, and the render step then
/// fabricated a number for the hero (rendered "24/100" against a real value of 40) because
/// nothing told it not to. Never ship one without the other again.
///
/// Deserializing DimensionResult&lt;TControlTotals,TRow&gt; directly (rather than the JsonElement
/// round-trip ComposeActivity uses) relies on System.Text.Json's constructor-parameter binding
/// matching the primary constructor's lowerCamelCase parameter names to the serialized PascalCase
/// properties - confirmed working by ComputeScoreActivityTests.
/// DimensionResult_RoundTripsThroughSystemTextJson_WithRealDataShape (no DB needed for that proof).
/// The one thing NOT yet verified against a live DB (no connection available while this was
/// written) is that the real sql/08/05/12/21 procs' actual output matches these shapes exactly -
/// see DimensionRepositoryTests for that pattern once a DB is reachable.
///
/// Missing dimensions (Risk/Location/Users/Licence not present in DimensionResults - degraded by
/// partial generation, Sec.11.4) are passed through as null; CompositeScoreCalculator already
/// handles a null dimension the same way it handles a null sub-metric.
/// </summary>
public sealed class ComputeScoreActivity : AsyncTaskActivity<ComputeScoreInput, ComputeScoreOutput>
{
    protected override Task<ComputeScoreOutput> ExecuteAsync(TaskContext context, ComputeScoreInput input) => Task.FromResult(Run(input));

    internal static ComputeScoreOutput Run(ComputeScoreInput input)
    {
        var risk = Deserialize<DimensionResult<RiskControlTotals, RiskRow>>(input.DimensionResults, "Risk");
        var location = Deserialize<DimensionResult<LocationControlTotals, LocationRow>>(input.DimensionResults, "Location");
        var users = Deserialize<DimensionResult<UsersControlTotals, UsersRow>>(input.DimensionResults, "Users");
        var licence = Deserialize<DimensionResult<LicenceControlTotals, LicenceRow>>(input.DimensionResults, "Licence");

        // [FIX] sql/05_dimension_location.sql now computes a real tenant-wide on-time-closure %
        // (TenantOnTimePct, event-level, lifetime - same dictionary-driven Timeliness classification
        // sql/12 already used per-performer, pooled here at tenant grain). CompositeScoreCalculator
        // was already built to accept and score this; it was only ever passed null because the
        // query didn't exist yet. Null-propagates correctly if Location itself degraded.
        var overallHealth = CompositeScoreCalculator.Compute(risk, location, users?.Rows, licence?.ControlTotals, tenantOnTimePct: location?.ControlTotals.TenantOnTimePct);

        // [BUG FOUND LIVE] Without a typed assertion for the OVERALL composite, only the
        // per-component values were ever citable - the narrative correctly never stated an
        // overall number (nothing backed it), and with no fact to draw from, the render step
        // fabricated one anyway ("24/100 At Risk" shown for a real value of 40) despite never
        // being told to guess. This is the exact "right numbers, lying narrative" failure this
        // whole architecture exists to prevent - caused by a missing fact, not a prompt
        // wording problem. A-SCORE-composite closes it: the composite score/band are now
        // ordinary typed facts like everything else, citable and never invented.
        var assertions = new List<Assertion>
        {
            new(
                AssertionId: "A-SCORE-composite",
                Metric: "composite_score",
                ScopeLabel: "tenant",
                Value: overallHealth.Score!.Value,
                Rank: null,
                OfN: null,
                ComparatorValue: null,
                VsComparatorPP: null,
                Direction: null,
                Caveat: $"Band: {overallHealth.Band}. Trend: {overallHealth.Trend}. PROVISIONAL - see OverallHealth.Method. Not yet reviewed with the business."),
        };
        assertions.AddRange(overallHealth.Components
            .Where(c => c.Score is not null)
            .Select(c => new Assertion(
                AssertionId: $"A-SCORE-{c.DomainKpi}",
                Metric: "component_score",
                ScopeLabel: "tenant",
                Value: c.Score!.Value,
                Rank: null,
                OfN: null,
                ComparatorValue: c.Weight,
                VsComparatorPP: null,
                Direction: null,
                Caveat: "PROVISIONAL - see OverallHealth.Method. Not yet reviewed with the business.")));

        return new ComputeScoreOutput(overallHealth, assertions, JsonSerializer.Serialize(overallHealth));
    }

    private static T? Deserialize<T>(IReadOnlyDictionary<string, string> dimensionResults, string key) =>
        dimensionResults.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
}

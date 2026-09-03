using System.Text.Json;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

/// <summary>
/// ComputeScoreActivity's own doc comment flags one real, previously-unverified risk: whether
/// System.Text.Json can deserialize DimensionResult&lt;TControlTotals,TRow&gt; directly (a
/// primary-constructor type with only get-only properties, no init/setters) via its
/// constructor-parameter binding. ComposeActivity's existing round-trip only proves the JsonElement
/// path works, not this one. These tests prove it (or would have caught it) without needing a DB.
/// </summary>
public sealed class ComputeScoreActivityTests
{
    [Fact]
    public void DimensionResult_RoundTripsThroughSystemTextJson_WithRealDataShape()
    {
        var original = new DimensionResult<RiskControlTotals, RiskRow>(
            "Risk",
            new RiskControlTotals { ScopedInstances = 100, CriticalRiskType = 3, TenantOverduePct = 12.5m },
            [new RiskRow { RiskType = 3, RiskLabel = "Critical", Instances = 50, OverduePct = 40m, Flags = "" }],
            [new DetectorPolicy("worst_risk", 4, 1, 25m, DetectorEmitMode.Individual)],
            [new Assertion("A1", "overdue_pct", "tenant", 12.5m, null, null, null, null, null, null)],
            [new Finding("F1", FindingSeverity.Medium, "headline", ["A1"], null)],
            [new DataQualityNote { Issue = "x", Detail = "y" }]);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<DimensionResult<RiskControlTotals, RiskRow>>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal("Risk", roundTripped!.Dimension);
        Assert.Equal(3, roundTripped.ControlTotals.CriticalRiskType);
        Assert.Equal(12.5m, roundTripped.ControlTotals.TenantOverduePct);
        Assert.Single(roundTripped.Rows);
        Assert.Equal(40m, roundTripped.Rows[0].OverduePct);
        Assert.Single(roundTripped.Assertions);
        Assert.Single(roundTripped.Findings);
    }

    [Fact]
    public void Run_AllFourDimensionsPresent_ProducesAssertionsOnlyForNonNullScores()
    {
        var risk = new DimensionResult<RiskControlTotals, RiskRow>(
            "Risk", new RiskControlTotals { CriticalRiskType = 3 },
            [new RiskRow { RiskType = 3, OverduePct = 30m }], [], [], [], []);
        var location = new DimensionResult<LocationControlTotals, LocationRow>(
            "Location", new LocationControlTotals { BranchesReported = 2, TenantOverduePct = 10m },
            [new LocationRow { BranchID = 1, Flags = "" }, new LocationRow { BranchID = 2, Flags = "" }], [], [], [], []);
        var users = new DimensionResult<UsersControlTotals, UsersRow>(
            "Users", new UsersControlTotals(),
            [new UsersRow { UserID = 1, PerformerInstances = 10, ReviewerInstances = 5 }], [], [], [], []);
        var licence = new DimensionResult<LicenceControlTotals, LicenceRow>(
            "Licence", new LicenceControlTotals { TenantLapsedCorroboratedPct = 15m }, [], [], [], [], []);

        var dimensionResults = new Dictionary<string, string>
        {
            ["Risk"] = JsonSerializer.Serialize(risk),
            ["Location"] = JsonSerializer.Serialize(location),
            ["Users"] = JsonSerializer.Serialize(users),
            ["Licence"] = JsonSerializer.Serialize(licence),
        };

        var result = ComputeScoreActivity.Run(new ComputeScoreInput(dimensionResults));

        // [BUG FOUND LIVE] A-SCORE-composite must ALWAYS be present alongside the components -
        // without it, nothing in the pipeline ever states the overall number, and a render step
        // fabricated one ("24/100" against a real 40) because no fact backed the real value.
        // Evidence and Timeliness are null this pass (no source / no query yet) - never emitted
        // as component assertions. Every other component (Risk/Licence/Coverage/OverdueBacklog/
        // People) plus the composite itself is - 6 total, not 5.
        Assert.Equal(6, result.Assertions.Count);
        Assert.Contains(result.Assertions, a => a.AssertionId == "A-SCORE-composite" && a.Value == result.OverallHealth.Score);
        Assert.DoesNotContain(result.Assertions, a => a.AssertionId == "A-SCORE-evidence_integrity");
        Assert.DoesNotContain(result.Assertions, a => a.AssertionId == "A-SCORE-timeliness");
        Assert.Contains(result.Assertions, a => a.AssertionId == "A-SCORE-licence" && a.Value == 85m);
    }

    /// <summary>
    /// [BUG FOUND LIVE, pinned so it cannot silently regress] Confirmed against a real render:
    /// with only per-component assertions and no composite one, the narrative correctly never
    /// stated an overall number (nothing backed it) - and with nothing to cite, the render step
    /// invented "24/100" for a real composite of 40. The Caveat must also carry Band/Trend, since
    /// that is the only place those two values reach a citable assertion at all.
    /// </summary>
    [Fact]
    public void Run_AlwaysProducesACompositeAssertion_NeverJustComponents()
    {
        var licence = new DimensionResult<LicenceControlTotals, LicenceRow>(
            "Licence", new LicenceControlTotals { TenantLapsedCorroboratedPct = 30m }, [], [], [], [], []);
        var dimensionResults = new Dictionary<string, string> { ["Licence"] = JsonSerializer.Serialize(licence) };

        var result = ComputeScoreActivity.Run(new ComputeScoreInput(dimensionResults));

        var composite = Assert.Single(result.Assertions, a => a.AssertionId == "A-SCORE-composite");
        Assert.Equal(result.OverallHealth.Score, composite.Value);
        Assert.Contains(result.OverallHealth.Band, composite.Caveat);
        Assert.Contains(result.OverallHealth.Trend, composite.Caveat);
    }

    [Fact]
    public void Run_MissingDimensionKey_TreatsItAsNull_DoesNotThrow()
    {
        var licence = new DimensionResult<LicenceControlTotals, LicenceRow>(
            "Licence", new LicenceControlTotals { TenantLapsedCorroboratedPct = 0m }, [], [], [], [], []);
        var dimensionResults = new Dictionary<string, string> { ["Licence"] = JsonSerializer.Serialize(licence) };

        var result = ComputeScoreActivity.Run(new ComputeScoreInput(dimensionResults));

        Assert.NotNull(result.OverallHealth.Score);
        Assert.Equal(2, result.Assertions.Count); // composite + Licence
    }
}

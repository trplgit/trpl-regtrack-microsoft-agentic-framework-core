using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// PROVISIONAL formula - see CompositeScoreCalculator's own doc comment. These tests pin the
/// MECHANICS (null-degrades-gracefully, renormalization, fail-closed on nothing scoreable), not
/// the specific business weights/thresholds, which are expected to change once Vinay/Sambram
/// review them.
/// </summary>
public sealed class CompositeScoreCalculatorTests
{
    private static DimensionResult<RiskControlTotals, RiskRow> RiskWith(int criticalType, decimal criticalOverduePct) =>
        new("Risk",
            new RiskControlTotals { CriticalRiskType = criticalType },
            [new RiskRow { RiskType = criticalType, OverduePct = criticalOverduePct }],
            [], [], [], []);

    private static DimensionResult<LocationControlTotals, LocationRow> LocationWith(int branchesReported, decimal tenantOverduePct, params LocationRow[] rows) =>
        new("Location", new LocationControlTotals { BranchesReported = branchesReported, TenantOverduePct = tenantOverduePct }, rows, [], [], [], []);

    [Fact]
    public void Compute_AllInputsPresent_ProducesNonNullCompositeAndBand()
    {
        var risk = RiskWith(criticalType: 3, criticalOverduePct: 40m);
        var location = LocationWith(4, 20m,
            new LocationRow { BranchID = 1, Flags = "" },
            new LocationRow { BranchID = 2, Flags = "" },
            new LocationRow { BranchID = 3, Flags = "high_ownerless" },
            new LocationRow { BranchID = 4, Flags = "no_obligations_configured" });
        var users = new List<UsersRow>
        {
            new() { UserID = 1, PerformerInstances = 60, ReviewerInstances = 0 },
            new() { UserID = 2, PerformerInstances = 30, ReviewerInstances = 0 },
            new() { UserID = 3, PerformerInstances = 10, ReviewerInstances = 80 },
        };
        var licence = new LicenceControlTotals { TenantLapsedCorroboratedPct = 25m };

        var result = CompositeScoreCalculator.Compute(risk, location, users, licence, tenantOnTimePct: 70m);

        Assert.NotNull(result.Score);
        Assert.InRange(result.Score!.Value, 0, 100);
        Assert.Equal(7, result.Components.Count);
        // Evidence has no source yet - always null in this pass.
        Assert.Null(result.Components.Single(c => c.DomainKpi == "evidence_integrity").Score);
    }

    [Fact]
    public void Compute_MissingDimension_DegradesJustThatComponent_DoesNotThrow()
    {
        // Risk and Location null (as if FetchDimensionsActivity degraded them) - Licence and
        // People still present. Composite must still compute from what remains.
        var licence = new LicenceControlTotals { TenantLapsedCorroboratedPct = 10m };
        var users = new List<UsersRow> { new() { UserID = 1, PerformerInstances = 10, ReviewerInstances = 10 } };

        var result = CompositeScoreCalculator.Compute(risk: null, location: null, users, licence, tenantOnTimePct: null);

        Assert.Null(result.Components.Single(c => c.DomainKpi == "risk_weighted").Score);
        Assert.Null(result.Components.Single(c => c.DomainKpi == "coverage").Score);
        Assert.Null(result.Components.Single(c => c.DomainKpi == "overdue_backlog").Score);
        Assert.NotNull(result.Components.Single(c => c.DomainKpi == "licence").Score);
        Assert.NotNull(result.Score); // composite still computes from Licence + People
    }

    [Fact]
    public void Compute_NothingScoreable_ThrowsRatherThanFabricatingABand()
    {
        var ex = Record.Exception(() =>
            CompositeScoreCalculator.Compute(risk: null, location: null, usersRows: null, licenceTotals: null, tenantOnTimePct: null));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Compute_LicenceScore_IsInverseOfLapsedPct()
    {
        var licence = new LicenceControlTotals { TenantLapsedCorroboratedPct = 30m };
        var result = CompositeScoreCalculator.Compute(risk: null, location: null, usersRows: null, licence, tenantOnTimePct: null);

        Assert.Equal(70m, result.Components.Single(c => c.DomainKpi == "licence").Score);
    }

    [Fact]
    public void Compute_RiskScore_UsesDictionaryResolvedCriticalType_NotAGuess()
    {
        // Two risk rows; the "critical" one is identified ONLY via CriticalRiskType, never by
        // position or by assuming RiskType 3 - here the dictionary resolves Critical to 7.
        var risk = new DimensionResult<RiskControlTotals, RiskRow>(
            "Risk",
            new RiskControlTotals { CriticalRiskType = 7 },
            [
                new RiskRow { RiskType = 3, OverduePct = 90m },  // NOT critical here - must be ignored
                new RiskRow { RiskType = 7, OverduePct = 20m },  // the real critical tier
            ],
            [], [], [], []);
        var licence = new LicenceControlTotals { TenantLapsedCorroboratedPct = 0m };

        var result = CompositeScoreCalculator.Compute(risk, location: null, usersRows: null, licence, tenantOnTimePct: null);

        Assert.Equal(80m, result.Components.Single(c => c.DomainKpi == "risk_weighted").Score);
    }

    [Fact]
    public void Compute_CoverageScore_GivesHalfCreditForHighOwnerless_ZeroForGhost()
    {
        var location = LocationWith(4, 0m,
            new LocationRow { BranchID = 1, Flags = "" },              // healthy
            new LocationRow { BranchID = 2, Flags = "" },              // healthy
            new LocationRow { BranchID = 3, Flags = "high_ownerless" }, // half credit
            new LocationRow { BranchID = 4, Flags = "no_obligations_configured" }); // zero credit
        var licence = new LicenceControlTotals { TenantLapsedCorroboratedPct = 0m };

        var result = CompositeScoreCalculator.Compute(risk: null, location, usersRows: null, licence, tenantOnTimePct: null);

        // (2 healthy + 0.5*1 ownerless) / 4 * 100 = 62.5
        Assert.Equal(62.5m, result.Components.Single(c => c.DomainKpi == "coverage").Score);
    }
}

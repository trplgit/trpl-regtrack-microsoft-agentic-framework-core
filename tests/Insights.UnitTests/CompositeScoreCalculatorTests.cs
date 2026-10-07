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
        var licence = new LicenceControlTotals { TenantExpiredPct = 25m };

        var result = CompositeScoreCalculator.Compute(risk, location, users, licence, tenantOnTimePct: 70m, evidenceReviewTrailPct: null);

        Assert.NotNull(result.Score);
        Assert.InRange(result.Score!.Value, 0, 100);
        Assert.Equal(7, result.Components.Count);
        // No EvidenceIntegrity dimension passed this call - degrades to null like any other missing input.
        Assert.Null(result.Components.Single(c => c.DomainKpi == "evidence_integrity").Score);
    }

    [Fact]
    public void Compute_MissingDimension_DegradesJustThatComponent_DoesNotThrow()
    {
        // Risk and Location null (as if FetchDimensionsActivity degraded them) - Licence and
        // People still present. Composite must still compute from what remains.
        var licence = new LicenceControlTotals { TenantExpiredPct = 10m };
        var users = new List<UsersRow> { new() { UserID = 1, PerformerInstances = 10, ReviewerInstances = 10 } };

        var result = CompositeScoreCalculator.Compute(risk: null, location: null, users, licence, tenantOnTimePct: null, evidenceReviewTrailPct: null);

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
            CompositeScoreCalculator.Compute(risk: null, location: null, usersRows: null, licenceTotals: null, tenantOnTimePct: null, evidenceReviewTrailPct: null));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Compute_LicenceScore_IsInverseOfExpiredPct()
    {
        var licence = new LicenceControlTotals { TenantExpiredPct = 30m };
        var result = CompositeScoreCalculator.Compute(risk: null, location: null, usersRows: null, licence, tenantOnTimePct: null, evidenceReviewTrailPct: null);

        Assert.Equal(70m, result.Components.Single(c => c.DomainKpi == "licence").Score);
    }

    [Fact]
    public void Compute_EvidenceScore_UsesReviewTrailPctDirectly_NoInversion()
    {
        // Unlike Risk/Licence (100 - x), a higher review-trail % is already "better" - no inversion.
        var result = CompositeScoreCalculator.Compute(
            risk: null, location: null, usersRows: null, licenceTotals: null,
            tenantOnTimePct: null, evidenceReviewTrailPct: 16m);

        Assert.Equal(16m, result.Components.Single(c => c.DomainKpi == "evidence_integrity").Score);
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
        var licence = new LicenceControlTotals { TenantExpiredPct = 0m };

        var result = CompositeScoreCalculator.Compute(risk, location: null, usersRows: null, licence, tenantOnTimePct: null, evidenceReviewTrailPct: null);

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
        var licence = new LicenceControlTotals { TenantExpiredPct = 0m };

        var result = CompositeScoreCalculator.Compute(risk: null, location, usersRows: null, licence, tenantOnTimePct: null, evidenceReviewTrailPct: null);

        // (2 healthy + 0.5*1 ownerless) / 4 * 100 = 62.5
        Assert.Equal(62.5m, result.Components.Single(c => c.DomainKpi == "coverage").Score);
    }

    /// <summary>
    /// [ADDED 2026-10-07, BUG FOUND LIVE] Location's own #rows includes BOTH real leaf branches
    /// AND intermediate/grouping tree nodes (state-level entity-hierarchy nodes, not physical
    /// locations) - BranchesReported counts both. An intermediate node can structurally never be
    /// flagged `no_obligations_configured` (that flag requires ActiveChildren = 0; a grouping node
    /// by definition has children), so it always counts as "healthy" regardless of its real
    /// children's state - Coverage was structurally biased toward a higher score the more
    /// intermediate nodes a tenant's entity tree has. Found live: Motul tenant 1926, 25 real leaf
    /// branches (24 healthy, 1 ghost) plus 24 intermediate nodes - old formula scored 98.0
    /// (denominator 49), the real leaf-only answer is 96.0 (denominator 25).
    /// </summary>
    [Fact]
    public void Compute_CoverageScore_ExcludesIntermediateNodesFromDenominator()
    {
        var location = LocationWith(4, 0m,
            new LocationRow { BranchID = 1, Flags = "", NodeType = EntityNodeType.Leaf },                              // healthy leaf
            new LocationRow { BranchID = 2, Flags = "no_obligations_configured", NodeType = EntityNodeType.Leaf },      // ghost leaf
            new LocationRow { BranchID = 3, Flags = "", NodeType = EntityNodeType.Intermediate },                       // grouping node - must not count
            new LocationRow { BranchID = 4, Flags = "", NodeType = EntityNodeType.Intermediate });                      // grouping node - must not count
        var licence = new LicenceControlTotals { TenantExpiredPct = 0m };

        var result = CompositeScoreCalculator.Compute(risk: null, location, usersRows: null, licence, tenantOnTimePct: null, evidenceReviewTrailPct: null);

        // Leaf-only: 1 healthy, 1 ghost, denominator 2 -> (1 + 0) / 2 * 100 = 50.0.
        // The old (wrong) behaviour would have scored (3 healthy + 0) / 4 * 100 = 75.0, since
        // both intermediate rows (Flags = "") would have counted as healthy against a denominator
        // of 4 (BranchesReported, all #rows) instead of 2 (real leaf branches only).
        Assert.Equal(50.0m, result.Components.Single(c => c.DomainKpi == "coverage").Score);
    }
}

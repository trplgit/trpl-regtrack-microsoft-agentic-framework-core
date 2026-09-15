using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-02] An earlier draft re-derived the classification from raw
/// Instances/Ownerless (Ownerless &gt; 0 -> has_ownerless) instead of reading the REAL,
/// already-computed sql/05_dimension_location.sql Flags field - which uses a 10% OwnerlessPct
/// THRESHOLD (`high_ownerless`), not "any ownerless obligation at all". Reading Flags directly
/// means this can never drift from the real detector logic. `under_configured` is never assigned
/// by this classifier - no real per-branch obligation-COUNT peer norm exists anywhere yet (the
/// only real peer field, VsPeerStateNormPP/peer_coverage_gap, is an OVERDUE-RATE peer gap, a
/// different concept) - see CoverageScriptInjector's own note for the full reasoning.
/// </summary>
public sealed class LocationCoverageClassifierTests
{
    [Fact]
    public void Classify_FlagsContainNoObligationsConfigured_ReturnsUnmapped()
    {
        var row = new LocationRow { BranchID = 1, Flags = "single_point_of_failure,no_obligations_configured" };

        Assert.Equal(CoverageStatus.Unmapped, LocationCoverageClassifier.Classify(row));
    }

    [Fact]
    public void Classify_FlagsContainHighOwnerless_ReturnsHasOwnerless()
    {
        var row = new LocationRow { BranchID = 1, Flags = "high_ownerless,peer_coverage_gap" };

        Assert.Equal(CoverageStatus.HasOwnerless, LocationCoverageClassifier.Classify(row));
    }

    [Fact]
    public void Classify_NeitherFlagPresent_ReturnsHealthy()
    {
        var row = new LocationRow { BranchID = 1, Flags = "single_point_of_failure" };

        Assert.Equal(CoverageStatus.Healthy, LocationCoverageClassifier.Classify(row));
    }

    [Fact]
    public void Classify_NullFlags_ReturnsHealthy()
    {
        var row = new LocationRow { BranchID = 1, Flags = null };

        Assert.Equal(CoverageStatus.Healthy, LocationCoverageClassifier.Classify(row));
    }

    /// <summary>no_obligations_configured takes precedence - most severe wins, matches docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4's own "status_precedence... first match wins" rule.</summary>
    [Fact]
    public void Classify_BothFlagsSomehowPresent_UnmappedTakesPrecedence()
    {
        var row = new LocationRow { BranchID = 1, Flags = "high_ownerless,no_obligations_configured" };

        Assert.Equal(CoverageStatus.Unmapped, LocationCoverageClassifier.Classify(row));
    }

    [Fact]
    public void ComputeCounts_TalliesEachRealStatus()
    {
        var rows = new List<LocationRow>
        {
            new() { BranchID = 1, Flags = "" },
            new() { BranchID = 2, Flags = "" },
            new() { BranchID = 3, Flags = "high_ownerless" },
            new() { BranchID = 4, Flags = "no_obligations_configured" },
            new() { BranchID = 5, Flags = "no_obligations_configured" },
        };

        var counts = LocationCoverageClassifier.ComputeCounts(rows);

        Assert.Equal(5, counts.Total);
        Assert.Equal(2, counts.Healthy);
        Assert.Equal(0, counts.UnderConfigured);
        Assert.Equal(1, counts.HasOwnerless);
        Assert.Equal(2, counts.Unmapped);
    }

    [Fact]
    public void ComputeCounts_EmptyList_ReturnsAllZero()
    {
        var counts = LocationCoverageClassifier.ComputeCounts([]);

        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.Healthy);
        Assert.Equal(0, counts.UnderConfigured);
        Assert.Equal(0, counts.HasOwnerless);
        Assert.Equal(0, counts.Unmapped);
    }
}

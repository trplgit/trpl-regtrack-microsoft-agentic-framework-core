using Insights.Domain;
using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class LicenceNumberFormulasTests
{
    private static LicenceRow Row(int active = 0, int expiring = 0, int expired = 0, int applied = 0,
        int pendingForReview = 0, int rejected = 0, int applicationRejected = 0, int terminated = 0,
        int notApplicable = 0, int otherStatus = 0, int endingNext30 = 0) => new()
    {
        LicenseTypeID = 1,
        LicenseTypeName = "Transport",
        ActiveLicences = active,
        Expiring = expiring,
        Expired = expired,
        Applied = applied,
        PendingForReview = pendingForReview,
        Rejected = rejected,
        ApplicationRejected = applicationRejected,
        Terminated = terminated,
        NotApplicable = notApplicable,
        OtherStatus = otherStatus,
        EndingNext30 = endingNext30,
    };

    [Fact]
    public void ActiveAndExpiredCome_FromTheTenantLevelTotals_NotSummedFromRows()
    {
        var totals = new LicenceControlTotals { TenantActiveLicences = 21, TenantExpiredLicences = 3, ScopedLicences = 60, TenantExpiredPct = 5.0m };
        var rows = new List<LicenceRow> { Row(active: 999) }; // deliberately wrong, to prove totals win, not rows

        var figures = LicenceNumberFormulas.Build(totals, rows);

        var active = figures.Single(f => f.Label == "Active licences");
        Assert.Equal("21", active.DisplayValue);
    }

    [Fact]
    public void PerStatusFields_AreSummedAcrossAllRows()
    {
        var totals = new LicenceControlTotals();
        var rows = new List<LicenceRow> { Row(applied: 2), Row(applied: 5) };

        var figures = LicenceNumberFormulas.Build(totals, rows);

        Assert.Equal("7", figures.Single(f => f.Label == "Applied").DisplayValue);
    }

    [Fact]
    public void ExpiredPercentage_IsFormattedToOneDecimalWithAPercentSign()
    {
        var totals = new LicenceControlTotals { TenantExpiredLicences = 3, ScopedLicences = 60, TenantExpiredPct = 5.0m };

        var figures = LicenceNumberFormulas.Build(totals, []);

        Assert.Equal("5.0%", figures.Single(f => f.Label == "Expired percentage").DisplayValue);
    }

    [Fact]
    public void ExpiredPercentagePanelText_UsesTheRealNumbersInItsWorkedExample()
    {
        var totals = new LicenceControlTotals { TenantExpiredLicences = 3, ScopedLicences = 60, TenantExpiredPct = 5.0m };

        var figures = LicenceNumberFormulas.Build(totals, []);

        var pct = figures.Single(f => f.Label == "Expired percentage");
        Assert.Contains("3 Expired out of 60 counted", pct.PanelText);
        Assert.Contains("(3 / 60) x 100 = 5.0%", pct.PanelText);
    }

    [Fact]
    public void EveryFieldGetsItsOwnDistinctNonEmptyPanelText()
    {
        var figures = LicenceNumberFormulas.Build(new LicenceControlTotals(), []);

        Assert.All(figures, f => Assert.False(string.IsNullOrWhiteSpace(f.PanelText)));
        Assert.Equal(figures.Count, figures.Select(f => f.PanelText).Distinct().Count());
    }

    [Fact]
    public void WithNoRealRows_StillProducesAllFourteenFields_WithZeroesForTheSummedOnes()
    {
        var figures = LicenceNumberFormulas.Build(new LicenceControlTotals(), []);

        Assert.Equal(14, figures.Count);
        Assert.Equal("0", figures.Single(f => f.Label == "Applied").DisplayValue);
    }
}

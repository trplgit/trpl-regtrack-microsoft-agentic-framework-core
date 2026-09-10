using Insights.Domain;
using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

public class BacklogAgeBarInjectorTests
{
    private const string DocWithPlaceholder =
        "<article><div class=\"di-kpi__big\"><div class=\"di-kpi__num\">22,070</div></div><div id=\"di-agebar-root\"></div></article>";

    private static readonly BacklogAgingControlTotals RealControlTotals = new()
    {
        CurrentFyLabel = "FY2026-27",
        PreviousFyLabel = "FY2025-26",
        SumOfRows = 21_955,
    };

    // Real sql/22 result-set order is newest-first - deliberately supplied that way here too, so
    // a passing test proves this class re-sorts rather than merely preserving input order.
    private static readonly IReadOnlyList<BacklogAgingRow> RealRows =
    [
        new() { Bucket = "current_fy", FYLabel = "FY2026-27", OverdueCount = 33 },
        new() { Bucket = "previous_fy", FYLabel = "FY2025-26", OverdueCount = 1_740 },
        new() { Bucket = "older", FYLabel = null, OverdueCount = 20_180 },
    ];

    [Fact]
    public void Inject_NoPlaceholder_ReturnsHtmlUnchanged()
    {
        const string html = "<article>no placeholder here</article>";

        var result = BacklogAgeBarInjector.Inject(html, RealRows, RealControlTotals);

        Assert.Equal(html, result);
    }

    [Fact]
    public void Inject_NullRows_RemovesPlaceholderWithoutFabricatingABar()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, null, RealControlTotals);

        Assert.DoesNotContain("di-agebar-root", result);
        Assert.DoesNotContain("di-agebar", result);
    }

    [Fact]
    public void Inject_ZeroOverdueControlTotal_RemovesPlaceholderWithoutFabricatingABar()
    {
        var zeroOverdue = RealControlTotals with { SumOfRows = 0 };

        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, zeroOverdue);

        Assert.DoesNotContain("di-agebar-root", result);
        Assert.DoesNotContain("di-agebar", result);
    }

    [Fact]
    public void Inject_RealBuckets_OrdersOldestFirst_RegardlessOfInputOrder()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, RealControlTotals);

        var olderIndex = result.IndexOf("pre-FY2025-26", StringComparison.Ordinal);
        var previousIndex = result.IndexOf("1,740", StringComparison.Ordinal);
        var barStart = result.IndexOf("di-agebar", StringComparison.Ordinal);
        var currentIndex = result.IndexOf("FY2026-27", barStart, StringComparison.Ordinal);

        Assert.True(olderIndex >= 0 && previousIndex >= 0 && currentIndex >= 0);
        Assert.True(olderIndex < previousIndex, "older bucket must render before previous_fy");
        Assert.True(previousIndex < currentIndex, "previous_fy bucket must render before current_fy");
    }

    [Fact]
    public void Inject_RealBuckets_AssignsToneByAgeNotByMagnitude()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, RealControlTotals);

        Assert.Contains("di-agebar__seg--bad\" style=\"flex:20180\"", result);
        Assert.Contains("di-agebar__seg--warn\" style=\"flex:1740\"", result);
        Assert.Contains("di-agebar__seg--neu\" style=\"flex:33\"", result);
    }

    [Fact]
    public void Inject_OlderBucketHasNoRealFYLabel_UsesPrefixedPreviousFyLabelInstead()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, RealControlTotals);

        Assert.Contains("20,180 · pre-FY2025-26", result);
    }

    [Fact]
    public void Inject_RealBuckets_TotalStaysAboveThePlaceholder_NeverDuplicatedInsideTheBar()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, RealControlTotals);

        Assert.Contains("22,070", result); // the pre-existing di-kpi__big total, untouched
        Assert.DoesNotContain("21,955", result); // the bar itself never repeats the control-total sum as its own number
    }

    /// <summary>
    /// [CHANGED 2026-09-10, tenant rule] Bar segments carry NO in-segment label - only a hover
    /// tooltip - and the names/counts live in a di-stacklegend beneath, not a bare
    /// di-agebar__axis. White text on the neutral-grey segment was an illegible duplicate of the
    /// legend.
    /// </summary>
    [Fact]
    public void Inject_RealBuckets_NoInSegmentLabel_UsesTooltipPlusStacklegend()
    {
        var result = BacklogAgeBarInjector.Inject(DocWithPlaceholder, RealRows, RealControlTotals);

        Assert.DoesNotContain("di-agebar__seglabel", result);
        Assert.DoesNotContain("di-agebar__axis", result);
        Assert.Contains("di-agebar__seg--minor", result);   // every segment hides its label
        Assert.Contains("di-agebar__tip", result);           // hover tooltip kept
        Assert.Contains("di-stacklegend__item", result);     // names + counts moved to the legend
        Assert.Contains("<b class=\"tnum\">20,180</b>", result);
    }
}

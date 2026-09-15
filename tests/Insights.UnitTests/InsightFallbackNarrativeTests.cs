using Insights.Agents;
using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The insight JSON, like the free digest email, must never fail to be produced - InsightFallbackNarrative
/// is what ComposeInsightJsonActivity substitutes when the LLM is skipped, over budget, or rejected.
/// Every number in the output must trace straight to the given InsightFocus - nothing invented.
/// </summary>
public sealed class InsightFallbackNarrativeTests
{
    [Fact]
    public void ImprisonmentFocus_FramesAConcreteBeforeAndAfter()
    {
        var focus = new InsightFocus("High impact", nameof(FreeDigestAggregates.ImprisonmentDueNext7), 1, 24);

        var (headline, explanation) = InsightFallbackNarrative.Build(focus);

        Assert.Contains("1", headline);
        Assert.Contains("24", headline);
        // The explanation states what's LEFT once the Value is addressed - Remainder (24 - 1 = 23),
        // computed deterministically, never invented or computed by prose alone.
        Assert.Contains(focus.Remainder.ToString()!, explanation);
        Assert.DoesNotContain("overdue", headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overdue", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletedLast7Focus_NeverPhrasesARate()
    {
        var focus = new InsightFocus("Low impact", nameof(FreeDigestAggregates.CompletedLast7), 12, null);

        var (headline, explanation) = InsightFallbackNarrative.Build(focus);

        Assert.Contains("12", headline);
        Assert.DoesNotContain("%", headline);
        Assert.DoesNotContain("%", explanation);
    }

    [Fact]
    public void DueNext30Focus_StaysForwardLookingNotBackward()
    {
        var focus = new InsightFocus("Low impact", nameof(FreeDigestAggregates.DueNext30), 40, 1893);

        var (headline, explanation) = InsightFallbackNarrative.Build(focus);

        Assert.Contains("40", headline);
        Assert.Contains("1893", headline);
        Assert.Contains("next 30 days", headline);
        Assert.DoesNotContain("overdue", headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overdue", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownMetric_FallsBackToCompletedLast7Clause()
    {
        // A metric name InsightFocus never actually produces still hits the switch's default arm
        // rather than throwing - the fallback must never be the thing that blocks the run.
        var focus = new InsightFocus("Low impact", "SomeFutureMetric", 3, null);

        var (headline, _) = InsightFallbackNarrative.Build(focus);

        Assert.Contains("completions", headline, StringComparison.OrdinalIgnoreCase);
    }
}

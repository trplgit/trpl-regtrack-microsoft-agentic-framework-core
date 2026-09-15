using Insights.Domain;
using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [WIRED 2026-09-10, WIDENED same day] ForwardRisk (sql/26) + ForwardPipeline (sql/24) are
/// deployed and live in prod. Their segment counts (carried / at-risk / clear) and 5 day-window
/// bucket counts are control totals, not typed assertions, so they cannot travel through the
/// render agent's assertion-only payload. The render agent also kept dropping the whole forward
/// pane on real tenants - so the ENTIRE pane body (`.di-kpi--fwd` card, due figure, segment bar,
/// bucket chart) is injected deterministically now; the agent authors only the section shell +
/// `<div id="di-forward-root"></div>`.
/// </summary>
public sealed class ForwardLookInjectorTests
{
    private const string DocWithPlaceholder =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<section class=\"di-pane\" id=\"di-pane-5\">" +
        "<div class=\"di-pane__head\"><span class=\"di-secnum\">05</span><h2 class=\"di-pane__title\">Key indicators</h2></div>" +
        "<div id=\"di-forward-root\"></div>" +
        "</section></body></html>";

    private static ForwardRiskControlTotals Risk(int due, int carried, int clean, int healthy, int jail = 0, bool empty = false) =>
        new()
        {
            DueInWindow = due, CarriedForward = carried, CleanAtRisk = clean, Healthy = healthy,
            ImprisonmentNeedingAttention = jail, ForwardWindowEmpty = empty, Reconciled = true,
        };

    private static IReadOnlyList<ForwardPipelineRow> Windows(params int[] counts)
    {
        string[] labels = ["0-7d", "8-14d", "15-30d", "31-60d", "61-90d"];
        return counts.Select((c, i) => new ForwardPipelineRow
        {
            WindowLabel = labels[i], MinDaysOut = i, MaxDaysOut = i + 1, DueCount = c,
        }).ToList();
    }

    [Fact]
    public void Inject_RealSegments_RendersCardHeadFigureAndTheAlreadyOverdueShare()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, Risk(due: 74561, carried: 22132, clean: 2179, healthy: 50250));

        Assert.Contains("di-kpi di-kpi--span12 di-kpi--fwd", result, StringComparison.Ordinal);
        Assert.Contains("Forward pipeline", result, StringComparison.Ordinal);
        Assert.Contains("Due distribution (next 90 days)", result, StringComparison.Ordinal);
        Assert.Contains("74,561", result, StringComparison.Ordinal);
        Assert.Contains("22,132", result, StringComparison.Ordinal);
        Assert.Contains("50,250", result, StringComparison.Ordinal);
        Assert.Contains("already overdue today", result, StringComparison.Ordinal);
        Assert.Contains("29.7%", result, StringComparison.Ordinal); // 22132 / 74561
        Assert.DoesNotContain("di-forward-root", result, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"margin-top", result, StringComparison.Ordinal); // rhythm comes from CSS
    }

    [Fact]
    public void Inject_CarriedForwardDominates_FillsVerdictTagBad()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, Risk(due: 74561, carried: 60000, clean: 2179, healthy: 12382));

        Assert.Contains("di-kpi__tag--bad", result, StringComparison.Ordinal);
        Assert.Contains("Carried forward", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_ImprisonmentLine_PaintsOnlyTheFigure_NotTheWholeSentence()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, Risk(due: 100, carried: 40, clean: 10, healthy: 50, jail: 397));

        Assert.Contains("<p class=\"di-kpi__narr di-kpi__narr--alert\"><b class=\"tnum\">397</b>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_NoJailRisk_OmitsThatLine()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, Risk(due: 100, carried: 40, clean: 10, healthy: 50, jail: 0));

        Assert.DoesNotContain("imprisonment exposure", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_WithPipelineRows_RendersFiveColumnBucketChart_NoValueLabels()
    {
        var result = ForwardLookInjector.Inject(
            DocWithPlaceholder,
            Risk(due: 2693, carried: 2611, clean: 0, healthy: 82),
            pipelineTotals: new ForwardPipelineControlTotals { DueNext90d = 2693, SumOfRows = 2693 },
            pipelineRows: Windows(146, 47, 2693, 1137, 840));

        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(result, "di-fwd__col").Count);
        Assert.Contains("<span>15-30d</span>", result, StringComparison.Ordinal); // axis label present
        Assert.Contains("obligations are due in the next 90 days", result, StringComparison.Ordinal);
        // bars are empty <i> elements - height only, no value label text inside the chart
        Assert.Matches("<div class=\"di-fwd\"[^>]*>(<div class=\"di-fwd__col\"><i class=\"di-fwd__bar\" style=\"height:[0-9.]+%\"></i></div>){5}</div>", result);
    }

    [Fact]
    public void Inject_PipelineRowsOnly_NoRisk_StillRendersCardAndChart()
    {
        var result = ForwardLookInjector.Inject(
            DocWithPlaceholder, riskTotals: null,
            pipelineTotals: new ForwardPipelineControlTotals { DueNext90d = 900, SumOfRows = 900 },
            pipelineRows: Windows(100, 100, 300, 200, 200));

        Assert.Contains("di-kpi--fwd", result, StringComparison.Ordinal);
        Assert.Contains("900", result, StringComparison.Ordinal);
        Assert.Contains("di-fwd__col", result, StringComparison.Ordinal);
        Assert.DoesNotContain("di-stackbar", result, StringComparison.Ordinal); // no segment bar without risk data
        Assert.DoesNotContain("di-kpi__tag", result, StringComparison.Ordinal); // no verdict tag without risk data
    }

    [Fact]
    public void Inject_ForwardWindowEmptyAndNoBuckets_LeavesHeadOnlyPane()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, Risk(due: 0, carried: 0, clean: 0, healthy: 0, empty: true));

        Assert.DoesNotContain("di-forward-root", result, StringComparison.Ordinal);
        Assert.DoesNotContain("di-kpi--fwd", result, StringComparison.Ordinal);
        Assert.DoesNotContain("di-stackbar", result, StringComparison.Ordinal);
        Assert.DoesNotContain("di-fwd__col", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_NullEverything_LeavesHeadOnlyPane()
    {
        var result = ForwardLookInjector.Inject(DocWithPlaceholder, riskTotals: null);

        Assert.DoesNotContain("di-forward-root", result, StringComparison.Ordinal);
        Assert.DoesNotContain("di-kpi--fwd", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_NoPlaceholder_LeavesDocumentUnchanged()
    {
        const string noPlaceholder = "<!DOCTYPE html><html><body>no forward pane</body></html>";

        var result = ForwardLookInjector.Inject(noPlaceholder, Risk(due: 100, carried: 40, clean: 10, healthy: 50));

        Assert.Equal(noPlaceholder, result);
    }
}

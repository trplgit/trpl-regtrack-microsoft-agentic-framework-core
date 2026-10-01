using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class EntityTilePercentageInjectorTests
{
    // Real markup shapes, extracted verbatim from a real rendered fixed_holistic report
    // (entity-fixed-holistic-adi1285-verify/entity-fixed-holistic.html, 2026-10-01).
    private const string SnaptileHtml = """
        <html><body>
        <article class="di-snaptile di-snaptile--bad">
          <div class="di-snaptile__label">Licence &middot; Expired</div>
          <div class="di-snaptile__num tnum">1.70%</div>
          <div class="di-snaptile__desc">Two of six licence types have an Expired share above this tenant-wide level.</div>
        </article>
        </body></html>
        """;

    // Real shape: Tab2's own pane, TWO pairs inside the SAME card (Risk) - the exact structure
    // that broke label-based matching live (pair-lbl text drifted "Critical overdue" ->
    // "Overdue rate" and "Liability overlap" -> "Personal-liability overlap" between two real
    // renders of the SAME report type).
    private const string KpiPairPaneHtml = """
        <html><body>
        <section id="di-pane-2" aria-label="Risk and licences">
        <div class="di-kpi__pair di-kpi__pair--bad">
          <div class="di-kpi__pair-lbl">Overdue rate</div>
          <div class="di-kpi__pair-val tnum">100.00%</div>
          <div class="di-kpi__pair-sub">48.40 points above the tenant rate.</div>
        </div>
        <div class="di-kpi__pair di-kpi__pair--warn">
          <div class="di-kpi__pair-lbl">Personal-liability overlap</div>
          <div class="di-kpi__pair-val tnum">2.40%</div>
        </div>
        </section>
        <section id="di-pane-3" aria-label="Coverage">
        <div class="di-kpi__pair-val tnum">12</div>
        </section>
        </body></html>
        """;

    // Real shape: Tab4's own pane, a di-fybar card with two bars (current/previous period) -
    // label text ("2026-04-01 to 2026-10-01" / "Previous comparable period" vs "Comparator
    // period" on a second real run) is not stable either.
    private const string FybarPaneHtml = """
        <html><body>
        <section id="di-pane-4" aria-label="Operations">
        <div class="di-fybar__lbl"><span>2026-04-01 to 2026-10-01</span><b class="tnum">58.60%</b></div>
        <div class="di-fybar__lbl"><span>Comparator period</span><b class="tnum">49.20%</b></div>
        </section>
        </body></html>
        """;

    [Fact]
    public void WrapsAKnownSnaptilePercentage_WithARealFraction()
    {
        var figures = new Dictionary<string, EntityTilePercentageInjector.FractionFigure>
        {
            ["Licence · Expired"] = new("Licence Expired percentage", 3, 180, "Expired licences", "Licences counted"),
        };

        var result = EntityTilePercentageInjector.Inject(SnaptileHtml, figures);

        Assert.Contains(">1.70<", result);
        Assert.Contains("class=\"hr-i pf\"", result);
        Assert.Contains("pf-num-value\">3<", result);
        Assert.Contains("pf-den-value\">180<", result);
        Assert.Contains("Expired licences", result);
        // The tile's own description line is untouched.
        Assert.Contains("Two of six licence types", result);
    }

    [Fact]
    public void WrapsKpiPairsByPosition_SurvivingLabelTextDrift()
    {
        EntityTilePercentageInjector.FractionFigure?[] figures =
        [
            new("Critical overdue percentage", 4, 4, "Critical overdue obligations", "Critical obligations"),
            new("Imprisonment-on-Critical percentage", 1, 41, "Critical obligations with imprisonment exposure", "Obligations with imprisonment exposure"),
        ];

        var result = EntityTilePercentageInjector.InjectPositional(
            KpiPairPaneHtml, "di-pane-2", EntityTilePercentageInjector.TilePattern.KpiPair, figures);

        Assert.Contains(">100.00<", result);
        Assert.Contains("pf-num-value\">4<", result);
        Assert.Contains(">2.40<", result);
        Assert.Contains("pf-num-value\">1<", result);
        // The sub-text (a separate, not-yet-covered pp figure) stays untouched.
        Assert.Contains("48.40 points above the tenant rate.", result);
        // The DIFFERENT pane's own di-kpi__pair-val ("12", not even a percentage) is never touched -
        // positional matching stays scoped to the one named pane.
        Assert.Contains("di-kpi__pair-val tnum\">12</div>", result);
    }

    [Fact]
    public void KpiPairPosition_WithNoFigureThisRun_IsLeftBare()
    {
        EntityTilePercentageInjector.FractionFigure?[] figures = [null, new("x", 1, 2, "a", "b")];

        var result = EntityTilePercentageInjector.InjectPositional(
            KpiPairPaneHtml, "di-pane-2", EntityTilePercentageInjector.TilePattern.KpiPair, figures);

        Assert.Contains("di-kpi__pair-val tnum\">100.00%</div>", result); // first position: untouched
        Assert.Contains(">2.40<", result); // second position: wrapped
    }

    [Fact]
    public void WrapsFybarsByPosition_SurvivingLabelTextDrift()
    {
        EntityTilePercentageInjector.FractionFigure?[] figures =
        [
            new("Current-period on-time percentage", 68, 116, "On-time completions", "Completed events"),
            new("Previous-period on-time percentage", 164, 333, "On-time completions", "Completed events"),
        ];

        var result = EntityTilePercentageInjector.InjectPositional(
            FybarPaneHtml, "di-pane-4", EntityTilePercentageInjector.TilePattern.Fybar, figures);

        Assert.Contains(">58.60<", result);
        Assert.Contains("pf-num-value\">68<", result);
        Assert.Contains(">49.20<", result);
        Assert.Contains("pf-num-value\">164<", result);
    }

    // Real shape: Tab4's Evidence card - a stacklegend with two items, SAME <b class="tnum">%</b>
    // shape the fybar card above also uses, in the SAME pane - this proves the two patterns don't
    // cross-wrap each other's values.
    private const string StackLegendPaneHtml = """
        <html><body>
        <section id="di-pane-4" aria-label="Operations">
        <div class="di-fybar__lbl"><span>Current period</span><b class="tnum">58.60%</b></div>
        <div class="di-stacklegend">
          <span class="di-stacklegend__item"><i class="di-stacklegend__sw"></i>With review trail &mdash; <b class="tnum">96.50%</b></span>
          <span class="di-stacklegend__item"><i class="di-stacklegend__sw"></i>No review trail &mdash; <b class="tnum">3.50%</b></span>
        </div>
        </section>
        </body></html>
        """;

    [Fact]
    public void WrapsStackLegendByPosition_WithoutTouchingTheFybarsOwnValueInTheSamePane()
    {
        EntityTilePercentageInjector.FractionFigure?[] figures =
        [
            new("Review-trail percentage", 495, 513, "Closed schedules with a review trail", "Closed schedules"),
            new("No-review-trail percentage", 18, 513, "Closed schedules without a review trail", "Closed schedules"),
        ];

        var result = EntityTilePercentageInjector.InjectPositional(
            StackLegendPaneHtml, "di-pane-4", EntityTilePercentageInjector.TilePattern.StackLegend, figures);

        Assert.Contains("pf-num-value\">495<", result);
        Assert.Contains("pf-num-value\">18<", result);
        // The fybar's own 58.60% (same pane, same <b class="tnum">% shape) is untouched.
        Assert.Contains("<b class=\"tnum\">58.60%</b></div>", result);
    }

    // [REGRESSION, found live against a real rendered report] Two InjectPositional calls on the
    // SAME pane (Fybar then StackLegend, both real on Tab4) each started their own id counter at
    // 0 - a real duplicate id="pf-tile-di-pane-4-0" existed in production output before this fix.
    [Fact]
    public void TwoPositionalCallsOnTheSamePane_NeverProduceDuplicateIds()
    {
        EntityTilePercentageInjector.FractionFigure?[] fybarFigures = [new("a", 68, 116, "x", "y")];
        EntityTilePercentageInjector.FractionFigure?[] stackFigures = [new("b", 495, 513, "x", "y")];

        var result = EntityTilePercentageInjector.InjectPositional(
            StackLegendPaneHtml, "di-pane-4", EntityTilePercentageInjector.TilePattern.Fybar, fybarFigures);
        result = EntityTilePercentageInjector.InjectPositional(
            result, "di-pane-4", EntityTilePercentageInjector.TilePattern.StackLegend, stackFigures);

        var ids = System.Text.RegularExpressions.Regex.Matches(result, "id=\"(pf-tile-[a-zA-Z0-9-]+)\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(ids.Count >= 2, "expected at least one id from each pass");
    }

    [Fact]
    public void InjectPositional_PaneAbsentThisRun_ReturnsHtmlUnchanged()
    {
        EntityTilePercentageInjector.FractionFigure?[] figures = [new("x", 1, 2, "a", "b")];

        var result = EntityTilePercentageInjector.InjectPositional(
            FybarPaneHtml, "di-pane-99-does-not-exist", EntityTilePercentageInjector.TilePattern.Fybar, figures);

        Assert.Equal(FybarPaneHtml, result);
    }

    [Fact]
    public void UnknownLabel_IsLeftBareNeverGuessed()
    {
        var figures = new Dictionary<string, EntityTilePercentageInjector.FractionFigure>
        {
            ["Some other tile"] = new("x", 1, 2, "a", "b"),
        };

        var result = EntityTilePercentageInjector.Inject(SnaptileHtml, figures);

        Assert.Equal(SnaptileHtml, result);
    }

    [Fact]
    public void NoFigures_ReturnsHtmlUnchanged()
    {
        var result = EntityTilePercentageInjector.Inject(SnaptileHtml, new Dictionary<string, EntityTilePercentageInjector.FractionFigure>());

        Assert.Equal(SnaptileHtml, result);
    }

    // [REGRESSION, found live via user-supplied screenshots 2026-10-01] Same native-title-attribute
    // bug as EntityScoreFormulaInjector - the browser's own tooltip rendered as a second black box
    // stacked on top of the custom `.pf-panel`, on every single percentage hover-link.
    [Fact]
    public void Trigger_NeverCarriesANativeTitleAttribute()
    {
        var figures = new Dictionary<string, EntityTilePercentageInjector.FractionFigure>
        {
            ["Licence · Expired"] = new("x", 3, 180, "a", "b"),
        };

        var result = EntityTilePercentageInjector.Inject(SnaptileHtml, figures);

        Assert.DoesNotMatch("""class="hr-i[^"]*"[^>]*\stitle=""", result);
    }

    [Fact]
    public void IncludesTheSharedPfFractionCssExactlyOnce()
    {
        var figures = new Dictionary<string, EntityTilePercentageInjector.FractionFigure>
        {
            ["Licence · Expired"] = new("x", 3, 180, "a", "b"),
        };

        var result = EntityTilePercentageInjector.Inject(SnaptileHtml, figures);

        Assert.Contains(".pf-frac{", result);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result, System.Text.RegularExpressions.Regex.Escape(".pf-frac{")).Count);
    }
}

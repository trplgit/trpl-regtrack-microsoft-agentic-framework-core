using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class EntityScoreFormulaInjectorTests
{
    // Real markup shape from prompts/05_report_html_fixed_holistic_v3.md's own "Hero + score
    // components" section - donut score + N component cards, each an exact `.di-comp` block.
    private const string HeroHtml = """
        <html><body>
        <section class="di-hero"><div class="di-hero__inner"><div class="di-scoreblock">
        <div class="di-donut di-donut--warn" aria-hidden="true"><svg viewBox="0 0 120 120">
        <circle class="di-donut__track" cx="60" cy="60" r="52" fill="none" stroke-width="12"></circle>
        <circle class="di-donut__arc" cx="60" cy="60" r="52" fill="none" stroke-width="12" stroke-linecap="round" style="stroke-dasharray:169.9 326.7"></circle>
        </svg><div class="di-donut__ctr"><div class="di-donut__num tnum">52</div><div class="di-donut__of">/ 100</div></div></div>
        </div></div>
        <div class="di-components"><div class="di-components__grid">
        <div class="di-comp">
          <div class="di-comp__name">Risk<small>Weight 30%</small></div>
          <div class="di-comp__bar di-comp__bar--bad"><i style="width:35%"></i></div>
          <div class="di-comp__row"><b class="tnum">35</b><span>35 &times; 0.3</span></div>
        </div>
        <div class="di-comp">
          <div class="di-comp__name">Licence<small>Weight 20%</small></div>
          <div class="di-comp__bar di-comp__bar--ok"><i style="width:80%"></i></div>
          <div class="di-comp__row"><b class="tnum">80</b><span>80 &times; 0.2</span></div>
        </div>
        </div></div></section>
        </body></html>
        """;

    private static readonly EntityScoreFormulaInjector.ScoreComponent Risk = new("Risk", Score: 35, Weight: 0.3m);
    private static readonly EntityScoreFormulaInjector.ScoreComponent Licence = new("Licence", Score: 80, Weight: 0.2m);

    [Fact]
    public void WithNoCompositeScore_ReturnsHtmlUnchanged()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: null, []);

        Assert.Equal(HeroHtml, result);
    }

    [Fact]
    public void WrapsTheDonutCompositeScoreAsAHoverLink()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        // The real "52" text node stays visible and legible without JS/hover (a11y + print) ...
        Assert.Contains(">52<", result);
        // ... but is now also the trigger label for a hover-link panel.
        Assert.Contains("class=\"hr-i pf\"", result);
        Assert.Contains("Compliance-health score", result);
        // Formula box states the real weighted-sum arithmetic, not invented numbers - including
        // the actual PRODUCT, not just the two operands (found live: a user had to compute
        // "46.0 x 0.15" by hand because the box stopped short of its own answer).
        Assert.Contains("35", result);
        Assert.Contains("0.3", result);
        Assert.Contains("80", result);
        Assert.Contains("0.2", result);
        Assert.Contains("= 10.5", result); // 35 x 0.3, Risk's real contribution
        Assert.Contains("= 16.0", result); // 80 x 0.2, Licence's real contribution
    }

    [Fact]
    public void WrapsEachComponentsOwnScore_KeepingTheSurroundingMarkupIntact()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        Assert.Contains("Risk score", result);
        Assert.Contains("Licence score", result);
        // The bar widths and weight labels outside the wrapped number are untouched.
        Assert.Contains("width:35%", result);
        Assert.Contains("width:80%", result);
        Assert.Contains("Weight 30%", result);
        Assert.Contains("Weight 20%", result);
    }

    [Fact]
    public void EveryGeneratedIdIsUnique()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        var ids = System.Text.RegularExpressions.Regex.Matches(result, "id=\"(pf-[a-z0-9-]+)\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(ids.Count >= 3, "expected at least one id for the composite score plus one per component");
    }

    [Fact]
    public void IncludesTheSharedHrPfStyleAndScriptExactlyOnce()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        Assert.Contains(".hr-just-closed", result);
        Assert.Contains("addEventListener('mouseleave'", result);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result, System.Text.RegularExpressions.Regex.Escape(".hr.hr-just-closed .hr-panel{opacity:0")).Count);
    }

    [Fact]
    public void ComponentWithNoMatchingMarkup_IsSkippedWithoutThrowing()
    {
        var ghost = new EntityScoreFormulaInjector.ScoreComponent("Evidence", Score: 90, Weight: 0.1m);

        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence, ghost]);

        Assert.DoesNotContain("Evidence score", result);
    }

    [Fact]
    public void WhenTheDonutMarkupIsAbsent_LeavesDocumentUnchangedBeyondComponents()
    {
        const string html = "<html><body><p>no hero here</p></body></html>";

        var result = EntityScoreFormulaInjector.Inject(html, compositeScore: 52, [Risk]);

        Assert.Equal(html, result);
    }

    // [REGRESSION, found live against a real rendered report] Real component scores are fractional
    // (e.g. "94.7", "0.0") - an earlier version of this class cast to int and silently truncated
    // them, and its regex also required capturing the weight as a decimal from <small>, which real
    // markup shows as a percentage ("Weight 20%") - together these meant it matched ZERO real
    // components against a real report.
    [Fact]
    public void PreservesFractionalScores_AndToleratesAPercentageWeightLabel()
    {
        const string html = """
            <html><body>
            <div class="di-comp">
              <div class="di-comp__name">Coverage<small>Weight 15%</small></div>
              <div class="di-comp__bar di-comp__bar--ok"><i style="width:94.7%"></i></div>
              <div class="di-comp__row"><b class="tnum">94.7</b><span>94.7 &times; 0.15</span></div>
            </div>
            </body></html>
            """;
        var coverage = new EntityScoreFormulaInjector.ScoreComponent("Coverage", Score: 94.7m, Weight: 0.15m);

        var result = EntityScoreFormulaInjector.Inject(html, compositeScore: 50, [coverage]);

        Assert.Contains(">94.7<", result);
        Assert.DoesNotContain(">94<", result);
        Assert.Contains("Coverage score - 94.7", result);
    }

    // [REGRESSION, user caught this reading the actual rendered popup] "HOW IT IS CALCULATED"
    // showed "46.0" and "0.15" (the two operands) but never 6.9 (their product) - the panel never
    // finished its own stated calculation, so a reader had to multiply it by hand to check it.
    [Fact]
    public void ComponentPanel_StatesTheActualProduct_NotJustTheTwoOperands()
    {
        const string html = """
            <html><body>
            <div class="di-comp">
              <div class="di-comp__name">Timeliness<small>Weight 15%</small></div>
              <div class="di-comp__bar di-comp__bar--bad"><i style="width:46.0%"></i></div>
              <div class="di-comp__row"><b class="tnum">46.0</b><span>46.0 &times; 0.15</span></div>
            </div>
            </body></html>
            """;
        var timeliness = new EntityScoreFormulaInjector.ScoreComponent("Timeliness", Score: 46.0m, Weight: 0.15m);

        var result = EntityScoreFormulaInjector.Inject(html, compositeScore: 50, [timeliness]);

        // Computed the same way the code does, not hardcoded - decimal's own trailing-zero/scale
        // rules are not worth guessing at in a test when the production expression can just be
        // reused directly. The "=" operator and the result value are separate HTML elements (not
        // literally adjacent text), so check each is present rather than one joined substring.
        var expected = decimal.Round(46.0m * 0.15m, 2);
        Assert.Contains("pf-diff-op\">=</span>", result);
        Assert.Contains($"pf-diff-value\">{expected}</span>", result);
        Assert.Contains("Contribution to the composite score", result);
    }

    // [REGRESSION, found live via user-supplied screenshots 2026-10-01] The trigger label carried
    // both a `title` attribute AND the custom `.pf-panel` - the browser's own native tooltip then
    // rendered as a second, overlapping black box stacked directly on top of the custom white
    // panel every time, on every single hover-link in the report. The custom panel already states
    // what the native tooltip would have said, so the native one must not fire at all.
    [Fact]
    public void CompositeAndComponentTriggers_NeverCarryANativeTitleAttribute()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        Assert.DoesNotMatch("""class="hr-i[^"]*"[^>]*\stitle=""", result);
    }

    // [ADDED 2026-10-08, user-reported clarity gap] The composite panel showed the 5 (or however
    // many) weighted-contribution rows but never the final /weightSum step - a reader doing the
    // obvious thing (adding the rows) landed on the raw numerator, not the real displayed score,
    // with zero explanation of the gap when a pillar's weight isn't counted this run. The real
    // displayed compositeScore is reused verbatim as the stated answer - never recomputed here,
    // so this can never drift from what the donut actually shows.
    [Fact]
    public void CompositePanel_ShowsTheRenormalizationStep_WhenNotEveryPillarScored()
    {
        // Risk(0.3) + Licence(0.2) = 0.5 of a notional full weight set - deliberately not 1.0,
        // same shape as a real run missing some pillars.
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        Assert.Contains("0.5", result); // the weight actually counted this run
        Assert.Contains("pf-diff-result", result);
        // The stated final answer is the real passed-in score, not a value re-derived here.
        Assert.Contains("pf-diff-value\">52</span><span class=\"pf-diff-label\">Composite score", result);
    }

    // [ADDED 2026-10-08, user-reported] "why does Risk get more weight than Evidence" - each
    // pillar's own popup should say what it measures, not just restate the arithmetic.
    [Fact]
    public void ComponentPanel_StatesWhatThePillarMeasures()
    {
        var result = EntityScoreFormulaInjector.Inject(HeroHtml, compositeScore: 52, [Risk, Licence]);

        Assert.Contains("pf-what", result);
        Assert.Contains("overdue", result); // Risk's definition mentions overdue critical-risk obligations
    }
}

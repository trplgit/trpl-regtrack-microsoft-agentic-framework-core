using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

public sealed class InjectNumberFormulaActivityTests
{
    private const string EntityHtml = """
        <html><body><section class="di-hero"><div class="di-scoreblock">
        <div class="di-donut__ctr"><div class="di-donut__num tnum">52</div></div>
        </div>
        <div class="di-components__grid">
        <div class="di-comp"><div class="di-comp__name">Risk<small>Weight 0.3</small></div>
        <div class="di-comp__bar di-comp__bar--bad"><i style="width:35%"></i></div>
        <div class="di-comp__row"><b class="tnum">35</b><span>35 &times; 0.3</span></div></div>
        </div></section></body></html>
        """;

    [Fact]
    public async Task WithNoEntityScore_BehavesExactlyAsBefore()
    {
        var activity = new InjectNumberFormulaActivity();
        var input = new InjectNumberFormulaInput(EntityHtml, []);

        var result = await activity.RunAsync(input);

        Assert.Equal(EntityHtml, result.Html);
    }

    [Fact]
    public async Task WithEntityScore_AlsoWrapsTheHeroScoreAsAHoverLink()
    {
        var activity = new InjectNumberFormulaActivity();
        var input = new InjectNumberFormulaInput(
            EntityHtml, [],
            EntityCompositeScore: 52,
            EntityScoreComponents: [new EntityScoreFormulaInjector.ScoreComponent("Risk", 35, 0.3m)]);

        var result = await activity.RunAsync(input);

        Assert.Contains("Compliance-health score", result.Html);
        Assert.Contains("Risk score", result.Html);
    }

    [Fact]
    public async Task RunsBothInjectorsTogether_LicenceFiguresAndEntityScoreCanBothBePresent()
    {
        var activity = new InjectNumberFormulaActivity();
        var figure = new NumberFormulaInjector.Figure("Active licences", "21", "some text");
        var input = new InjectNumberFormulaInput(
            EntityHtml, [figure],
            EntityCompositeScore: 52,
            EntityScoreComponents: [new EntityScoreFormulaInjector.ScoreComponent("Risk", 35, 0.3m)]);

        var result = await activity.RunAsync(input);

        Assert.Contains("How your numbers are worked out", result.Html);
        Assert.Contains("Compliance-health score", result.Html);
    }
}

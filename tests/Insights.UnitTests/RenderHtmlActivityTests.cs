using Insights.Agents;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class RenderHtmlActivityTests
{
    [Fact]
    public async Task RunAsync_PassesThroughToRenderAsync()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

        IReadOnlyList<Assertion> assertions = [];
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html></html>", 3100));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { ["compliance_health"] = agent.Object });
        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt));

        Assert.Equal("<!DOCTYPE html><html></html>", result.Html);
        Assert.Equal(3100, result.TotalTokens);
    }

    /// <summary>
    /// [REMOVED 2026-09-01] This activity briefly took two agents and picked between them by
    /// ReportType - the plain "compliance_health" render path (05_report_html.md) it existed to
    /// distinguish from was removed the same session, so there is only ever one IReportHtmlAgent
    /// again, and the test above already covers it regardless of what ReportType string is passed
    /// through - RenderHtmlActivity itself no longer inspects that value at all.
    /// </summary>
    [Fact]
    public async Task RunAsync_FixedHolisticReportType_UsesTheSameSingleAgent()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var plan = new CompositionPlan(new CompositionHero("snapshot", "fixed template"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

        IReadOnlyList<Assertion> assertions = [];
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", FixedHolisticComposition.ReportType, generatedAt, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>fixed holistic</html>", 4200));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { [FixedHolisticComposition.ReportType] = agent.Object });
        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", FixedHolisticComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>fixed holistic</html>", result.Html);
        Assert.Equal(4200, result.TotalTokens);
    }
}

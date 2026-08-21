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

        agent.Setup(a => a.RenderAsync(plan, narrative, "Tenant 29 (UAT)", "compliance_health", generatedAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync("<!DOCTYPE html><html></html>");

        var activity = new RenderHtmlActivity(agent.Object);
        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, "Tenant 29 (UAT)", "compliance_health", generatedAt));

        Assert.Equal("<!DOCTYPE html><html></html>", result.Html);
    }
}

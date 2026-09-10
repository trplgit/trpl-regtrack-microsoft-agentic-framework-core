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
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt, null, null, null, It.IsAny<CancellationToken>()))
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
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", FixedHolisticComposition.ReportType, generatedAt, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>fixed holistic</html>", 4200));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { [FixedHolisticComposition.ReportType] = agent.Object });
        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", FixedHolisticComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>fixed holistic</html>", result.Html);
        Assert.Equal(4200, result.TotalTokens);
    }

    /// <summary>
    /// Design spec Sec.4.1 - a dimension-specific key ("{ReportType}:{DimensionName}") is tried
    /// first when the plan has exactly one block, ahead of the plain ReportType key. Uses
    /// DimensionSelectionComposition.Build's real block-naming (the block's Block field IS the
    /// dimension name, e.g. "Location" - not a synthetic string invented for this test).
    /// </summary>
    [Fact]
    public async Task RunAsync_SingleDimensionRequest_PrefersTheDimensionSpecificAgentWhenRegistered()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var locationAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Location"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        locationAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location, finalized</html>", 2000));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>location, finalized</html>", result.Html);
        genericAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Regression guard - today's exact behavior when no dimension-specific agent is registered yet (the real current state for every dimension).</summary>
    [Fact]
    public async Task RunAsync_SingleDimensionRequest_FallsBackToGenericAgentWhenNoDimensionSpecificOneRegistered()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Nature"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>nature, generic</html>", 1800));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>nature, generic</html>", result.Html);
    }

    /// <summary>Design spec Sec.4.2 - a multi-dimension request never considers a dimension-specific key, even if one happens to be registered for one of the requested dimensions.</summary>
    [Fact]
    public async Task RunAsync_MultiDimensionRequest_AlwaysUsesTheGenericAgent_NeverADimensionSpecificOne()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var locationAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Location", "Users"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location+users, generic</html>", 3200));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>location+users, generic</html>", result.Html);
        locationAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Unchanged - an unrecognized ReportType still throws, fail-closed, regardless of this change.</summary>
    [Fact]
    public async Task RunAsync_UnrecognisedReportType_StillThrows()
    {
        var plan = new CompositionPlan(new CompositionHero("snapshot", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "no_such_report_type", generatedAt)));
    }
}

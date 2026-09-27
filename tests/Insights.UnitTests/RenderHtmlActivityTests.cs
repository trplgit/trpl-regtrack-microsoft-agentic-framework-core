using Insights.Agents;
using Insights.Data;
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
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html></html>", 3100));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { ["compliance_health"] = agent.Object });
        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt));

        Assert.Equal("<!DOCTYPE html><html></html>", result.Html);
        Assert.Equal(3100, result.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_RunIdGivenAndReasoningSummaryPresent_RecordsIt()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html></html>", 3100, "chose a coverage-first layout because..."));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { ["compliance_health"] = agent.Object }, recorder.Object);
        await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt), runId: "run-123");

        recorder.Verify(r => r.RecordAsync("run-123", "render_html", "chose a coverage-first layout because...", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NoRunIdGiven_NeverCallsRecorder()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html></html>", 3100, "some reasoning"));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { ["compliance_health"] = agent.Object }, recorder.Object);
        await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "compliance_health", generatedAt));

        recorder.Verify(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        agent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", FixedHolisticComposition.ReportType, generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
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

        locationAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location, finalized</html>", 2000));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, DimensionName: "Location"));

        Assert.Equal("<!DOCTYPE html><html>location, finalized</html>", result.Html);
        genericAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// [ADDED 2026-09-27] Single-dimension render: the real rows/totals are injected by code into
    /// the returned page (DimensionDataInjector), so the model never has to re-type them.
    /// </summary>
    [Fact]
    public async Task RunAsync_SingleDimensionWithRows_InjectsTheRealDataBlock()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Users"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];
        var rows = new Dictionary<string, string> { ["Users"] = """[{"UserID":7,"UserName":"Asha"}]""" };
        var totals = new Dictionary<string, string> { ["Users"] = """{"UsersReported":1}""" };

        agent.Setup(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html><body><script>draw()</script></body></html>", 100));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { [$"{DimensionSelectionComposition.ReportType}:Users"] = agent.Object });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "T", DimensionSelectionComposition.ReportType, generatedAt,
            DimensionRowsJson: rows, DimensionControlTotalsJson: totals, DimensionName: "Users"));

        Assert.Contains("id=\"insights-data\"", result.Html);
        Assert.Contains("\"UserName\":\"Asha\"", result.Html);
        Assert.Contains("\"UsersReported\":1", result.Html);
    }

    [Fact]
    public async Task RunAsync_SingleDimension_ListsTypedNumbersThatDoNotTraceToTheData()
    {
        var agent = new Mock<IReportHtmlAgent>();
        var rows = new Dictionary<string, string> { ["Act"] = """[{"ActID":1,"ActName":"Factories Act","Instances":20,"Overdue":15},{"ActID":2,"ActName":"Gratuity Act","Instances":13,"Overdue":13}]""" };
        var totals = new Dictionary<string, string> { ["Act"] = """{"ScopedInstances":33,"OverdueInstances":28}""" };

        agent.Setup(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html><body><p>28 of 33 overdue (84.8%)</p><p>1,640 penalties</p></body></html>", 100));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent> { [$"{DimensionSelectionComposition.ReportType}:Act"] = agent.Object });

        var result = await activity.RunAsync(new RenderHtmlInput(DimensionSelectionComposition.Build(["Act"]), new NarrativeResult([]), [], "T",
            DimensionSelectionComposition.ReportType, new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
            DimensionRowsJson: rows, DimensionControlTotalsJson: totals, DimensionName: "Act"));

        Assert.Equal(["1,640"], result.UntracedNumbers);
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

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
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

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location+users, generic</html>", 3200));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>location+users, generic</html>", result.Html);
        locationAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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

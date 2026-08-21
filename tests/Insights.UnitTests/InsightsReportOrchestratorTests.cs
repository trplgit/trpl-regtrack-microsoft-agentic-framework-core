using System.Text.Json;
using DurableTask.Core;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class InsightsReportOrchestratorTests
{
    [Fact]
    public async Task RunTask_HappyPath_ReachesPersistStubOutput_ReflectionApprovesFirstTry()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, JsonElement>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan));
        context.Setup(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));
        context.Setup(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new RenderHtmlOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistStubOutput>(typeof(PersistStubActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PersistStubOutput("stub-29-compliance_health-abc"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var result = await orchestrator.RunTask(context.Object, input);

        Assert.Equal("stub-29-compliance_health-abc", result.ArtifactId);
        context.Verify(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity), It.IsAny<object[]>()), Times.Once);
        context.Verify(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity), It.IsAny<object[]>()), Times.Once);

        var status = JsonSerializer.Deserialize<JsonElement>(orchestrator.GetStatus());
        Assert.Equal("complete", status.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task RunTask_CompositionReflectionRevises_CallsComposeTwice()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var issue = new CompositionReflectionIssue("lede", "hero", "problem", "fix");

        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, JsonElement>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan));
        context.SetupSequence(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Revise, [issue])))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));
        context.Setup(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new RenderHtmlOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistStubOutput>(typeof(PersistStubActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new PersistStubOutput("stub-29-compliance_health-abc"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        await orchestrator.RunTask(context.Object, input);

        context.Verify(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity), It.IsAny<object[]>()), Times.Exactly(2));
        context.Verify(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity), It.IsAny<object[]>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RunTask_PublishGateRefuses_ThrowsAndNeverRenders()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, JsonElement>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan));
        context.Setup(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity), It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, [])));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity), It.IsAny<object[]>()))
            .ThrowsAsync(new OrchestrationRefusedException("GATE_REFUSED", "refused"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));
        Assert.Equal("GATE_REFUSED", ex.ReasonCode);
        context.Verify(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity), It.IsAny<object[]>()), Times.Never);
    }
}

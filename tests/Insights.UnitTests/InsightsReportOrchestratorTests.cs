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
    public async Task RunTask_HappyPath_ReachesPersistOutput_ReflectionApprovesFirstTry()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));
        context.Setup(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RenderHtmlOutput("<html></html>", 1000));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PersistOutput("11111111-1111-1111-1111-111111111111"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var result = await orchestrator.RunTask(context.Object, input);

        Assert.Equal("11111111-1111-1111-1111-111111111111", result.ReportId);
        context.Verify(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
        context.Verify(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);

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

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan, 1000));
        context.SetupSequence(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Revise, [issue]), 1000))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));
        context.Setup(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RenderHtmlOutput("<html></html>", 1000));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PersistOutput("11111111-1111-1111-1111-111111111111"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        await orchestrator.RunTask(context.Object, input);

        context.Verify(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Exactly(2));
        context.Verify(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RunTask_PublishGateRefuses_ThrowsAndNeverRenders()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ThrowsAsync(new OrchestrationRefusedException("GATE_REFUSED", "refused"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));
        Assert.Equal("GATE_REFUSED", ex.ReasonCode);
        context.Verify(c => c.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);

        // Design doc Sec.12.3: a gate refusal still spent real tokens (compose + both reflections)
        // before it refused - that spend must still be recorded, not silently dropped because the
        // report never shipped.
        context.Verify(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
    }

    /// <summary>Design doc Sec.12.3: a tenant already over its monthly ceiling must refuse before ANY other work - not even a scope lookup.</summary>
    [Fact]
    public async Task RunTask_MonthlyTokenBudgetExceeded_RefusesBeforeGatherScope_AndNeverRecordsUsage()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ThrowsAsync(new OrchestrationRefusedException("MONTHLY_BUDGET_EXCEEDED", "refused"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.Equal("MONTHLY_BUDGET_EXCEEDED", ex.ReasonCode);
        context.Verify(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
        // Nothing was spent yet - the check itself refused before the try/finally even starts, so
        // there is nothing for RecordTenantTokenUsageActivity to record on this path.
        context.Verify(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
    }

    /// <summary>Design doc Sec.12.3: the check runs against real month-to-date usage, threaded straight through to CheckTenantTokenBudgetActivity's input untouched.</summary>
    [Fact]
    public async Task RunTask_PassesTenantIdAndCurrentTime_ToTheBudgetCheck()
    {
        var context = new Mock<OrchestrationContext>();
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(now);

        CheckTenantTokenBudgetInput? capturedInput = null;
        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, args) => capturedInput = (CheckTenantTokenBudgetInput)args[0])
            .ThrowsAsync(new OrchestrationRefusedException("MONTHLY_BUDGET_EXCEEDED", "refused"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(1490, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.NotNull(capturedInput);
        Assert.Equal(1490, capturedInput!.CustomerId);
        Assert.Equal(now, capturedInput.AsOfUtc);
    }

    /// <summary>
    /// Item 17 (design doc Sec.12.3): "a single report exceeding its expected envelope aborts to
    /// the gate-refusal path rather than running away." One call alone (300k) already exceeds
    /// Budget:PerRunTokenCeiling's 250k default - the run must refuse immediately after composing,
    /// never reach narrate/render.
    /// </summary>
    [Fact]
    public async Task RunTask_PerRunTokenBudgetExceeded_RefusesAndNeverNarrates()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan, 300_000));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.Equal("BUDGET_EXCEEDED", ex.ReasonCode);
        Assert.DoesNotContain("300000", ex.Message); // internal number never in the user-safe message
        Assert.Contains(ex.InternalDiagnostics, d => d.Contains("300000"));
        context.Verify(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
        context.Verify(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
    }

    /// <summary>Several calls that individually look fine must still sum and refuse - the budget is on the RUN, not any one call.</summary>
    [Fact]
    public async Task RunTask_PerRunTokenBudgetExceeded_AcrossMultipleCalls_StillRefuses()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeOutput(plan, 120_000));
        context.Setup(c => c.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnCompositionOutput(new CompositionReflectionResult(ReflectionVerdict.Approve, []), 120_000));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 20_000)); // 120k + 120k + 20k = 260k > 250k ceiling

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.Equal("BUDGET_EXCEEDED", ex.ReasonCode);
        context.Verify(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
    }
}

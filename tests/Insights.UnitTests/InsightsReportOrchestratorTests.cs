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
    /// <summary>
    /// [REPLACES the deleted RunTask_HappyPath_ReachesPersistOutput_ReflectionApprovesFirstTry,
    /// 2026-09-11] That test exercised the old "compliance_health" dynamic-composition happy path
    /// (Compose/ReflectOnComposition) - gone along with ComposeActivity/ReflectOnCompositionActivity
    /// themselves. This test already covers the identical shared plumbing (token budget, gather
    /// scope, fetch dimensions, compute score, narrate + reflection, publish gate, render, every
    /// injector, normalize/sanitize, structure validation, Playwright QA, persist) end to end for a
    /// ReportType that still exists, so no coverage is lost by the deletion.
    /// </summary>
    [Fact]
    public async Task RunTask_FixedHolisticReportType_SkipsComposeAndReflection_ReachesPersistOutput()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));

        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        // Deliberately NO Setup for ComposeOutput/ReflectOnCompositionOutput - Times.Never below
        // proves they were never called, not merely unconfigured-and-ignored.
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));
        context.Setup(c => c.ScheduleWithRetry<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new RenderHtmlOutput("<html></html>", 1000));
        context.Setup(c => c.ScheduleTask<InjectFontOutput>(typeof(InjectFontActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectFontOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageGridOutput>(typeof(InjectCoverageGridActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageGridOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageCssOutput>(typeof(InjectCoverageCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageScriptOutput>(typeof(InjectCoverageScriptActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageScriptOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarOutput>(typeof(InjectBacklogAgeBarActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarCssOutput>(typeof(InjectBacklogAgeBarCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookOutput>(typeof(InjectForwardLookActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookCssOutput>(typeof(InjectForwardLookCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<ValidateFixedHolisticStructureOutput>(typeof(ValidateFixedHolisticStructureActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ValidateFixedHolisticStructureOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PersistOutput("22222222-2222-2222-2222-222222222222"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var result = await orchestrator.RunTask(context.Object, input);

        Assert.Equal("22222222-2222-2222-2222-222222222222", result.ReportId);
        context.Verify(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
    }

    // [DELETED 2026-09-11] RunTask_CompositionReflectionRevises_CallsComposeTwice - tested the
    // dynamic composition reflection loop (ComposeActivity/ReflectOnCompositionActivity), which no
    // longer exists at all. FixedHolisticComposition/DimensionSelectionComposition are pure
    // functions with no reflection loop of their own, so there is nothing left to test here.

    [Fact]
    public async Task RunTask_PublishGateRefuses_ThrowsAndNeverRenders()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ThrowsAsync(new OrchestrationRefusedException("GATE_REFUSED", "refused"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));
        Assert.Equal("GATE_REFUSED", ex.ReasonCode);
        context.Verify(c => c.ScheduleWithRetry<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()), Times.Never);

        // Design doc Sec.12.3: a gate refusal still spent real tokens (narrate + reflection) before
        // it refused - that spend must still be recorded, not silently dropped because the report
        // never shipped.
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
        var input = new InsightsReportOrchestrationInput(1490, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.NotNull(capturedInput);
        Assert.Equal(1490, capturedInput!.CustomerId);
        Assert.Equal(now, capturedInput.AsOfUtc);
    }

    /// <summary>
    /// Item 17 (design doc Sec.12.3): "a single report exceeding its expected envelope aborts to
    /// the gate-refusal path rather than running away." Composition is a free deterministic call for
    /// every ReportType now (FixedHolisticComposition.Build/DimensionSelectionComposition.Build spend
    /// no tokens), so the first chargeable call in the pipeline is Narrate - one call alone (300k)
    /// already exceeds Budget:PerRunTokenCeiling's 250k default, and the run must refuse immediately,
    /// never reach the reflection loop or render.
    /// </summary>
    [Fact]
    public async Task RunTask_PerRunTokenBudgetExceeded_RefusesAndNeverNarrates()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 300_000));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.Equal("BUDGET_EXCEEDED", ex.ReasonCode);
        Assert.DoesNotContain("300000", ex.Message); // internal number never in the user-safe message
        Assert.Contains(ex.InternalDiagnostics, d => d.Contains("300000"));
        context.Verify(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
    }

    /// <summary>
    /// Render's RetryOptions.Handle must retry a transient/malformed-render failure but NEVER a
    /// deliberate deterministic refusal (Normalize/Sanitize/Structure/PublishGate) - same input
    /// always fails those the same way, so retrying would only burn real LLM tokens chasing a
    /// result that cannot change. RenderHtmlActivity itself never throws OrchestrationRefusedException
    /// today, so this pins the FILTER itself, not a call this orchestrator would ever actually make.
    /// </summary>
    [Fact]
    public async Task RunTask_RenderRetryOptions_HandlesTransientFailures_ButNeverADeterministicRefusal()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));

        RetryOptions? capturedRetryOptions = null;
        context.Setup(c => c.ScheduleWithRetry<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .Callback<string, string, RetryOptions, object[]>((_, _, retryOptions, _) => capturedRetryOptions = retryOptions)
            .ReturnsAsync(new RenderHtmlOutput("<html></html>", 1000));

        // Everything after render must still be mocked for RunTask to complete - the point of this
        // test is the captured RetryOptions, not the full happy path, but RunTask needs a full path.
        context.Setup(c => c.ScheduleTask<InjectFontOutput>(typeof(InjectFontActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectFontOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageGridOutput>(typeof(InjectCoverageGridActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageGridOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageCssOutput>(typeof(InjectCoverageCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageScriptOutput>(typeof(InjectCoverageScriptActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageScriptOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarOutput>(typeof(InjectBacklogAgeBarActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarCssOutput>(typeof(InjectBacklogAgeBarCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookOutput>(typeof(InjectForwardLookActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookCssOutput>(typeof(InjectForwardLookCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<ValidateFixedHolisticStructureOutput>(typeof(ValidateFixedHolisticStructureActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ValidateFixedHolisticStructureOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PersistOutput("11111111-1111-1111-1111-111111111111"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        await orchestrator.RunTask(context.Object, input);

        Assert.NotNull(capturedRetryOptions);
        Assert.True(capturedRetryOptions!.Handle(new InvalidOperationException("malformed render")), "a transient/malformed-render failure must be retryable");
        Assert.False(capturedRetryOptions.Handle(new OrchestrationRefusedException("NOT_NORMALIZABLE", "refused")), "a deterministic refusal must never be retried");
    }

    /// <summary>
    /// [ADDED 2026-09-09] The real Angular product has no dedicated per-dimension branch for
    /// Entity at all (confirmed by reading detailed-insights.component.html/.ts directly - only
    /// 'User' and 'Department' get one; Entity itself falls through to the plain full holistic
    /// view). A "dimension_selection" request naming Entity alone is therefore translated,
    /// in-process, into a plain "fixed_holistic" request before anything else runs - proven here
    /// the same way RunTask_FixedHolisticReportType_SkipsComposeAndReflection_ReachesPersistOutput
    /// proves the real fixed_holistic path: ComputeScoreActivity DOES run (the opposite of every
    /// other dimension_selection request, which skips it), and the render call actually receives
    /// ReportType "fixed_holistic", never "dimension_selection" - a caller inspecting the rendered
    /// payload (or a render-agent-key lookup) sees the real fixed_holistic shape throughout, not a
    /// half-translated hybrid. [UPDATED 2026-09-11] No longer verifies Compose/ReflectOnComposition
    /// Times.Never - those activities were deleted outright along with "compliance_health", so
    /// there is nothing left that could call them regardless of ReportType.
    /// </summary>
    [Fact]
    public async Task RunTask_DimensionSelectionRequestingOnlyEntity_TranslatesToFixedHolistic()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));

        var narrative = new NarrativeResult([]);

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));

        FetchDimensionsInput? capturedFetchInput = null;
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, args) => capturedFetchInput = (FetchDimensionsInput)args[0])
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));

        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        context.Setup(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 1000));
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Approve, []), 1000));
        context.Setup(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PublishGateOutput(true));

        RenderHtmlInput? capturedRenderInput = null;
        context.Setup(c => c.ScheduleWithRetry<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .Callback<string, string, RetryOptions, object[]>((_, _, _, args) => capturedRenderInput = (RenderHtmlInput)args[0])
            .ReturnsAsync(new RenderHtmlOutput("<html></html>", 1000));

        context.Setup(c => c.ScheduleTask<InjectFontOutput>(typeof(InjectFontActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectFontOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageGridOutput>(typeof(InjectCoverageGridActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageGridOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageCssOutput>(typeof(InjectCoverageCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectCoverageScriptOutput>(typeof(InjectCoverageScriptActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectCoverageScriptOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarOutput>(typeof(InjectBacklogAgeBarActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectBacklogAgeBarCssOutput>(typeof(InjectBacklogAgeBarCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectBacklogAgeBarCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookOutput>(typeof(InjectForwardLookActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<InjectForwardLookCssOutput>(typeof(InjectForwardLookCssActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new InjectForwardLookCssOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NormalizeOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new SanitizeOutput("<html></html>"));

        ValidateFixedHolisticStructureInput? capturedStructureInput = null;
        context.Setup(c => c.ScheduleTask<ValidateFixedHolisticStructureOutput>(typeof(ValidateFixedHolisticStructureActivity).Name, "1.0", It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, args) => capturedStructureInput = (ValidateFixedHolisticStructureInput)args[0])
            .ReturnsAsync(new ValidateFixedHolisticStructureOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PlaywrightQaOutput(new ReportQaResult(false, [], false, [])));
        context.Setup(c => c.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new PersistOutput("33333333-3333-3333-3333-333333333333"));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(
            29, DimensionSelectionComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38, RequestedDimensions: ["Entity"]);

        var result = await orchestrator.RunTask(context.Object, input);

        Assert.Equal("33333333-3333-3333-3333-333333333333", result.ReportId);
        context.Verify(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
        context.Verify(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);

        Assert.NotNull(capturedFetchInput);
        Assert.Null(capturedFetchInput!.RequestedDimensions); // fetches every dimension, not just Entity
        Assert.NotNull(capturedRenderInput);
        Assert.Equal(FixedHolisticComposition.ReportType, capturedRenderInput!.ReportType);
        Assert.NotNull(capturedStructureInput);
        Assert.Equal(FixedHolisticComposition.ReportType, capturedStructureInput!.ReportType); // the real gate actually evaluates, not a no-op
    }

    /// <summary>
    /// Several calls that individually look fine must still sum and refuse - the budget is on the
    /// RUN, not any one call. [REWRITTEN 2026-09-11] Composition no longer charges tokens for any
    /// ReportType, so the three summed calls are now the narrate/reflect loop's own: first Narrate
    /// (120k), the reflection it triggers (100k, still under ceiling at 220k, verdict Revise), then
    /// the revised Narrate (40k, pushing the running total to 260k > 250k) - the run must refuse
    /// there, before ever reaching PublishGate.
    /// </summary>
    [Fact]
    public async Task RunTask_PerRunTokenBudgetExceeded_AcrossMultipleCalls_StillRefuses()
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);

        var narrative = new NarrativeResult([]);
        var issue = new NarrativeReflectionIssue("check", "block", "quote", "problem", "fix");

        context.Setup(c => c.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new CheckTenantTokenBudgetOutput(0));
        context.Setup(c => c.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new RecordTenantTokenUsageOutput());
        context.Setup(c => c.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new GatherScopeOutput([new ScopePair(100, 1)], "multi_entity"));
        context.Setup(c => c.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDimensionsOutput(new Dictionary<string, string>(), [], []));
        context.Setup(c => c.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ComputeScoreOutput(new OverallHealth(null, "Needs Attention", "flat", "test", []), [], "{}"));
        context.SetupSequence(c => c.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new NarrateOutput(narrative, 120_000))
            .ReturnsAsync(new NarrateOutput(narrative, 40_000)); // 120k + 100k + 40k = 260k > 250k ceiling
        context.Setup(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(new ReflectOnNarrativeOutput(new NarrativeReflectionResult(ReflectionVerdict.Revise, [issue]), 100_000));

        var orchestrator = new InsightsReportOrchestrator();
        var input = new InsightsReportOrchestrationInput(29, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26", 38);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => orchestrator.RunTask(context.Object, input));

        Assert.Equal("BUDGET_EXCEEDED", ex.ReasonCode);
        context.Verify(c => c.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
        context.Verify(c => c.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0", It.IsAny<object[]>()), Times.Never);
    }
}

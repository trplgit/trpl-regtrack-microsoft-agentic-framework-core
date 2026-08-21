using DurableTask.Core;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

/// <summary>
/// Wraps ReportCompositionPipeline's compose-reflect-narrate-reflect-gate-render-normalize-
/// sanitize-QA-persist sequence as a durable, crash-resumable, versioned orchestration
/// (CLAUDE.md build order item 11). Deterministic body only - every LLM call, every DB read,
/// every DateTime read lives in an activity (CLAUDE.md 6, spec 5).
/// </summary>
public sealed class InsightsReportOrchestrator : TaskOrchestration<PersistStubOutput, InsightsReportOrchestrationInput>
{
    public const string Name = "InsightsReportOrchestrator";
    public const string Version = "1.0";

    // KNOWN LIMITATION, not an oversight: input.Scope (entity-level sub-scoping) and input.Period
    // are accepted for contract-shape parity with API_CONTRACTS.md 3, but not threaded through
    // below. IDimensionRepository's nine GetXAsync methods take the caller's FULL tenant scope
    // (userId, customerId) with no entity-narrowing parameter, and an optional `asOf` for
    // period-scoping that FetchDimensionsActivity does not currently pass through either - this
    // matches every existing manual test and ReportCompositionPipeline itself, neither of which
    // support sub-scoping today. Wiring real entity-level scope filtering and period selection is
    // out of scope for this slice; flagged here so it is a visible decision, not a silent gap.

    private InsightsRunStage _stage = InsightsRunStage.Gathering;
    private int _stagesComplete;
    private const int StagesTotal = 7;

    public override async Task<PersistStubOutput> RunTask(OrchestrationContext context, InsightsReportOrchestrationInput input)
    {
        const int maxReflectionIterations = 2; // matches Agents:MaxReflectionIterations' documented default.

        SetStage(InsightsRunStage.Gathering);
        var gathered = await context.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", new GatherScopeInput(input.UserId, input.TenantId));

        SetStage(InsightsRunStage.Validating);
        var dimensions = await context.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", new FetchDimensionsInput(input.UserId, input.TenantId));

        SetStage(InsightsRunStage.Composing);
        var composeResult = await context.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0",
            new ComposeInput(dimensions.DimensionResults, gathered.TenantShape, input.ReportType, null, null));
        var plan = composeResult.Plan;

        for (var i = 0; i < maxReflectionIterations; i++)
        {
            var reflection = await context.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0",
                new ReflectOnCompositionInput(plan, dimensions.Assertions, dimensions.Findings, gathered.TenantShape));
            if (reflection.Result.Verdict == ReflectionVerdict.Approve)
                break;

            var revised = await context.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0",
                new ComposeInput(dimensions.DimensionResults, gathered.TenantShape, input.ReportType, plan, reflection.Result.Issues));
            plan = revised.Plan;
        }

        SetStage(InsightsRunStage.Narrating);
        var narrateResult = await context.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0",
            new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;

        for (var i = 0; i < maxReflectionIterations; i++)
        {
            var reflection = await context.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0",
                new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            if (reflection.Result.Verdict == ReflectionVerdict.Approve)
                break;

            var revised = await context.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0",
                new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrative = revised.Narrative;
        }

        SetStage(InsightsRunStage.Verifying);
        // Throws OrchestrationRefusedException on refusal - propagates out of RunTask, DTFx marks
        // the instance Failed. Nothing after this line runs on a refusal, by construction.
        await context.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0",
            new PublishGateInput(input.UserId, input.TenantId, narrative, dimensions.Assertions));

        SetStage(InsightsRunStage.Rendering);
        var renderResult = await context.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0",
            new RenderHtmlInput(plan, narrative, $"Tenant {input.TenantId}", input.ReportType, context.CurrentUtcDateTime));

        var normalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(renderResult.Html));
        var sanitized = await context.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", new SanitizeInput(normalized.Html));
        // Second normalize call: the loop-closing re-check (item 13, already built and tested) -
        // catches DOMPurify's own serialization side effects, e.g. the DOCTYPE-drop bug.
        var reNormalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(sanitized.Html));

        // Advisory only - result intentionally unused for any branching decision (spec/CLAUDE.md
        // [TRAP]: Playwright is cosmetic QA, never a security control). Item 17 (cost/observability,
        // not this slice) is where this result gets logged/alerted on instead of discarded.
        _ = await context.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", new PlaywrightQaInput(reNormalized.Html));

        SetStage(InsightsRunStage.Complete, final: true);
        return await context.ScheduleTask<PersistStubOutput>(typeof(PersistStubActivity).Name, "1.0",
            new PersistStubInput(reNormalized.Html, input.TenantId, input.ReportType));
    }

    public override string GetStatus() =>
        System.Text.Json.JsonSerializer.Serialize(new { stage = _stage.ToContractName(), stagesComplete = _stagesComplete, stagesTotal = StagesTotal });

    private void SetStage(InsightsRunStage stage, bool final = false)
    {
        _stage = stage;
        _stagesComplete = final ? StagesTotal : _stagesComplete + 1;
    }
}

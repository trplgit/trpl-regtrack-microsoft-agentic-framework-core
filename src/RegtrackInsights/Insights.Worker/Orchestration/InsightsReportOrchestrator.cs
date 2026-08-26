using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

/// <summary>
/// Wraps ReportCompositionPipeline's compose-reflect-narrate-reflect-gate-render-normalize-
/// sanitize-QA-persist sequence as a durable, crash-resumable, versioned orchestration
/// (CLAUDE.md build order item 11). Deterministic body only - every LLM call, every DB read,
/// every DateTime read lives in an activity (CLAUDE.md 6, spec 5).
/// </summary>
public sealed class InsightsReportOrchestrator : TaskOrchestration<PersistOutput, InsightsReportOrchestrationInput>
{
    public const string Name = "InsightsReportOrchestrator";

    /*  Bumped 1.0 -> 1.1 for build order item 14 (real persistence replacing PersistStubActivity) -
        RunTask's return type and its final ScheduleTask call both changed shape, which is exactly
        what CLAUDE.md 6's "orchestration versioning ON from day one" exists to protect: an in-flight
        1.0 instance replayed against this code would hit a non-determinism error otherwise. Safe to
        bump now - every 1.0 instance created so far is already terminal.

        Bumped 1.1 -> 1.2 for item 17's per-run token budget - ComposeOutput/ReflectOnComposition
        Output/NarrateOutput/ReflectOnNarrativeOutput/RenderHtmlOutput all gained a TotalTokens
        field. The ScheduleTask call SEQUENCE this method makes is unchanged (same activities, same
        order), so a bump is not strictly required by DTFx's replay check - bumped anyway, matching
        this file's own 1.0->1.1 precedent, since it costs nothing and removes any doubt. Safe now
        for the same reason: every prior instance is terminal.

        Bumped 1.2 -> 1.3 for design doc Sec.11.4 (Partial generation) - FetchDimensionsOutput
        gained FailedDimensions, and a new deterministic transform (PartialDimensionPlaceholder.
        InsertPlaceholders) now runs between the RenderHtml and Normalize ScheduleTask calls. The
        ScheduleTask call SEQUENCE is unchanged - same activities, same order - so again not
        strictly required, bumped anyway for the same reason as both prior bumps.

        [VERIFY BEFORE DEPLOY] unlike the 1.0->1.1 and 1.1->1.2 bumps, whether any 1.2 instance is
        still in-flight (Running/Pending) has not been checked this session - confirm against the
        real task hub before shipping this if that matters in the target environment.

        Bumped 1.3 -> 1.4 for design doc Sec.12.3 (per-tenant monthly circuit breaker + 80% alert)
        - a new ScheduleTask call (CheckTenantTokenBudgetActivity) now runs FIRST, before
        GatherScope, and a new try/finally wraps the rest of the body so RecordTenantTokenUsage
        Activity runs on every exit path. This DOES change the ScheduleTask call sequence (an
        extra call at the start, another at the end/on every unwind), unlike the two prior bumps -
        this one is load-bearing, not just precautionary. [VERIFY BEFORE DEPLOY] same as above:
        in-flight 1.3 instances not checked this session.                                          */
    public const string Version = "1.4";

    // KNOWN LIMITATION, not an oversight: input.Scope (entity-level sub-scoping) and input.Period
    // are used for persistence's index row (ScopeDescriptor, Period) but not threaded into the
    // dimension queries themselves. IDimensionRepository's nine GetXAsync methods take the
    // caller's FULL tenant scope (userId, customerId) with no entity-narrowing parameter, and an
    // optional `asOf` for
    // period-scoping that FetchDimensionsActivity does not currently pass through either - this
    // matches every existing manual test and ReportCompositionPipeline itself, neither of which
    // support sub-scoping today. Wiring real entity-level scope filtering and period selection is
    // out of scope for this slice; flagged here so it is a visible decision, not a silent gap.

    private InsightsRunStage _stage = InsightsRunStage.Gathering;
    private int _stagesComplete;
    private const int StagesTotal = 7;

    public override async Task<PersistOutput> RunTask(OrchestrationContext context, InsightsReportOrchestrationInput input)
    {
        const int maxReflectionIterations = 2; // matches Agents:MaxReflectionIterations' documented default.

        // Item 17's per-run token budget (design doc Sec.12.3): "a single report exceeding its
        // expected envelope aborts to the gate-refusal path rather than running away." A HARDCODED
        // constant, not read from IConfiguration - the orchestrator body must stay deterministic
        // across replay (CLAUDE.md 6), same treatment maxReflectionIterations above already gets.
        // Matches Budget:PerRunTokenCeiling's documented default.
        const long perRunTokenCeiling = 250_000;
        var runTotalTokens = 0L;

        void ChargeAndCheck(long tokens)
        {
            runTotalTokens += tokens;
            if (runTotalTokens > perRunTokenCeiling)
                throw new OrchestrationRefusedException(
                    "BUDGET_EXCEEDED",
                    "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                    internalDiagnostics: [$"Per-run token budget exceeded: {runTotalTokens} tokens billed so far, ceiling is {perRunTokenCeiling}."]);
        }

        // Node 0 - design doc Sec.12.3's per-tenant monthly circuit breaker, run FIRST and OUTSIDE
        // the try/finally below: cheapest-gate-first ordering (CLAUDE.md 4's entitlement-gate
        // precedent) - a tenant already over its monthly ceiling costs nothing further, not even a
        // scope lookup, and (since nothing has been charged yet at this point) there is nothing
        // for RecordTenantTokenUsageActivity to record on this path either.
        await context.ScheduleTask<CheckTenantTokenBudgetOutput>(typeof(CheckTenantTokenBudgetActivity).Name, "1.0",
            new CheckTenantTokenBudgetInput(input.TenantId, context.CurrentUtcDateTime));

        try
        {
            SetStage(InsightsRunStage.Gathering);
            var gathered = await context.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", new GatherScopeInput(input.UserId, input.TenantId));

            SetStage(InsightsRunStage.Validating);
            var dimensions = await context.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0", new FetchDimensionsInput(input.UserId, input.TenantId));

            SetStage(InsightsRunStage.Composing);
            var composeResult = await context.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0",
                new ComposeInput(dimensions.DimensionResults, gathered.TenantShape, input.ReportType, null, null, input.Priority));
            ChargeAndCheck(composeResult.TotalTokens);
            var plan = composeResult.Plan;

            for (var i = 0; i < maxReflectionIterations; i++)
            {
                var reflection = await context.ScheduleTask<ReflectOnCompositionOutput>(typeof(ReflectOnCompositionActivity).Name, "1.0",
                    new ReflectOnCompositionInput(plan, dimensions.Assertions, dimensions.Findings, gathered.TenantShape, input.Priority));
                ChargeAndCheck(reflection.TotalTokens);
                if (reflection.Result.Verdict == ReflectionVerdict.Approve)
                    break;

                var revised = await context.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0",
                    new ComposeInput(dimensions.DimensionResults, gathered.TenantShape, input.ReportType, plan, reflection.Result.Issues, input.Priority));
                ChargeAndCheck(revised.TotalTokens);
                plan = revised.Plan;
            }

            SetStage(InsightsRunStage.Narrating);
            var narrateResult = await context.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0",
                new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null, input.Priority));
            ChargeAndCheck(narrateResult.TotalTokens);
            var narrative = narrateResult.Narrative;

            for (var i = 0; i < maxReflectionIterations; i++)
            {
                var reflection = await context.ScheduleTask<ReflectOnNarrativeOutput>(typeof(ReflectOnNarrativeActivity).Name, "1.0",
                    new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings, input.Priority));
                ChargeAndCheck(reflection.TotalTokens);
                if (reflection.Result.Verdict == ReflectionVerdict.Approve)
                    break;

                var revised = await context.ScheduleTask<NarrateOutput>(typeof(NarrateActivity).Name, "1.0",
                    new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues, input.Priority));
                ChargeAndCheck(revised.TotalTokens);
                narrative = revised.Narrative;
            }

            SetStage(InsightsRunStage.Verifying);
            // Throws OrchestrationRefusedException on refusal - propagates out of RunTask, DTFx marks
            // the instance Failed. Nothing after this line runs on a refusal, by construction.
            await context.ScheduleTask<PublishGateOutput>(typeof(PublishGateActivity).Name, "1.0",
                new PublishGateInput(input.UserId, input.TenantId, narrative, dimensions.Assertions));

            SetStage(InsightsRunStage.Rendering);
            var renderResult = await context.ScheduleTask<RenderHtmlOutput>(typeof(RenderHtmlActivity).Name, "1.0",
                new RenderHtmlInput(plan, narrative, $"Tenant {input.TenantId}", input.ReportType, context.CurrentUtcDateTime, input.Priority));
            ChargeAndCheck(renderResult.TotalTokens);

            // Design doc Sec.11.4 (Partial generation) - a fixed, non-agent-authored placeholder for
            // each dimension FetchDimensionsActivity had to skip. Pure string transform on activity
            // OUTPUT, no I/O/clock/randomness of its own - safe directly in the orchestrator body
            // (CLAUDE.md 6). Runs BEFORE Normalize/Sanitize deliberately, so the placeholder markup is
            // validated by the exact same safety pipeline as the rest of the document.
            var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Html, dimensions.FailedDimensions);

            var normalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(html));
            var sanitized = await context.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", new SanitizeInput(normalized.Html));
            // Second normalize call: the loop-closing re-check (item 13, already built and tested) -
            // catches DOMPurify's own serialization side effects, e.g. the DOCTYPE-drop bug.
            var reNormalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(sanitized.Html));

            // Advisory only - result intentionally unused for any branching decision (spec/CLAUDE.md
            // [TRAP]: Playwright is cosmetic QA, never a security control). Item 17 (cost/observability,
            // not this slice) is where this result gets logged/alerted on instead of discarded.
            _ = await context.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", new PlaywrightQaInput(reNormalized.Html));

            SetStage(InsightsRunStage.Complete, final: true);
            return await context.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0",
                new PersistInput(reNormalized.Html, input.TenantId, input.ReportType, input.Period, input.Scope.ToDescriptor(), input.UserId));
        }
        finally
        {
            // Design doc Sec.12.3: record ACTUAL spend on every exit path - full success, a
            // publish-gate refusal, or any other exception mid-run all represent real tokens
            // already billed by the provider, which must count against the tenant's monthly
            // ceiling regardless of whether a report ultimately shipped.
            //
            // RunId is RECOMPUTED here via InsightsRunId.For - the exact same pure function
            // DurableTaskRunEnqueuer used to derive the DTFx instance id at enqueue time - rather
            // than read from context.OrchestrationInstance.InstanceId. Two reasons: (1) that
            // property is not virtual, so a Mock<OrchestrationContext> cannot stub it, which would
            // make this activity's input untestable against InsightsReportOrchestratorTests'
            // established pattern; (2) InsightsRunId.For is a pure function of fields already on
            // `input`, so recomputing it here is exactly as safe as PartialDimensionPlaceholder's
            // call above (CLAUDE.md 6) and produces an IDENTICAL value by construction.
            await context.ScheduleTask<RecordTenantTokenUsageOutput>(typeof(RecordTenantTokenUsageActivity).Name, "1.0",
                new RecordTenantTokenUsageInput(
                    input.TenantId, InsightsRunId.For(input.TenantId, input.Scope.ToDescriptor(), input.ReportType, input.Period), runTotalTokens));
        }
    }

    public override string GetStatus() =>
        System.Text.Json.JsonSerializer.Serialize(new { stage = _stage.ToContractName(), stagesComplete = _stagesComplete, stagesTotal = StagesTotal });

    private void SetStage(InsightsRunStage stage, bool final = false)
    {
        _stage = stage;
        _stagesComplete = final ? StagesTotal : _stagesComplete + 1;
    }
}

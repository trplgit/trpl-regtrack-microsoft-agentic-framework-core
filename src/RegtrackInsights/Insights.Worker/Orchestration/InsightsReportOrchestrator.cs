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
        in-flight 1.3 instances not checked this session.

        Bumped 1.4 -> 1.5 for the real self-hosted Poppins fix: a new ScheduleTask call
        (InjectFontActivity, node 8a) now runs right after PartialDimensionPlaceholder's
        transform and before the first Normalize call. Load-bearing like the 1.3->1.4 bump -
        an extra call mid-sequence, not just a payload shape change. [VERIFY BEFORE DEPLOY]
        in-flight 1.4 instances not checked this session.

        Bumped 1.5 -> 1.6 for the composite score (CompositeScoreCalculator, PROVISIONAL formula
        - not yet reviewed with the business): a new ScheduleTask call (ComputeScoreActivity,
        node 4a) now runs right after FetchDimensions and before Compose. Load-bearing, same
        reasoning as 1.3->1.4 and 1.4->1.5 - an extra call mid-sequence. [VERIFY BEFORE DEPLOY]
        in-flight 1.5 instances not checked this session - no DB connection available while this
        was written.

        Bumped 1.6 -> 1.7: RenderHtmlInput gained an Assertions field (same reasoning as the
        1.1->1.2 TotalTokens bump - the ScheduleTask call SEQUENCE is unchanged, only the payload
        shape, so not strictly required by DTFx's replay check, bumped anyway per that same
        precedent). Render can now cite real numbers directly instead of only through Narrate's
        prose - closes the same class of gap the composite-score fabrication bug (1.5->1.6) found.
        [VERIFY BEFORE DEPLOY] in-flight 1.6 instances not checked this session.

        Bumped 1.7 -> 1.8: a new ScheduleTask call (ValidateFixedHolisticStructureActivity) now
        runs right after the second Normalize call and before PlaywrightQa. Load-bearing, same
        reasoning as 1.3->1.4/1.4->1.5/1.5->1.6 - an extra call mid-sequence, not just a payload
        shape change. Closes a real gap found live: a render can silently drop score-component
        cards or ship a fabricated tab-nav badge count on a data-blocked pane (see
        FixedHolisticStructureGate's own doc comment) - both are structural invariants (CLAUDE.md
        Sec.11), so this THROWS rather than just logging for later, unlike PlaywrightQa's cosmetic-
        only stance right after it. At the time this was added, a no-op on the plain MVP template
        then live in production (05_report_html.md had no score hero, no data-blocked pane) - safe
        to add now rather than waiting for the score-hero/fixed-tab template to leave the lab.
        [CORRECTED 2026-09-01] that plain template and its render path were removed the same day
        per explicit scoping - this gate is no longer a no-op for any path, it runs on every render
        unconditionally (see ValidateFixedHolisticStructureActivity's own doc comment). [VERIFY
        BEFORE DEPLOY] in-flight 1.7 instances not checked this session.

        Bumped 1.8 -> 1.9: the RenderHtmlActivity ScheduleTask call became ScheduleWithRetry (up
        to 3 attempts, 3s/2x backoff). Load-bearing - DTFx records retry attempts in the
        orchestration history differently from a single ScheduleTask call, so this changes the
        history shape for that call, not just a payload change. Found live: a render can fail on a
        transient network/timeout blip to the LLM provider, or on a one-off malformed/truncated
        response (now thrown from MafReportHtmlAgent.RenderAsync itself, inside this same retry
        boundary - see its own [BUG FOUND LIVE] note) - neither is a reason to fail the whole run,
        both were confirmed live the same afternoon this was added. Excludes
        OrchestrationRefusedException from retry on purpose - a deterministic refusal must never be
        retried, only a render that could plausibly come back different on a fresh attempt should
        be. [VERIFY BEFORE DEPLOY] in-flight 1.8 instances not checked this session.

        Bumped 1.9 -> 2.0 (minor bump number, but the biggest structural change this file has had):
        Fixed Holistic (FixedHolisticComposition, prompts/05_report_html_fixed_holistic.md) wired
        into production, gated behind a new explicit ReportType value
        ("fixed_holistic" = FixedHolisticComposition.ReportType) - a caller opts in, the existing
        "compliance_health" dynamic path is completely unchanged for every tenant not requesting
        it. When selected: ComposeActivity/ReflectOnCompositionActivity are skipped ENTIRELY (zero
        LLM calls, zero tokens) in favour of the deterministic FixedHolisticComposition.Build()
        call; Narrate/its reflection loop and PublishGate are UNCHANGED, shared by both paths.
        RenderHtmlActivity itself stays SINGLE-agent - a second IFixedHolisticReportHtmlAgent was
        tried and removed the same day it was added: once 05_report_html.md (the old
        "compliance_health" render prompt) was deleted per explicit scoping, there was only ever
        one IReportHtmlAgent left to pick between, so the branch was dead code (see that class's
        own [REMOVED 2026-09-01] comment). This DOES change the orchestration history shape for a "fixed_holistic" run (an
        entirely different set of ScheduleTask calls happen) - a full major-version-worthy change
        in spirit, kept as a minor bump only because DTFx versions the WHOLE orchestrator, not per
        report-type, and every "compliance_health" run's own history shape is byte-for-byte
        unchanged. [VERIFY BEFORE DEPLOY] in-flight 1.9 instances not checked this session - since
        no "fixed_holistic" instance could have existed before this bump (the report type did not
        exist), only in-flight "compliance_health" runs need checking, and their own call sequence
        did not change at all.

        Bumped 2.0 -> 2.1: a new ScheduleTask call (InjectCoverageScriptActivity, node 8b) now runs
        right after InjectFontActivity and before the first Normalize call - an extra call
        mid-sequence, not just a payload shape change, same load-bearing reasoning as every prior
        mid-sequence insertion in this file. Closes a real gap found live: the Coverage-tile
        driving script was previously something the render agent had to author correctly every
        run, and three separate live failures (DOMPurify's default script strip, DOMPurify's
        defensive strip of a script whose content contained HTML-tag-shaped text, and the render
        agent simply omitting it on a given attempt) all made tiles non-clickable in a real render
        despite the grid markup itself rendering fine - see CoverageScriptInjector's own [BUG
        FOUND LIVE] note. A no-op (returns input unchanged) on any document that never rendered a
        Coverage grid at all - safe on the "compliance_health" dynamic path and on any
        fixed_holistic run before Coverage's own dimension data exists. [VERIFY BEFORE DEPLOY]
        in-flight 2.0 instances not checked this session.

        Bumped 2.1 -> 2.2: a new ScheduleTask call (InjectCoverageGridActivity, node 8b) now runs
        right after InjectFontActivity and before InjectCoverageScriptActivity (renumbered to node
        8c) - an extra call mid-sequence, not just a payload shape change, same load-bearing
        reasoning as every prior mid-sequence insertion in this file. Closes a real gap found live:
        the render agent was hand-authoring the Coverage store grid (one &lt;button&gt; tile per
        real leaf branch) and silently drew a small SAMPLE (10 of 177 for tenant 29) instead of the
        full real population, while the chip/legend/KPI numbers elsewhere in the same document
        correctly stated the true counts - see CoverageGridInjector's own [BUG FOUND LIVE] note.
        A no-op on any document that never rendered the `di-covgrid-root` placeholder at all - safe
        on the "compliance_health" dynamic path and on any run where Location degraded.
        [VERIFY BEFORE DEPLOY] in-flight 2.1 instances not checked this session.

        Bumped 2.2 -> 2.3: a new ScheduleTask call (InjectCoverageCssActivity, node 8c, renumbering
        the driving-script injection to 8d) now runs right after InjectCoverageGridActivity - an
        extra call mid-sequence, not just a payload shape change, same load-bearing reasoning as
        every prior mid-sequence insertion in this file. Closes a real gap found live: the
        Coverage pane's 4 status colours (chip swatch, tile fill, detail pill) were the one
        remaining piece asked of the render agent as "declare these rules verbatim" - a real
        render shipped tiles as unfilled outline boxes and a colourless detail pill because that
        CSS silently dropped or malformed - see CoverageCssInjector's own [BUG FOUND LIVE] note.
        A no-op on any document that never rendered a Coverage grid at all.
        [VERIFY BEFORE DEPLOY] in-flight 2.2 instances not checked this session.                   */
    public const string Version = "2.3";

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

            // Node 4a - deterministic (CLAUDE.md 6/CompositeScoreCalculator's own doc comment: an
            // agent may only CITE a score, never compute one). Folded into the SAME assertions list
            // every downstream activity already reads via `dimensions`, as ordinary typed
            // Assertion records - no new plumbing needed on Compose/Narrate's input shape.
            var scoreResult = await context.ScheduleTask<ComputeScoreOutput>(typeof(ComputeScoreActivity).Name, "1.0",
                new ComputeScoreInput(dimensions.DimensionResults));
            // Merged into BOTH: DimensionResults so ComposeActivity's own LLM input actually
            // contains it (see ComputeScoreOutput's doc comment - this was found missing live),
            // and Assertions so Reflect/Narrate/PublishGate see it too.
            var dimensionResultsWithScore = new Dictionary<string, string>(dimensions.DimensionResults) { ["Score"] = scoreResult.ScoreDimensionResultJson };
            dimensions = dimensions with
            {
                DimensionResults = dimensionResultsWithScore,
                Assertions = dimensions.Assertions.Concat(scoreResult.Assertions).ToList(),
            };

            SetStage(InsightsRunStage.Composing);
            // Fixed Holistic (ReportType "fixed_holistic") skips this entirely - structure is
            // deterministic C# (FixedHolisticComposition.Build), not an LLM decision, by design
            // (see that class's own doc comment: "more strictly than CLAUDE.md's non-negotiable #1
            // already requires... there is no judgement call to make at all"). Pure function, no
            // I/O/clock/randomness of its own - safe directly in the orchestrator body, same as
            // PartialDimensionPlaceholder elsewhere in this file. Zero tokens spent on composition
            // for this path - nothing to ChargeAndCheck.
            CompositionPlan plan;
            if (input.ReportType == FixedHolisticComposition.ReportType)
            {
                plan = FixedHolisticComposition.Build();
            }
            else
            {
                var composeResult = await context.ScheduleTask<ComposeOutput>(typeof(ComposeActivity).Name, "1.0",
                    new ComposeInput(dimensions.DimensionResults, gathered.TenantShape, input.ReportType, null, null, input.Priority));
                ChargeAndCheck(composeResult.TotalTokens);
                plan = composeResult.Plan;

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

            // [DELIBERATE, SCOPED EXCEPTION - see IReportHtmlAgent.RenderAsync's locationRows doc
            // comment] Pure deserialization of already-fetched activity output, no I/O/clock/
            // randomness - safe directly in the orchestrator body, same as PartialDimensionPlaceholder
            // above. Null when Location degraded (dimensions.DimensionResults has no "Location" key).
            var locationRows = dimensions.DimensionResults.TryGetValue("Location", out var locationJson)
                ? System.Text.Json.JsonSerializer.Deserialize<DimensionResult<LocationControlTotals, LocationRow>>(locationJson)?.Rows
                : null;

            SetStage(InsightsRunStage.Rendering);
            // [BUG FOUND LIVE, 2026-09-01] Render is the one call in this whole pipeline with two
            // real reasons to legitimately fail on a good input: a transient network/timeout blip
            // to the LLM provider, and a one-off malformed/truncated response (MafReportHtmlAgent.
            // RenderAsync now throws InvalidOperationException on that, BEFORE returning, per its
            // own [BUG FOUND LIVE] note - specifically so it surfaces HERE, inside this retry, not
            // one activity later where a retry can no longer reach it). ScheduleWithRetry is DTFx's
            // own durable retry primitive - durable because it survives a worker crash/restart
            // mid-retry, unlike a hand-rolled loop; "durable" is also why this is safe to do in an
            // orchestrator body at all (CLAUDE.md 6 - no real I/O of its own, just re-scheduling
            // the same activity). Handle excludes OrchestrationRefusedException on purpose: that is
            // a DETERMINISTIC refusal (Normalize/Sanitize/Structure/PublishGate) - same input
            // always fails the same way, so retrying it would only burn real LLM tokens chasing a
            // result that cannot change. RenderHtmlActivity itself never throws that today; this
            // filter is what keeps it true if that ever changes.
            var renderResult = await context.ScheduleWithRetry<RenderHtmlOutput>(
                typeof(RenderHtmlActivity).Name, "1.0",
                new RetryOptions(TimeSpan.FromSeconds(3), maxNumberOfAttempts: 3)
                {
                    BackoffCoefficient = 2.0,
                    Handle = ex => ex is not OrchestrationRefusedException,
                },
                new RenderHtmlInput(plan, narrative, dimensions.Assertions, $"Tenant {input.TenantId}", input.ReportType, context.CurrentUtcDateTime, input.Priority, locationRows));
            ChargeAndCheck(renderResult.TotalTokens);

            // Design doc Sec.11.4 (Partial generation) - a fixed, non-agent-authored placeholder for
            // each dimension FetchDimensionsActivity had to skip. Pure string transform on activity
            // OUTPUT, no I/O/clock/randomness of its own - safe directly in the orchestrator body
            // (CLAUDE.md 6). Runs BEFORE Normalize/Sanitize deliberately, so the placeholder markup is
            // validated by the exact same safety pipeline as the rest of the document.
            var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Html, dimensions.FailedDimensions);

            // Node 8a - embeds the REAL vendored Poppins font (vendor/README.md) as a self-hosted
            // @font-face, deterministically, after the LLM but before anything validates or ships
            // the document. Runs BEFORE Normalize on purpose, same reasoning as the placeholder
            // insertion above: the injected markup goes through the exact same safety pipeline as
            // everything else, not a carve-out. See PoppinsFontInjector's doc comment for why this
            // can't be the render agent's own job.
            var fonted = await context.ScheduleTask<InjectFontOutput>(typeof(InjectFontActivity).Name, "1.0", new InjectFontInput(html));

            // Node 8b - same deterministic-injection treatment as the font above. CoverageGridInjector's
            // own [BUG FOUND LIVE] note: the render agent was asked to hand-author one tile per real
            // leaf branch (up to 177 for tenant 29) and silently sampled instead of completing the
            // population while still stating the true full counts elsewhere in the same document -
            // removed from the render agent's job entirely, generated deterministically from the
            // real locationRows this orchestrator already deserialized above. No-op (returns input
            // unchanged) when the render agent's own `di-covgrid-root` placeholder is missing, or
            // when Location degraded - never forces a grid in.
            var coverageGridded = await context.ScheduleTask<InjectCoverageGridOutput>(
                typeof(InjectCoverageGridActivity).Name, "1.0", new InjectCoverageGridInput(fonted.Html, locationRows));

            // Node 8c - same deterministic-injection treatment. CoverageCssInjector's own [BUG
            // FOUND LIVE] note: tiles rendered as unfilled outline boxes and the detail pill had
            // no colour at all because the render agent's own "declare these rules verbatim" CSS
            // silently dropped or malformed the colour declarations - this CSS is 100% static, no
            // reason to gamble on it being copied correctly every run. Inserted last inside
            // </head> so it wins any cascade conflict with whatever the render agent's own
            // <style> block still contains.
            var coverageStyled = await context.ScheduleTask<InjectCoverageCssOutput>(
                typeof(InjectCoverageCssActivity).Name, "1.0", new InjectCoverageCssInput(coverageGridded.Html));

            // Node 8d - same deterministic-injection treatment, for the exact same reason:
            // CoverageScriptInjector's own [BUG FOUND LIVE] note documents three real ways the
            // render agent authoring this itself went wrong (DOMPurify's default script strip,
            // DOMPurify's tag-shaped-content strip, and simply omitting it on a given attempt) -
            // the script's logic never varies by tenant, only the data-* attributes on the tiles
            // it reads (now themselves deterministically injected by node 8b, immediately above).
            // No-op when no Coverage grid was rendered at all - never forces this in.
            var coverageScripted = await context.ScheduleTask<InjectCoverageScriptOutput>(
                typeof(InjectCoverageScriptActivity).Name, "1.0", new InjectCoverageScriptInput(coverageStyled.Html));

            var normalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(coverageScripted.Html));
            var sanitized = await context.ScheduleTask<SanitizeOutput>(typeof(SanitizeActivity).Name, "1.0", new SanitizeInput(normalized.Html));
            // Second normalize call: the loop-closing re-check (item 13, already built and tested) -
            // catches DOMPurify's own serialization side effects, e.g. the DOCTYPE-drop bug.
            var reNormalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(sanitized.Html));

            // Structural invariant gate (CLAUDE.md Sec.11), not cosmetic QA - throws
            // OrchestrationRefusedException on the actual persisted HTML if the score-component
            // count or a blocked-tab badge is wrong (FixedHolisticStructureGate's own doc comment
            // names the two live bugs this closes). Runs BEFORE PlaywrightQa deliberately: unlike
            // PlaywrightQa's advisory-only result, a violation here must refuse publish, not just
            // get logged for later.
            var structureChecked = await context.ScheduleTask<ValidateFixedHolisticStructureOutput>(
                typeof(ValidateFixedHolisticStructureActivity).Name, "1.0", new ValidateFixedHolisticStructureInput(reNormalized.Html));

            // Advisory only - result intentionally unused for any branching decision (spec/CLAUDE.md
            // [TRAP]: Playwright is cosmetic QA, never a security control). Item 17 (cost/observability,
            // not this slice) is where this result gets logged/alerted on instead of discarded.
            _ = await context.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", new PlaywrightQaInput(structureChecked.Html));

            SetStage(InsightsRunStage.Complete, final: true);
            return await context.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0",
                new PersistInput(structureChecked.Html, input.TenantId, input.ReportType, input.Period, input.Scope.ToDescriptor(), input.UserId));
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

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
        [VERIFY BEFORE DEPLOY] in-flight 2.2 instances not checked this session.

        Bumped 2.3 -> 2.4: the render-through-validate sequence (RenderHtmlActivity through
        ValidateFixedHolisticStructureActivity) is now wrapped in a bounded retry loop (up to 3
        attempts) that re-renders from scratch on a FIXED_HOLISTIC_STRUCTURE_INVALID refusal, instead
        of failing the whole run on the first one. Load-bearing - the ScheduleTask call sequence for
        any run that needed more than one render attempt is now longer than a 2.3 instance's history
        could replay against. Confirmed live on tenant 29: FixedHolisticStructureGate's own [BUG
        FOUND LIVE] Coverage-grid check (documented "found live twice" there) failed two consecutive
        manual runs with the identical violation before this fix - a per-call rendering miss, not a
        fixed-input defect, so a fresh render attempt is a legitimate retry here (see this loop's own
        doc comment). [VERIFY BEFORE DEPLOY] in-flight 2.3 instances not checked this session.

        Bumped 2.4 -> 2.5: new ReportType "dimension_selection" (DimensionSelectionComposition) -
        a caller-picked subset of dimensions, one pane each, no fixed 6-tab hero, no composite
        score. Load-bearing for that report type specifically: ComputeScoreActivity's ScheduleTask
        call is now conditionally SKIPPED (not just given different input) when
        ReportType == "dimension_selection", a real call-sequence change, not just a payload-shape
        one. Zero effect on any existing "fixed_holistic" or "compliance_health" instance - both
        keep calling ComputeScoreActivity exactly as before, so no in-flight instance of either type
        needs checking before this deploys. [VERIFY BEFORE DEPLOY] no "dimension_selection" instance
        could have existed before this bump (the report type did not exist), so nothing of that type
        needs checking either - first bump of this file where a new ReportType branch adds zero
        replay risk by construction.

        Bumped 2.5 -> 2.6: RenderHtmlInput gained DimensionRowsJson - payload-shape-only change,
        same reasoning as the 1.1->1.2 TotalTokens bump (the ScheduleTask call SEQUENCE is
        unchanged, only the payload shape, so not strictly required by DTFx's replay check, bumped
        anyway per that precedent). Fixes a real gap found live the same day as this fix: the render
        agent for ReportType "dimension_selection" was only ever given `assertions` (curated,
        top-5-capped) and never a dimension's complete real row set, so its own prompt instruction
        to "include a table of real rows" kept getting skipped - not a wording problem, a missing-
        data problem. [VERIFY BEFORE DEPLOY] in-flight 2.5 instances not checked this session.

        Bumped 2.6 -> 2.7: RenderHtmlInput gained DimensionControlTotalsJson - payload-shape-only
        change, same precedent as the 2.5->2.6 bump immediately above. Fixes the mirror-image gap:
        a dimension-specific render prompt (05_report_html_dimension_selection_department.md) asked
        for real tenant-level aggregates (the untagged/unassigned bucket's size) that live ONLY on
        a dimension's ControlTotals result set - never a named assertion, never derivable from Rows
        (confirmed live: the render agent correctly refused rather than fabricate a number it did
        not have - "I will not infer or invent those values from narrative prose or incomplete
        data"). [VERIFY BEFORE DEPLOY] in-flight 2.6 instances not checked this session.

        Bumped 2.7 -> 2.8: ValidateFixedHolisticStructureInput gained ReportType - payload-shape-only
        change, same precedent as 2.5->2.6/2.6->2.7. Fixes a real refusal found live the same day:
        ValidateFixedHolisticStructureActivity now SKIPS its evaluation entirely for any ReportType
        other than "fixed_holistic" (previously ran unconditionally on every render's HTML string,
        with no way to tell a real fixed_holistic score hero apart from a per-dimension prompt's
        legitimate, unrelated reuse of the same ".di-components" class - see
        FixedHolisticStructureGate's own corrected doc comment). [VERIFY BEFORE DEPLOY] in-flight 2.7
        instances not checked this session.

        Bumped 2.8 -> 2.9: a "dimension_selection" request naming exactly ["Entity"] is now
        translated to a plain "fixed_holistic" request (ReportType and RequestedDimensions both
        rewritten) in the very first lines of RunTask, before anything else reads either field.
        Load-bearing, NOT just a payload-shape change - every ScheduleTask call this run makes
        from that point on is the real fixed_holistic sequence (ComputeScoreActivity now runs
        instead of being skipped, Coverage-grid injectors receive real Location rows instead of
        null, ValidateFixedHolisticStructureActivity actually evaluates instead of no-op'ing,
        composition is FixedHolisticComposition.Build() instead of
        DimensionSelectionComposition.Build()) - the call sequence for an Entity-named
        dimension_selection request is now materially longer and different than a 2.8 instance's
        history would replay against. Real product parity, not a stylistic choice: the real
        Angular app has no dedicated Entity branch either (confirmed by reading
        detailed-insights.component.html/.ts) - Entity alone already meant "the full holistic
        view" there. [VERIFY BEFORE DEPLOY] no dimension_selection request naming exactly
        ["Entity"] could have taken any other path before this bump (the translation did not
        exist), so no in-flight instance of that specific shape needs checking; every other
        dimension_selection shape and every fixed_holistic instance is completely unaffected by
        this bump.

        Bumped 2.9 -> 3.0: a new ScheduleTask call (InjectBacklogAgeBarActivity, node 8e) now runs
        right after InjectCoverageScriptActivity and before the first Normalize call - an extra
        call mid-sequence, not just a payload shape change, same load-bearing reasoning as every
        prior mid-sequence insertion in this file. Closes a real gap found live: the segmented
        backlog age-bar (Tab 2 Card 2) was silently skipped in favour of the bigNumber-only
        fallback on two consecutive real tenant-29 runs, even with real BacklogAging bucket data
        available both times - same failure family as the Coverage-grid fix already in this file,
        now closed the same way (deterministic post-render injection instead of asking the render
        agent to author it). [VERIFY BEFORE DEPLOY] in-flight 2.9 instances not checked this
        session.

        Bumped 3.0 -> 3.1: a new ScheduleTask call (InjectBacklogAgeBarCssActivity, node 8f) now
        runs right after InjectBacklogAgeBarActivity and before the first Normalize call - same
        load-bearing reasoning as every prior mid-sequence insertion in this file. Closes a real
        gap found live: node 8e's deterministically-injected age-bar markup had no CSS anywhere in
        the whole pipeline (not in the render agent's prompt instructions, not injected by any
        activity), so it rendered with real bucket counts and real segment widths but zero colour
        and no bar chrome - same failure family as the Coverage CSS fix (node 8c) already in this
        file, closed the same way. [VERIFY BEFORE DEPLOY] in-flight 3.0 instances not checked this
        session.

        Bumped 3.1 -> 3.2: FetchDimensionsActivity now fetches a fifteenth dimension (ForwardRisk,
        usp_Insights_Dimension_ForwardRisk / sql/26 - already deployed and live in prod, not
        modified) - an extra SQL call inside that activity, plus a new ScheduleTask
        (InjectForwardLookActivity, node 8g) right after InjectBacklogAgeBarCssActivity. Wires the
        real carried_forward / clean_at_risk / healthy segment counts (the "already late today"
        figure the live demo shows) into Tab 5, deterministically - those counts are reconciled by
        the proc but carry no typed assertion, so they cannot travel through the render agent's
        assertion-only payload. [VERIFY BEFORE DEPLOY] in-flight 3.1 instances not checked this
        session.

        Bumped 3.2 -> 3.3: FetchDimensionsInput gained optional WindowStart/WindowEnd, and
        FetchDimensionsActivity now passes a concrete [start, end) window to
        GetTimelinessFYAsync / GetEvidenceIntegrityAsync - the deployed sql/23 + sql/25 procs now
        REQUIRE @WindowStart/@WindowEnd (they THROW 51177 / 51178 on NULL). Payload-shape-only for
        the orchestrator (same ScheduleTask sequence); the new fields default to null and the
        activity falls back to CURRENT-FY-TO-DATE, preserving today's numbers. **COORDINATED
        RELEASE**: this worker build and the sql/23 + sql/25 deploy must ship together - deploy
        either alone and TimelinessFY + EvidenceIntegrity fetch fails (degrades to a placeholder,
        report still ships) until both are in place. [VERIFY BEFORE DEPLOY] in-flight 3.2 instances
        not checked this session.

        Bumped 3.3 -> 3.4: InjectForwardLookActivity (node 8g) now also receives ForwardPipeline's
        control totals + rows (the 5 day-window bucket counts), and ForwardLookInjector renders
        the ENTIRE Tab 5 pane body deterministically - the `.di-kpi--fwd` card, the due figure,
        the segment breakdown AND the bucket chart. Was rendering only the 3-segment breakdown;
        the render agent kept dropping the rest of the pane on real tenants. Payload-shape change
        to InjectForwardLookInput (two new nullable fields); same ScheduleTask sequence.

        Bumped 3.4 -> 3.5: new ScheduleTask call (InjectForwardLookCssActivity, node 8h) right
        after node 8g. Once the render agent only authors the Tab 5 shell it stopped emitting the
        Tab 5 style block, so the injected `.di-fwd` bucket chart rendered with zero height. The
        CSS is 100% static - injected markup needs injected CSS, same as the age bar (8f) and
        Coverage. New call-sequence node.                                                          */
    public const string Version = "3.5";

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
        // [ADDED 2026-09-09] "Entity" special case, decided explicitly: the real Angular product
        // has no dedicated per-dimension branch for Entity at all (confirmed by reading
        // detailed-insights.component.html/.ts directly - only 'User' and 'Department' get one;
        // dimensions = ['Entity', 'User', 'Department', 'Location', 'Statutory', 'Internal'] but
        // Entity itself just falls through to the plain full holistic view). A
        // "dimension_selection" request naming Entity alone is therefore translated, right here,
        // into a plain "fixed_holistic" request before anything else reads ReportType or
        // RequestedDimensions - every other branch in this file (composition, dimension fetch
        // scope, ComputeScoreActivity's skip condition, the render-agent key lookup, the
        // dimension_rows/dimension_control_totals threading) already keys off those two fields
        // consistently, so this single rewrite is the whole fix - no other line needs to know
        // Entity was ever mentioned. Sambram never built a per-dimension Entity template for this
        // reason - the real product doesn't have one to model it on.
        if (input.ReportType == DimensionSelectionComposition.ReportType
            && input.RequestedDimensions is ["Entity"])
        {
            input = input with { ReportType = FixedHolisticComposition.ReportType, RequestedDimensions = null };
        }

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
            // [ADDED 2026-09-08] Fail closed rather than silently falling back to "all fourteen
            // dimensions" if a caller requests dimension_selection without actually naming any -
            // CLAUDE.md non-negotiable #2. Checked before Gathering, same "cheapest gate first"
            // ordering as the token-budget/entitlement gates above.
            if (input.ReportType == DimensionSelectionComposition.ReportType
                && input.RequestedDimensions is not { Count: > 0 })
            {
                throw new OrchestrationRefusedException(
                    "NO_DIMENSIONS_REQUESTED",
                    "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                    internalDiagnostics: ["ReportType 'dimension_selection' requires a non-empty RequestedDimensions list."]);
            }

            SetStage(InsightsRunStage.Gathering);
            var gathered = await context.ScheduleTask<GatherScopeOutput>(typeof(GatherScopeActivity).Name, "1.0", new GatherScopeInput(input.UserId, input.TenantId));

            SetStage(InsightsRunStage.Validating);
            var dimensions = await context.ScheduleTask<FetchDimensionsOutput>(typeof(FetchDimensionsActivity).Name, "1.0",
                new FetchDimensionsInput(input.UserId, input.TenantId, input.RequestedDimensions));

            // [ADDED 2026-09-08] The composite score is a whole-tenant, fixed-holistic concept
            // (CompositeScoreCalculator throws if literally no component can be computed at all) -
            // not something that means anything for an arbitrary caller-picked subset of
            // dimensions, and a subset that happens to map to zero scoring components would refuse
            // the whole run over a score nobody asked to see. Skipped entirely for
            // dimension_selection, same "skip what this report type does not need" precedent
            // FixedHolisticComposition's own branch below already sets for ComposeActivity.
            if (input.ReportType != DimensionSelectionComposition.ReportType)
            {
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
            }

            SetStage(InsightsRunStage.Composing);
            // Fixed Holistic (ReportType "fixed_holistic") and DimensionSelection (ReportType
            // "dimension_selection") both skip real composition - structure is deterministic C#
            // (FixedHolisticComposition.Build / DimensionSelectionComposition.Build), not an LLM
            // decision, by design (see either class's own doc comment: there is no judgement call
            // to make once either "always these six tabs" or "exactly the dimensions the caller
            // named" is already decided). Pure functions, no I/O/clock/randomness of their own -
            // safe directly in the orchestrator body, same as PartialDimensionPlaceholder elsewhere
            // in this file. Zero tokens spent on composition for either path - nothing to
            // ChargeAndCheck.
            CompositionPlan plan;
            if (input.ReportType == FixedHolisticComposition.ReportType)
            {
                plan = FixedHolisticComposition.Build();
            }
            else if (input.ReportType == DimensionSelectionComposition.ReportType)
            {
                plan = DimensionSelectionComposition.Build(input.RequestedDimensions!);
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

            // [ADDED 2026-09-09] Same deliberate, scoped exception as locationRows immediately
            // above - feeds InjectBacklogAgeBarActivity (node 8e below). Null when BacklogAging
            // degraded (dimensions.DimensionResults has no "BacklogAging" key) - fixed_holistic is
            // the only ReportType this dimension is ever fetched for today, so it is null for
            // every other ReportType, matching locationRows' own real behaviour for
            // dimension_selection requests that never asked for Location either.
            var backlogAgingResult = dimensions.DimensionResults.TryGetValue("BacklogAging", out var backlogAgingJson)
                ? System.Text.Json.JsonSerializer.Deserialize<DimensionResult<BacklogAgingControlTotals, BacklogAgingRow>>(backlogAgingJson)
                : null;

            // [ADDED 2026-09-10] Same deliberate, scoped extraction as backlogAgingResult above -
            // feeds InjectForwardLookActivity (node 8g). Null when ForwardRisk degraded or was not
            // fetched (fixed_holistic is the only ReportType that fetches it today). The proc
            // (sql/26) is deployed and live in prod; nothing here modifies it.
            var forwardRiskResult = dimensions.DimensionResults.TryGetValue("ForwardRisk", out var forwardRiskJson)
                ? System.Text.Json.JsonSerializer.Deserialize<DimensionResult<ForwardRiskControlTotals, ForwardRiskRow>>(forwardRiskJson)
                : null;

            // [ADDED 2026-09-10] The 5 day-window bucket counts for Tab 5's chart - also injected
            // now (the render agent was dropping the whole forward pane). Same scoped extraction.
            var forwardPipelineResult = dimensions.DimensionResults.TryGetValue("ForwardPipeline", out var forwardPipelineJson)
                ? System.Text.Json.JsonSerializer.Deserialize<DimensionResult<ForwardPipelineControlTotals, ForwardPipelineRow>>(forwardPipelineJson)
                : null;

            // [ADDED 2026-09-08] Only meaningful for DimensionSelectionComposition.ReportType - the
            // render agent's own payload only ever carried `assertions` (curated, top-5-capped
            // comparative facts), never a dimension's full per-member row set, so asking it to
            // render "a table of all real rows" for a dimension_selection report was asking for
            // data it was never actually given (see IReportHtmlAgent.RenderAsync's own doc comment
            // on this parameter - found live the same day as this fix, tenant 29, Nature). Generic
            // JSON extraction, not dimension-shape-specific: every dimension's own serialized
            // DimensionResult already carries a "Rows" property (PascalCase - FetchDimensionsActivity's
            // own plain JsonSerializer.Serialize call, no naming policy applied), pulled out here as
            // a raw JSON array string per requested dimension. Pure JSON parsing of already-fetched
            // activity output, no I/O of its own - same "safe directly in the orchestrator body"
            // reasoning as locationRows immediately above.
            IReadOnlyDictionary<string, string>? dimensionRowsJson = input.ReportType == DimensionSelectionComposition.ReportType
                ? dimensions.DimensionResults
                    .Where(kv => input.RequestedDimensions!.Contains(kv.Key))
                    .ToDictionary(
                        kv => kv.Key,
                        kv => System.Text.Json.JsonDocument.Parse(kv.Value).RootElement.GetProperty("Rows").GetRawText())
                : null;

            // [ADDED 2026-09-09] Same shape as dimensionRowsJson immediately above, one property
            // name different ("ControlTotals" instead of "Rows") - see IReportHtmlAgent.RenderAsync's
            // own doc comment on dimensionControlTotalsJson for the real gap this closes (tenant-
            // level aggregates, e.g. Departments' untagged/unassigned bucket size, exist ONLY on a
            // dimension's ControlTotals, never as a named assertion and never derivable from Rows).
            IReadOnlyDictionary<string, string>? dimensionControlTotalsJson = input.ReportType == DimensionSelectionComposition.ReportType
                ? dimensions.DimensionResults
                    .Where(kv => input.RequestedDimensions!.Contains(kv.Key))
                    .ToDictionary(
                        kv => kv.Key,
                        kv => System.Text.Json.JsonDocument.Parse(kv.Value).RootElement.GetProperty("ControlTotals").GetRawText())
                : null;

            SetStage(InsightsRunStage.Rendering);

            // [FIX 2026-09-07] FixedHolisticStructureGate's own doc comment (item 3) documents this
            // exact failure "found live twice": the render agent sometimes ignores the Coverage
            // pane's scaffold instruction and writes a plain summary card instead of leaving
            // `di-covgrid-root` in place - a prose-instruction miss, same failure family as its
            // item 1 (score-component count), NOT a fixed-input structural defect. Confirmed live a
            // third time on tenant 29 (two consecutive runs, identical violation). The prior
            // ScheduleWithRetry comment below claims every OrchestrationRefusedException past this
            // point is "a DETERMINISTIC refusal - same input always fails the same way" - that is
            // true for NOT_NORMALIZABLE/POST_SANITIZE_VIOLATION/GATE_REFUSED (those check the
            // document's real data/safety shape), but FALSE for FIXED_HOLISTIC_STRUCTURE_INVALID's
            // Coverage-grid check specifically: its input (the render agent's own free-form output)
            // is exactly what a fresh render attempt can plausibly change. Retrying the WHOLE
            // render-through-validate chain (not just RenderHtmlActivity, since a fresh render needs
            // fresh font/coverage injection and re-normalization too) up to maxRenderAttempts times
            // gives the render agent additional real chances to follow the scaffold before this
            // becomes a genuine refusal. Every attempt bills real tokens via ChargeAndCheck, same as
            // the narrate/compose reflection loops - this is not a free retry.
            const int maxRenderAttempts = 3;
            ValidateFixedHolisticStructureOutput? structureChecked = null;

            for (var renderAttempt = 1; renderAttempt <= maxRenderAttempts; renderAttempt++)
            {
                // [BUG FOUND LIVE, 2026-09-01] Render is the one call in this whole pipeline with
                // two real reasons to legitimately fail on a good input: a transient network/timeout
                // blip to the LLM provider, and a one-off malformed/truncated response
                // (MafReportHtmlAgent.RenderAsync now throws InvalidOperationException on that,
                // BEFORE returning, per its own [BUG FOUND LIVE] note - specifically so it surfaces
                // HERE, inside this retry, not one activity later where a retry can no longer reach
                // it). ScheduleWithRetry is DTFx's own durable retry primitive - durable because it
                // survives a worker crash/restart mid-retry, unlike a hand-rolled loop; "durable" is
                // also why this is safe to do in an orchestrator body at all (CLAUDE.md 6 - no real
                // I/O of its own, just re-scheduling the same activity). Handle excludes
                // OrchestrationRefusedException on purpose - RenderHtmlActivity itself never throws
                // that today; this filter is what keeps it true if that ever changes. The OUTER
                // renderAttempt loop above is the mechanism for the structure-gate retry described
                // above; this INNER ScheduleWithRetry is unchanged and still only ever sees
                // RenderHtmlActivity's own transient/malformed-response failures.
                var renderResult = await context.ScheduleWithRetry<RenderHtmlOutput>(
                    typeof(RenderHtmlActivity).Name, "1.0",
                    new RetryOptions(TimeSpan.FromSeconds(3), maxNumberOfAttempts: 3)
                    {
                        BackoffCoefficient = 2.0,
                        Handle = ex => ex is not OrchestrationRefusedException,
                    },
                    new RenderHtmlInput(plan, narrative, dimensions.Assertions, $"Tenant {input.TenantId}", input.ReportType, context.CurrentUtcDateTime, input.Priority, locationRows, dimensionRowsJson, dimensionControlTotalsJson));
                ChargeAndCheck(renderResult.TotalTokens);

                // Design doc Sec.11.4 (Partial generation) - a fixed, non-agent-authored placeholder
                // for each dimension FetchDimensionsActivity had to skip. Pure string transform on
                // activity OUTPUT, no I/O/clock/randomness of its own - safe directly in the
                // orchestrator body (CLAUDE.md 6). Runs BEFORE Normalize/Sanitize deliberately, so
                // the placeholder markup is validated by the exact same safety pipeline as the rest
                // of the document.
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
                // when Location degraded - never forces a grid in. [FIX 2026-09-07] That "no-op when
                // missing" behaviour is exactly the gap the outer renderAttempt loop now closes: a
                // missing placeholder used to reach ValidateFixedHolisticStructureActivity and refuse
                // the whole run; now it triggers a fresh render attempt instead, up to the cap above.
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

                // Node 8e - same deterministic-injection treatment, a completely different part of
                // the document from the Coverage pieces above (Tab 2 Card 2's age-bar, not Tab 3).
                // BacklogAgeBarInjector's own [BUG FOUND LIVE] note: confirmed on two consecutive
                // real tenant-29 runs, the render agent silently fell back to the bigNumber-only
                // shape and never rendered the segmented bar, even with real bucket data available.
                // No-op when the render agent's own `di-agebar-root` placeholder is missing (or
                // BacklogAging degraded, or a real zero-overdue tenant) - never forces a bar in.
                var agebarred = await context.ScheduleTask<InjectBacklogAgeBarOutput>(
                    typeof(InjectBacklogAgeBarActivity).Name, "1.0",
                    new InjectBacklogAgeBarInput(coverageScripted.Html, backlogAgingResult?.Rows, backlogAgingResult?.ControlTotals));

                // Node 8f - same deterministic-injection treatment as node 8c, for the piece node 8e
                // just injected. BacklogAgeBarCssInjector's own [BUG FOUND LIVE] note: the age-bar's
                // markup was already deterministic (node 8e), but its CSS was never declared anywhere
                // - not in the render agent's prompt, not by any injector - so it rendered with real
                // bucket counts and real widths but zero colour and no bar chrome. No-op when node 8e
                // injected nothing (blocked/degraded/zero-overdue this run).
                var agebarStyled = await context.ScheduleTask<InjectBacklogAgeBarCssOutput>(
                    typeof(InjectBacklogAgeBarCssActivity).Name, "1.0", new InjectBacklogAgeBarCssInput(agebarred.Html));

                // Node 8g - renders the WHOLE Tab 5 pane body: the `.di-kpi--fwd` card, the "N due
                // in the next 90 days" figure + carried_forward / clean_at_risk / healthy segment
                // breakdown (from ForwardRisk's control totals, sql/26, reconciled by the proc),
                // AND the 5 day-window bucket chart (from ForwardPipeline's rows, sql/24). None of
                // those are typed assertions, so none can round-trip through the render agent's
                // assertion-only payload - same deterministic-injection reason as the Coverage pane
                // and the age-bar above. [WIDENED 2026-09-10] Was just the segment breakdown; the
                // render agent kept dropping the rest of the pane. No-op when the `di-forward-root`
                // placeholder is missing; head-only pane when both sources degraded/empty.
                var forwarded = await context.ScheduleTask<InjectForwardLookOutput>(
                    typeof(InjectForwardLookActivity).Name, "1.0",
                    new InjectForwardLookInput(
                        agebarStyled.Html,
                        forwardRiskResult?.ControlTotals,
                        forwardPipelineResult?.ControlTotals,
                        forwardPipelineResult?.Rows));

                // Node 8h - the CSS for the Tab 5 card ForwardLookActivity just injected. The
                // render agent stopped emitting the Tab 5 style block once it only authors the
                // shell, so the injected `.di-fwd` chart had no height and its axis ran together.
                // Injected markup needs injected CSS. No-op when no forward card rendered.
                var forwardStyled = await context.ScheduleTask<InjectForwardLookCssOutput>(
                    typeof(InjectForwardLookCssActivity).Name, "1.0", new InjectForwardLookCssInput(forwarded.Html));

                var normalized = await context.ScheduleTask<NormalizeOutput>(typeof(NormalizeActivity).Name, "1.0", new NormalizeInput(forwardStyled.Html));
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
                //
                // [BUG FOUND LIVE, 2026-09-07] The catch below used to filter on
                // `catch (OrchestrationRefusedException ex) when (ex.ReasonCode == "...")` - that
                // NEVER matched. DurableTask.Core's plain `ScheduleTask<T>` (unlike ScheduleWithRetry,
                // whose own `Handle` callback DOES receive the original exception) rethrows activity
                // failures as `TaskFailedException` once replayed from persisted history - which
                // happens on every orchestrator replay pass, confirmed live via this exact bug: the
                // custom `OrchestrationRefusedException` type, and with it `ReasonCode`/
                // `InternalDiagnostics`, never survives that boundary, so the filtered catch silently
                // never fired and every run still failed after exactly one render attempt despite this
                // loop's cap being 3. Catching plain `Exception` instead - scoped to ONLY this one
                // ScheduleTask call, so any exception escaping it is by construction this activity's
                // own refusal, without needing to identify its original type - fixes this without
                // depending on anything DTFx does or does not preserve across replay.
                try
                {
                    structureChecked = await context.ScheduleTask<ValidateFixedHolisticStructureOutput>(
                        typeof(ValidateFixedHolisticStructureActivity).Name, "1.0", new ValidateFixedHolisticStructureInput(reNormalized.Html, input.ReportType));
                    break;
                }
                catch (Exception) when (renderAttempt < maxRenderAttempts)
                {
                    // Re-render-able per this loop's own doc comment above - fall through to the next
                    // iteration for a fresh render attempt rather than refusing the whole run on what
                    // is really a per-call sampling miss, not a fixed-input defect. On the LAST attempt
                    // the `when` guard above is false, so the exception is NOT caught here at all and
                    // propagates normally - same terminal behaviour as before this loop existed.
                }
            }

            // Unreachable with structureChecked null: the loop above either breaks with a value, or
            // its last iteration's catch guard (renderAttempt < maxRenderAttempts) is false, so the
            // exception on that final attempt propagates out of the loop instead of being caught.
            var finalStructureChecked = structureChecked!;

            // Advisory only - result intentionally unused for any branching decision (spec/CLAUDE.md
            // [TRAP]: Playwright is cosmetic QA, never a security control). Item 17 (cost/observability,
            // not this slice) is where this result gets logged/alerted on instead of discarded.
            _ = await context.ScheduleTask<PlaywrightQaOutput>(typeof(PlaywrightQaActivity).Name, "1.0", new PlaywrightQaInput(finalStructureChecked.Html));

            SetStage(InsightsRunStage.Complete, final: true);
            return await context.ScheduleTask<PersistOutput>(typeof(PersistActivity).Name, "1.0",
                new PersistInput(finalStructureChecked.Html, input.TenantId, input.ReportType, input.Period, input.Scope.ToDescriptor(), input.UserId));
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

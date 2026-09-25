#pragma warning disable OPENAI001 // ResponseReasoningEffortLevel - experimental in this SDK version, same pragma MafAgentFactory already carries.

using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using OpenAI.Responses;

namespace Insights.Worker.Orchestration;

/// <summary>
/// Registers the paid-tier agents (compose, reflect x2, narrate, render) and the shared
/// deterministic presentation pieces (browser, sanitizer, QA runner). Every value read eagerly
/// from configuration at registration time, matching FreeDigestRegistration's established
/// pattern - a missing key must throw while the host starts, not on the first real report.
/// </summary>
public static class PaidReportAgentsRegistration
{
    public static IServiceCollection AddInsightsPaidReportAgents(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = Require(configuration, "Llm:Maf:Endpoint");
        var model = Require(configuration, "Llm:Maf:Model");
        var apiKey = Require(configuration, "Llm:Maf:ApiKey");
        var promptDirectory = Require(configuration, "Agents:PromptDirectory");

        // [ADDED 2026-09-14, CORRECTED 2026-09-14] The four FreehandDimensions agents (composition
        // + their four render overrides) deliberately use a DIFFERENT deployment (sol) than every
        // other agent this file registers, and a configurable reasoning effort - explicit product
        // decision to be able to tune it without a code change ("play with reasoning levels").
        // [BUG FOUND LIVE] First wired assuming sol shared Llm:Maf's endpoint/key (same Azure AI
        // Foundry project, different deployment name only) - confirmed WRONG via a real run: sol
        // 404s (DeploymentNotFound) on trpl-prod-saas-ai-3 (Llm:Maf's endpoint), because it is
        // actually deployed on a DIFFERENT resource, trpl-prod-saas-ai-2. Endpoint/ApiKey are now
        // independently configurable (FreehandDimensions:Endpoint/ApiKey), each falling back to
        // Llm:Maf's own value only so a fresh environment with nothing configured still starts -
        // that fallback will keep 404ing until FreehandDimensions:Endpoint/ApiKey are actually set.
        var freehandEndpoint = configuration["FreehandDimensions:Endpoint"] is { Length: > 0 } fe ? fe : endpoint;
        var freehandApiKey = configuration["FreehandDimensions:ApiKey"] is { Length: > 0 } fk ? fk : apiKey;
        var freehandModel = configuration["FreehandDimensions:Model"] is { Length: > 0 } fm ? fm : "gpt-5.6-sol";
        var freehandReasoningEffort = configuration["FreehandDimensions:ReasoningEffort"] is { Length: > 0 } fre
            ? Enum.Parse<ResponseReasoningEffortLevel>(fre, ignoreCase: true)
            : ResponseReasoningEffortLevel.High;

        // [ADDED 2026-09-22] Activates ReadOnlySqlFetchTool for the narrative analyst - see that
        // class's own doc comment for the real guardrails (single SELECT only, forced #scoped
        // reference, row cap, timeout, 3-call budget) that stay in place around this deliberate
        // exception to CLAUDE.md's "never let an LLM author SQL" rule. Falls back to the app's own
        // ConnectionStrings:RegTrack (same connection SqlDimensionRepository already uses) so this
        // is never silently unconfigured - but that means the REAL enforcement on any given
        // environment is whatever that login's own grants are: confirmed live 2026-09-22 that UAT's
        // is `sa` (full sysadmin, no DB-level read-only backstop there - the tool's own query-shape
        // validation is the only wall on UAT) while the SEPARATE prod-readonly replica login
        // (regtech_dev01_readonly) is genuinely DB-enforced read-only. Whatever ConnectionStrings:
        // RegTrack resolves to in a REAL prod deployment has never been checked from here - flag
        // that to whoever owns prod DB credentials before this reaches real prod traffic.
        var readOnlySqlConnectionString = configuration["FreehandDimensions:ReadOnlySqlConnectionString"] is { Length: > 0 } rosql
            ? rosql
            : configuration["ConnectionStrings:RegTrack"];

        // [ADDED 2026-09-22] Tenant memory - see
        // docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md. Falls back to the same
        // storage account reports already use (Azure:BlobConnectionString), different container -
        // never silently unconfigured on an environment that already has blob persistence working.
        var memoryBlobConnectionString = configuration["TenantMemory:BlobConnectionString"] is { Length: > 0 } mbcs
            ? mbcs
            : configuration["Azure:BlobConnectionString"];
        var memoryContainerName = configuration["TenantMemory:ContainerName"] is { Length: > 0 } mcn
            ? mcn
            : "insights-tenant-memory";

        // [ADDED 2026-09-14] Vision QA's own deployment - a real, different Azure resource again
        // (trpl-prod-saas-ai-3, not ai-2/sol or the shared Llm:Maf one), same "confirm before
        // wiring, do not assume it shares an endpoint" lesson FreehandDimensions' own [BUG FOUND
        // LIVE] note already paid for once tonight.
        var visionQaEndpoint = Require(configuration, "Llm:VisionQa:Endpoint");
        var visionQaModel = Require(configuration, "Llm:VisionQa:Model");
        var visionQaApiKey = Require(configuration, "Llm:VisionQa:ApiKey");

        /*  Item 17 - cost instrumentation. Registered as one singleton exposed under two service
            types so the agents depend on the ILlmUsageRecorder contract while anything that needs
            to dispose the Meter can resolve the concrete class. NOT two registrations: two Meters
            with the same name would double-count every token.

            Agents:MaxTokensPerCall is OPTIONAL and off when absent. A per-call ceiling that is
            guessed rather than measured trips on legitimate runs, and a breaker nobody trusts
            gets disabled - so it stays opt-in until a real run tells us what a normal call costs.
            The metrics below are what supply that number.                                        */
        var maxTokensPerCall = configuration.GetValue<int?>("Agents:MaxTokensPerCall");

        // Otel:EnableSensitiveData - design doc Sec.3.2's two-projection audit. Off by default:
        // full prompt/response text is genuinely sensitive (customer names, entity data), and
        // turning it on is a deliberate internal-observability choice, not something that should
        // happen just because LangFuse is configured at all (endpoint/keys control WHERE spans go;
        // this controls WHAT they carry).
        var enableSensitiveTelemetry = configuration.GetValue<bool>("Otel:EnableSensitiveData");

        /*  Item 17 (design doc Sec.4.4) - the concurrency semaphore. Agents:MaxConcurrentLlmCalls
            is OPTIONAL, same stance as MaxTokensPerCall above: unset means ungated (today's
            behaviour, unchanged), so this cannot make an existing working setup worse by being
            present. One shared gate instance registered here and threaded into every one of the
            five agent-factory calls below - see LlmConcurrencyGate's doc comment for why it must
            be ONE shared instance, not one per agent.                                              */
        var maxConcurrentLlmCalls = configuration.GetValue<int?>("Agents:MaxConcurrentLlmCalls");
        if (maxConcurrentLlmCalls is int cap)
        {
            var gate = new LlmConcurrencyGate(cap);
            services.AddSingleton(gate);
            // insights.queue.depth{lane}, insights.governor.saturation_pct/batch_max_wait_seconds
            // (CONFIGURATION.md, Sec.13). Only meaningful once a gate exists - ungated means no
            // queueing to measure, and ConcurrencyGatedChatClient itself never enters the chain
            // either (see MafAgentFactory).
            services.AddSingleton(new LlmConcurrencyGateMetrics(gate));
        }

        // [ADDED 2026-09-23] Backs onSqlToolInvoked/onMemoryWriteInvoked below - see
        // IToolInvocationRecorder's own doc comment and sql/34_tool_invocation_log.sql. Falls back
        // to ConnectionStrings:RegTrack, same "never silently unconfigured" reasoning as
        // readOnlySqlConnectionString above (this always writes to the same DB the app already
        // has a real connection string for - it is a separate table, not a separate database).
        var toolInvocationConnectionString = configuration["ToolInvocationLog:ConnectionString"] is { Length: > 0 } tilcs
            ? tilcs
            : configuration["ConnectionStrings:RegTrack"];
        IToolInvocationRecorder toolInvocationRecorder = toolInvocationConnectionString is { Length: > 0 } tics
            ? new SqlToolInvocationRecorder(tics)
            : IToolInvocationRecorder.Null;

        services.AddSingleton<InsightsCostMetrics>();
        services.AddSingleton<ILlmUsageRecorder>(sp => sp.GetRequiredService<InsightsCostMetrics>());

        // TryAdd, not Add - AddInsightsFreeDigest may already have registered IPromptLoader for
        // the same directory. Registering twice would not break resolution (DI returns the last
        // one), but it is needless duplication of an identical singleton.
        services.TryAddSingleton<IPromptLoader>(_ => new FilePromptLoader(promptDirectory));

        // [REMOVED 2026-09-11] ICompositionAgent/ICompositionReflectionAgent (01_composition.md,
        // 02_composition_reflection.md) only ever served "compliance_health"'s dynamic LLM
        // composition path - deleted along with it. fixed_holistic and dimension_selection both
        // build their CompositionPlan deterministically in C# (FixedHolisticComposition.Build /
        // DimensionSelectionComposition.Build), zero LLM calls, so nothing replaces this.

        // [ADDED 2026-09-14] Real LLM composition for FreehandDimensions.Names - see that class's
        // doc comment. One agent per dimension (each gets its own prompt, since real fields/traps
        // differ per dimension), keyed by dimension name, same dictionary-not-keyed-DI reasoning
        // as the render dictionary below.
        services.AddSingleton<IReadOnlyDictionary<string, IFreehandDimensionCompositionAgent>>(sp =>
        {
            var usage = sp.GetRequiredService<ILlmUsageRecorder>();
            var gate = sp.GetService<LlmConcurrencyGate>();

            IFreehandDimensionCompositionAgent Build(string dimension, string promptFile) =>
                new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
                    freehandEndpoint, freehandModel, freehandApiKey, $"FreehandComposition{dimension}Agent", $"Decides structure/hero/emphasis for a freehand {dimension} insight from real tenant data.",
                    LoadPromptSync(sp, promptFile), usage, maxTokensPerCall, enableSensitiveTelemetry, gate, freehandReasoningEffort));

            return new Dictionary<string, IFreehandDimensionCompositionAgent>
            {
                // [ADDED 2026-09-25] v2 - real "window" data_quality entry documented (Act's own
                // period-scoping change). See that file's own header for what changed and why.
                ["Act"] = Build("Act", "02_composition_freehand_act_v2.md"),
                ["BacklogAging"] = Build("BacklogAging", "02_composition_freehand_backlogaging.md"),
                // [ADDED 2026-09-25] v2 - Departments got the same @WindowStart/@WindowEnd hard
                // population gate as Act/Event this session; its own "window" data_quality entry
                // needed the same real-detail-text fix, not the generic-filler default.
                ["Departments"] = Build("Departments", "02_composition_freehand_departments_v2.md"),
                ["Licence"] = Build("Licence", "02_composition_freehand_licence.md"),
                // [ADDED 2026-09-25] v2 - same window data_quality fix as Departments above.
                ["Location"] = Build("Location", "02_composition_freehand_location_v2.md"),
                // [ADDED 2026-09-22] Closes the gap CLAUDE.md's V1 scope table flagged - same
                // pattern as the five above, not a new mechanism.
                // [ADDED 2026-09-25] v2 on Risk/Nature/Internal - same window data_quality fix.
                ["Risk"] = Build("Risk", "02_composition_freehand_risk_v2.md"),
                ["Nature"] = Build("Nature", "02_composition_freehand_nature_v2.md"),
                ["Internal"] = Build("Internal", "02_composition_freehand_internal_v2.md"),
                // [ADDED 2026-09-25] v2 - same real "window" data_quality fix as Act above.
                ["Event"] = Build("Event", "02_composition_freehand_event_v2.md"),
                // [ADDED 2026-09-23] Retires Sambram's fixed single-section Users template - see
                // FreehandDimensions.cs's own doc comment for the real lab-tested evidence behind
                // this decision.
                // [ADDED 2026-09-25] v2 - same window data_quality fix as the others above.
                ["Users"] = Build("Users", "02_composition_freehand_users_v2.md"),
            };
        });

        services.AddSingleton<INarrativeAgent>(sp => new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.",
            LoadPromptSync(sp, "03_narrative.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>()),
            sp.GetService<IReportEncryptor>(), sp.GetService<IReportDecryptor>(), memoryBlobConnectionString, memoryContainerName));

        services.AddSingleton<INarrativeReflectionAgent>(sp => new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques the narrative.",
            LoadPromptSync(sp, "04_narrative_reflection.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

        // [ADDED 2026-09-20] v2 of Narrate+Reflect for the 5 freehand dimensions, behind
        // InsightsReportOrchestrationInput.UseAnalystNarrative - see AnalyzeAndNarrateActivity's own
        // doc comment and docs/superpowers/specs/2026-09-20-narrative-analyst-agent-design.md.
        // Deliberately on the SAME sol deployment/reasoning-effort variables as the freehand
        // composition agents above (freehandEndpoint/freehandModel/freehandApiKey/
        // freehandReasoningEffort) - not the shared `model` every other agent in this file uses -
        // per the user's explicit "use sol with high reasoning" instruction, and because root-cause
        // tracing across multiple raw rows is the same class of harder reasoning task freehand
        // composition already runs on that deployment.
        services.AddSingleton<IAnalystNarrativeAgent>(sp => new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            freehandEndpoint, freehandModel, freehandApiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.",
            LoadPromptSync(sp, "v2/03_narrative_analyst.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>(), freehandReasoningEffort),
            readOnlySqlConnectionString,
            // [WAS null, FIXED 2026-09-23] This was the real gap: the hook existed but nothing
            // durable ever recorded a call. Now every real fetch_scoped_sql_data/write_tenant_memory
            // call - or the fact that a run made NONE - lands in dbo.InsightsToolInvocationLog.
            onSqlToolInvoked: (runId, sql, result, success) =>
                toolInvocationRecorder.RecordAsync(runId, "analyze_and_narrate", "fetch_scoped_sql_data", sql, success, result.Length),
            memoryEncryptor: sp.GetService<IReportEncryptor>(), memoryDecryptor: sp.GetService<IReportDecryptor>(),
            memoryBlobConnectionString: memoryBlobConnectionString, memoryContainerName: memoryContainerName,
            onMemoryWriteInvoked: (runId, dimensionName, success) =>
                toolInvocationRecorder.RecordAsync(runId, "analyze_and_narrate", "write_tenant_memory", dimensionName, success, resultLength: null)));

        // [ADDED 2026-09-14] Real vision-model gate inside the render-retry loop - see
        // VisionQaActivity's own doc comment for why this is a real gate, not advisory like
        // PlaywrightQaActivity. CreateJsonAgent, not CreateTextAgent - the output contract is
        // {has_visual_defect, issue}, same JSON-mode reasoning as every other structured agent.
        services.AddSingleton<IVisionQaAgent>(sp => new MafVisionQaAgent(MafAgentFactory.CreateJsonAgent(
            visionQaEndpoint, visionQaModel, visionQaApiKey, "VisionQaAgent", "Checks a real screenshot of the rendered report for overlap or broken layout only.",
            LoadPromptSync(sp, "06_vision_qa.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

        // [CHANGED 2026-09-01] Was 05_report_html.md ("compliance_health" - dynamic, no fixed
        // tabs, composition-agent-decided structure) - that file and report type were removed
        // this session. Fixed Holistic (05_report_html_fixed_holistic.md, ReportType
        // "fixed_holistic" = FixedHolisticComposition.ReportType) is now the only render prompt.
        // A second IFixedHolisticReportHtmlAgent registration briefly existed alongside this one
        // (so RenderHtmlActivity could pick between two agents by report type) - removed the same
        // session once the plain path it existed to distinguish from was gone; back to one agent.
        //
        // [REINTRODUCED 2026-09-08] Two agents again, for a genuinely new reason (not the removed
        // one above): DimensionSelectionComposition.ReportType ("dimension_selection") needs its
        // own render prompt with no fixed 6-tab hero. Registered as a plain
        // Dictionary<string,IReportHtmlAgent> keyed by ReportType, not two separate IReportHtmlAgent
        // registrations (DI would only resolve the last one) and not .NET keyed-DI attributes (this
        // codebase has not exercised that feature anywhere else - a plain dictionary is one thing
        // fewer to get wrong under time pressure). RenderHtmlActivity throws on an unrecognised
        // ReportType rather than silently picking one - CLAUDE.md non-negotiable #2.
        services.AddSingleton<IReadOnlyDictionary<string, IReportHtmlAgent>>(sp =>
        {
            var usage = sp.GetRequiredService<ILlmUsageRecorder>();
            var gate = sp.GetService<LlmConcurrencyGate>();

            // [TRAP] `modelOverride is null ? null : freehandReasoningEffort` looks equivalent but
            // is NOT: ResponseReasoningEffortLevel has an implicit conversion FROM string, so the
            // compiler resolves that ternary's common type as the non-nullable struct itself
            // (preferring the string-conversion path over wrapping in Nullable<T>) and tries to
            // implicitly-convert the null LITERAL through THAT operator - crashing at runtime with
            // ArgumentNullException deep inside the SDK, not a compile error. Confirmed via an
            // isolated repro against the pinned OpenAI 2.11.0 package. The explicit cast forces the
            // nullable target type instead.
            // [TRIED 2026-09-15, REVERTED SAME DAY] requestReasoningSummary: false was a real
            // diagnostic - ruled out, not the cause. 5 live Users/Minda runs total (2 summary ON,
            // 3 summary OFF... reading was mixed both ways): failures happened regardless of this
            // flag, always in the per-user leaderboard section, always right after a real named
            // employee's row. See NormalizeActivity's own doc comment on the ongoing investigation.
            IReportHtmlAgent Build(string name, string description, string promptFile, string? modelOverride = null) =>
                new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
                    modelOverride is null ? endpoint : freehandEndpoint, modelOverride ?? model, modelOverride is null ? apiKey : freehandApiKey, name, description,
                    LoadPromptSync(sp, promptFile), usage, maxTokensPerCall, enableSensitiveTelemetry, gate,
                    modelOverride is null ? (ResponseReasoningEffortLevel?)null : freehandReasoningEffort));

            return new Dictionary<string, IReportHtmlAgent>
            {
                ["fixed_holistic"] = Build(
                    "ReportHtmlAgent", "Renders the fixed 6-tab Holistic Insights report as self-contained HTML.",
                    "05_report_html_fixed_holistic.md"),
                ["dimension_selection"] = Build(
                    "DimensionSelectionReportHtmlAgent", "Renders a caller-selected subset of dimensions as self-contained HTML, no fixed tabs.",
                    "05_report_html_dimension_selection.md"),
                // [ADDED 2026-09-09] Sambram's real, approved "dimension view" design system
                // (AI-INSIGHTS-BRAND-HANDOFF.md Sec.6, single-section, no tabs, no donut) arrived
                // AFTER the generic dimension_selection prompt above was built on Trent's design -
                // Location gets its own specific-key override here rather than changing the
                // generic prompt (which still serves every dimension without its own specific
                // key, e.g. Nature/Risk/Internal/Event, on Trent's system for now - flagged as a
                // real, not-yet-closed gap, not a silent inconsistency; NOT part of v1's release
                // scope - see the freehand-dimensions-v1-scope memory).
                // [REPLACED 2026-09-14] Location joined FreehandDimensions.Names the same day as
                // v1's scope was confirmed - same reasoning as the Departments/BacklogAging/Act/
                // Licence entries below: real LLM composition ahead of render, sol deployment.
                // [ADDED 2026-09-25] v2 - real "window" data_quality phrasing fix, same as Act's
                // own render prompt - see that file's own header for what changed and why.
                ["dimension_selection:Location"] = Build(
                    "DimensionSelectionLocationReportHtmlAgent", "Renders a freehand-composed Location insight as self-contained HTML.",
                    "05_report_html_dimension_selection_location_v2.md", freehandModel),
                // [ADDED 2026-09-09, REPLACED 2026-09-09, REPLACED AGAIN 2026-09-23] Dimension-
                // specific override for a single-"Users" request - RenderHtmlActivity's own doc
                // comment explains the "{ReportType}:{DimensionName}" key-preference rule this
                // depends on. First built to reproduce the real Angular "By User & Role" tabbed
                // sub-page verbatim; replaced 2026-09-09 with Sambram's fixed "dimension view"
                // design system; replaced AGAIN 2026-09-23 with the same freehand treatment every
                // other dimension below already has (lab-tested first, real Minda output showed the
                // composition agent choosing a genuinely different, tenant-specific hero the fixed
                // template never surfaced - see FreehandDimensions.cs's own doc comment). Several
                // real fields still have no backing (imprisonment-overdue combined lens, per-user
                // risk mix, dept-head fan-out, an "Approver" role) - see the prompt file's own "Real
                // vs. NOT AVAILABLE" table, unchanged by this move.
                // [ADDED 2026-09-25] v2 - same real "window" data_quality fix as Location above.
                ["dimension_selection:Users"] = Build(
                    "DimensionSelectionUserReportHtmlAgent", "Renders a freehand-composed Users insight as self-contained HTML.",
                    "05_report_html_dimension_selection_user_v2.md", freehandModel),
                // [ADDED 2026-09-09, REPLACED same day] Same reasoning as the Users entry
                // immediately above. The Concentration tab and closure-status strip the earlier
                // Angular-mirroring version carried (both honest not-available blocks, no real
                // backing) don't exist as structural slots in Sambram's single-section shape at
                // all any more - the gap is gone along with the tabs, not papered over.
                // [REPLACED 2026-09-14] Departments/BacklogAging/Act/Licence no longer use
                // Sambram's fixed single-section template - FreehandDimensions.Names now gives
                // each its own real LLM composition step (ComposeFreehandDimensionActivity) ahead
                // of render, so the render agent gets a genuinely agent-decided structure/hero to
                // build from rather than a fixed document shape. Deliberately on the sol deployment
                // (freehandModel), not the shared `model` every other agent in this file uses.
                // [ADDED 2026-09-25] v2 - same real "window" data_quality fix as Location above.
                ["dimension_selection:Departments"] = Build(
                    "DimensionSelectionDepartmentReportHtmlAgent", "Renders a freehand-composed Departments insight as self-contained HTML.",
                    "05_report_html_dimension_selection_department_v2.md", freehandModel),
                ["dimension_selection:BacklogAging"] = Build(
                    "DimensionSelectionBacklogAgingReportHtmlAgent", "Renders a freehand-composed BacklogAging insight as self-contained HTML.",
                    "05_report_html_dimension_selection_backlogaging.md", freehandModel),
                // [ADDED 2026-09-25] v2 - real "window" data_quality phrasing fix (Act's own
                // period-scoping change) - see that file's own header for what changed and why.
                ["dimension_selection:Act"] = Build(
                    "DimensionSelectionActReportHtmlAgent", "Renders a freehand-composed Act insight as self-contained HTML.",
                    "05_report_html_dimension_selection_act_v2.md", freehandModel),
                ["dimension_selection:Licence"] = Build(
                    "DimensionSelectionLicenceReportHtmlAgent", "Renders a freehand-composed Licence insight as self-contained HTML.",
                    "05_report_html_dimension_selection_licence.md", freehandModel),
                // [ADDED 2026-09-22] Closes the gap CLAUDE.md's V1 scope table flagged - same
                // freehand pattern as the five above.
                // [ADDED 2026-09-25] v2 on Risk/Nature/Internal - same real "window" data_quality
                // fix as Location above.
                ["dimension_selection:Risk"] = Build(
                    "DimensionSelectionRiskReportHtmlAgent", "Renders a freehand-composed Risk insight as self-contained HTML.",
                    "05_report_html_dimension_selection_risk_v2.md", freehandModel),
                ["dimension_selection:Nature"] = Build(
                    "DimensionSelectionNatureReportHtmlAgent", "Renders a freehand-composed Nature-of-compliance insight as self-contained HTML.",
                    "05_report_html_dimension_selection_nature_v2.md", freehandModel),
                ["dimension_selection:Internal"] = Build(
                    "DimensionSelectionInternalReportHtmlAgent", "Renders a freehand-composed Statutory-vs-Internal insight as self-contained HTML.",
                    "05_report_html_dimension_selection_internal_v2.md", freehandModel),
                // [ADDED 2026-09-25] v2 - same real "window" data_quality fix as Act above.
                ["dimension_selection:Event"] = Build(
                    "DimensionSelectionEventReportHtmlAgent", "Renders a freehand-composed Event-triggered-compliance insight as self-contained HTML.",
                    "05_report_html_dimension_selection_event_v2.md", freehandModel),
            };
        });

        // Headless Chromium, launched once at startup, shared by both DomPurifySanitizer and
        // PlaywrightReportQa - launching per-activity-call would be a multi-hundred-millisecond
        // tax on every single report. Blocking .GetAwaiter().GetResult() inside a DI factory is
        // deliberate here, same "fail at startup, not first use" stance as the rest of this file -
        // an unavailable browser binary must surface immediately, not on the first real run.
        services.AddSingleton<IPlaywright>(_ => Microsoft.Playwright.Playwright.CreateAsync().GetAwaiter().GetResult());
        services.AddSingleton<IBrowser>(sp => sp.GetRequiredService<IPlaywright>().Chromium.LaunchAsync().GetAwaiter().GetResult());

        services.AddSingleton<IDomPurifySanitizer>(sp => new DomPurifySanitizer(sp.GetRequiredService<IBrowser>()));
        services.AddSingleton<IReportQaRunner>(sp => new PlaywrightReportQa(sp.GetRequiredService<IBrowser>()));

        return services;
    }

    /// <summary>
    /// MafAgentFactory takes instructions synchronously but IPromptLoader.LoadAsync is async -
    /// registration-time DI factories in this codebase are synchronous (see BuildChatClientFactory
    /// in FreeDigestRegistration.cs), so this blocks once per agent at startup, not per call.
    /// </summary>
    private static string LoadPromptSync(IServiceProvider sp, string fileName) =>
        sp.GetRequiredService<IPromptLoader>().LoadAsync(fileName).GetAwaiter().GetResult();

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}

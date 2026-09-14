#pragma warning disable OPENAI001 // ResponseReasoningEffortLevel - experimental in this SDK version, same pragma MafAgentFactory already carries.

using Insights.Agents;
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
                ["Act"] = Build("Act", "02_composition_freehand_act.md"),
                ["BacklogAging"] = Build("BacklogAging", "02_composition_freehand_backlogaging.md"),
                ["Departments"] = Build("Departments", "02_composition_freehand_departments.md"),
                ["Licence"] = Build("Licence", "02_composition_freehand_licence.md"),
            };
        });

        services.AddSingleton<INarrativeAgent>(sp => new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.",
            LoadPromptSync(sp, "03_narrative.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

        services.AddSingleton<INarrativeReflectionAgent>(sp => new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques the narrative.",
            LoadPromptSync(sp, "04_narrative_reflection.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

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
                // key, e.g. Nature/Entity/Risk/Act/Internal/Event, on Trent's system for now -
                // flagged as a real, not-yet-closed gap, not a silent inconsistency).
                ["dimension_selection:Location"] = Build(
                    "DimensionSelectionLocationReportHtmlAgent", "Renders a single-Location-dimension request as self-contained HTML, matching Sambram's approved dimension-view design system.",
                    "05_report_html_dimension_selection_location.md"),
                // [ADDED 2026-09-09, REPLACED same day] Dimension-specific override for a
                // single-"Users" request - RenderHtmlActivity's own doc comment explains the
                // "{ReportType}:{DimensionName}" key-preference rule this depends on. First built
                // to reproduce the real Angular "By User & Role" tabbed sub-page verbatim; replaced
                // the same day with Sambram's real, approved "dimension view" design system
                // (AI-INSIGHTS-BRAND-HANDOFF.md Sec.6 - single section, no tabs, no donut) once that
                // handoff arrived - the tabbed shape never matched the approved AI-generation
                // contract. Several real fields still have no backing (imprisonment-overdue
                // combined lens, per-user risk mix, dept-head fan-out, an "Approver" role) - see
                // the prompt file's own "Real vs. NOT AVAILABLE" table.
                ["dimension_selection:Users"] = Build(
                    "DimensionSelectionUserReportHtmlAgent", "Renders a single-Users-dimension request as self-contained HTML, matching Sambram's approved dimension-view design system.",
                    "05_report_html_dimension_selection_user.md"),
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
                ["dimension_selection:Departments"] = Build(
                    "DimensionSelectionDepartmentReportHtmlAgent", "Renders a freehand-composed Departments insight as self-contained HTML.",
                    "05_report_html_dimension_selection_department.md", freehandModel),
                ["dimension_selection:BacklogAging"] = Build(
                    "DimensionSelectionBacklogAgingReportHtmlAgent", "Renders a freehand-composed BacklogAging insight as self-contained HTML.",
                    "05_report_html_dimension_selection_backlogaging.md", freehandModel),
                ["dimension_selection:Act"] = Build(
                    "DimensionSelectionActReportHtmlAgent", "Renders a freehand-composed Act insight as self-contained HTML.",
                    "05_report_html_dimension_selection_act.md", freehandModel),
                ["dimension_selection:Licence"] = Build(
                    "DimensionSelectionLicenceReportHtmlAgent", "Renders a freehand-composed Licence insight as self-contained HTML.",
                    "05_report_html_dimension_selection_licence.md", freehandModel),
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

using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

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

        services.AddSingleton<ICompositionAgent>(sp => new MafCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent", "Decides report structure.",
            LoadPromptSync(sp, "01_composition.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

        services.AddSingleton<ICompositionReflectionAgent>(sp => new MafCompositionReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionReflectionAgent", "Critiques the composition plan.",
            LoadPromptSync(sp, "02_composition_reflection.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

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
        services.AddSingleton<IReportHtmlAgent>(sp => new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "ReportHtmlAgent", "Renders the fixed 6-tab Holistic Insights report as self-contained HTML.",
            LoadPromptSync(sp, "05_report_html_fixed_holistic.md"), sp.GetRequiredService<ILlmUsageRecorder>(), maxTokensPerCall, enableSensitiveTelemetry, sp.GetService<LlmConcurrencyGate>())));

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

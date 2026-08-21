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

        // TryAdd, not Add - AddInsightsFreeDigest may already have registered IPromptLoader for
        // the same directory. Registering twice would not break resolution (DI returns the last
        // one), but it is needless duplication of an identical singleton.
        services.TryAddSingleton<IPromptLoader>(_ => new FilePromptLoader(promptDirectory));

        services.AddSingleton<ICompositionAgent>(sp => new MafCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent", "Decides report structure.",
            LoadPromptSync(sp, "01_composition.md"))));

        services.AddSingleton<ICompositionReflectionAgent>(sp => new MafCompositionReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionReflectionAgent", "Critiques the composition plan.",
            LoadPromptSync(sp, "02_composition_reflection.md"))));

        services.AddSingleton<INarrativeAgent>(sp => new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.",
            LoadPromptSync(sp, "03_narrative.md"))));

        services.AddSingleton<INarrativeReflectionAgent>(sp => new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques the narrative.",
            LoadPromptSync(sp, "04_narrative_reflection.md"))));

        services.AddSingleton<IReportHtmlAgent>(sp => new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "ReportHtmlAgent", "Renders the approved report as self-contained HTML.",
            LoadPromptSync(sp, "05_report_html.md"))));

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

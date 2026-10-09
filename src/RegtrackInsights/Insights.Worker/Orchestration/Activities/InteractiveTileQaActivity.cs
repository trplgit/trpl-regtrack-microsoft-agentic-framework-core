using DurableTask.Core;
using Insights.Presentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insights.Worker.Orchestration.Activities;

/// <summary>
/// [CHANGED 2026-10-09] <paramref name="OnlyCardOrdinals"/> is trailing-optional: null = check every
/// card (the 4.8 behaviour); a list = re-verify only those cards after a scoped patch.
/// </summary>
public sealed record InteractiveTileQaInput(string Html, IReadOnlyList<int>? OnlyCardOrdinals = null);

/// <summary>
/// [CHANGED 2026-10-09] Every field after Findings is trailing-optional and null-safe: a run
/// recorded before they existed deserializes them as null/0 and the orchestrator reproduces the
/// 4.8 path exactly. <paramref name="PatchDisabled"/> true = configuration has the patch loop off;
/// <paramref name="PageErrors"/> = uncaught page errors seen while exercising the page;
/// <paramref name="Truncated"/> = the pass budget ran out; <paramref name="TotalTokens"/> = vision
/// tokens billed by this pass (previously dropped, never charged).
/// </summary>
public sealed record InteractiveTileQaOutput(
    IReadOnlyList<TileFinding> Findings,
    bool? PatchDisabled = null,
    IReadOnlyList<string>? PageErrors = null,
    bool? Truncated = null,
    long TotalTokens = 0,
    bool? Disabled = null);

/// <summary>
/// Node 11b - interactive tile QA. Reads TileQaOptions on EVERY execution (IOptionsMonitor):
/// disabled => returns no findings without opening a page. Fails soft: a checker exception is
/// logged and reported as "no findings" - this is cosmetic QA, never a security control, and it
/// must never fail a report that passed every deterministic gate before it.
/// </summary>
public sealed class InteractiveTileQaActivity(
    IInteractiveTileChecker checker, ILogger<InteractiveTileQaActivity> logger, IOptionsMonitor<TileQaOptions>? options = null)
    : AsyncTaskActivity<InteractiveTileQaInput, InteractiveTileQaOutput>
{
    private static readonly TileQaOptions Defaults = new();

    protected override Task<InteractiveTileQaOutput> ExecuteAsync(TaskContext context, InteractiveTileQaInput input) => RunAsync(input);

    internal async Task<InteractiveTileQaOutput> RunAsync(InteractiveTileQaInput input)
    {
        var opts = options?.CurrentValue ?? Defaults;
        if (!opts.Enabled)
        {
            logger.LogInformation("Interactive tile QA is disabled by configuration ({Section}:Enabled) - skipped.", TileQaOptions.SectionName);
            return new InteractiveTileQaOutput([], PatchDisabled: true, PageErrors: [], Truncated: false, TotalTokens: 0, Disabled: true);
        }

        var request = new TileCheckRequest(
            OnlyCardOrdinals: input.OnlyCardOrdinals,
            BudgetSeconds: opts.BudgetSeconds,
            PerElementSeconds: opts.PerElementSeconds,
            MaxGlitchReviews: opts.MaxGlitchReviews,
            MaxCards: opts.MaxCardsPerPass,
            ScreenshotDirectory: string.IsNullOrWhiteSpace(opts.ScreenshotDirectory) ? null : opts.ScreenshotDirectory);

        TileCheckResult result;
        try
        {
            result = await checker.FindIssuesAsync(input.Html, request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Interactive tile QA failed - reporting no findings (fail-soft; the page already passed every deterministic gate).");
            return new InteractiveTileQaOutput([], PatchDisabled: !opts.PatchEnabled, PageErrors: [], Truncated: true, TotalTokens: 0);
        }

        foreach (var f in result.Findings)
            logger.LogWarning("Interactive tile QA finding: card {Ordinal} ({Kind}) {Severity} - {Description}",
                f.CardOrdinal, f.ElementKind, f.Severity, f.TechnicalDescription);
        if (result.PageErrors.Count > 0)
            logger.LogWarning("Interactive tile QA saw {Count} uncaught page error(s): {First}", result.PageErrors.Count, result.PageErrors[0]);
        if (result.Truncated)
            logger.LogWarning("Interactive tile QA pass hit its {Seconds}s budget - findings are partial.", opts.BudgetSeconds);

        return new InteractiveTileQaOutput(
            result.Findings,
            PatchDisabled: !opts.PatchEnabled,
            PageErrors: result.PageErrors,
            Truncated: result.Truncated,
            TotalTokens: result.TotalTokens);
    }
}

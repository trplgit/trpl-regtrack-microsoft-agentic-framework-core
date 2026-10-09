using DurableTask.Core;
using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PatchRenderInput(string Html, IReadOnlyList<TileFinding> Findings);

/// <summary>
/// [CHANGED 2026-10-09] Everything after TotalTokens is trailing-optional and null-safe. A run
/// recorded before these fields existed deserializes <paramref name="Outcome"/> as null, which the
/// orchestrator treats exactly as the 4.8 "the whole document came back patched" path.
///
/// Outcome: "patched" (Html is the spliced page, PatchedCardOrdinals says which cards), or one of
/// "unchanged" / "rejected" / "failed" / "skipped" / "disabled" - in all of which Html is the
/// INPUT page byte-for-byte and the orchestrator stops patching. This activity never throws for a
/// model problem: a patch is best-effort and must never fail a report that already passed every
/// deterministic gate.
/// </summary>
public sealed record PatchRenderOutput(
    string Html,
    long TotalTokens,
    string? Outcome = null,
    IReadOnlyList<int>? PatchedCardOrdinals = null,
    IReadOnlyList<int>? UnpatchableCardOrdinals = null,
    string? RejectReason = null);

/// <summary>
/// Node 11c - scoped patch of the cards the tile checker flagged. Builds the scoped request
/// (ScopedPatchSplicer.BuildPlan), calls the patch agent under a wall-clock cap, validates and
/// splices the answer (ScopedPatchSplicer.Apply), then runs the deterministic before/after
/// PatchIntegrityGate; any violation discards the attempt. Reads TileQaOptions on every execution.
/// </summary>
public sealed class PatchRenderActivity(
    IPatchRenderAgent agent, ILogger<PatchRenderActivity> logger, IOptionsMonitor<TileQaOptions>? options = null)
    : AsyncTaskActivity<PatchRenderInput, PatchRenderOutput>
{
    private static readonly TileQaOptions Defaults = new();

    protected override Task<PatchRenderOutput> ExecuteAsync(TaskContext context, PatchRenderInput input) => RunAsync(input);

    internal async Task<PatchRenderOutput> RunAsync(PatchRenderInput input)
    {
        var opts = options?.CurrentValue ?? Defaults;
        if (!opts.PatchEnabled)
        {
            logger.LogInformation("Patch loop is disabled by configuration ({Section}:PatchEnabled) - findings ship as reported.", TileQaOptions.SectionName);
            return new PatchRenderOutput(input.Html, 0, Outcome: "disabled");
        }

        var plan = ScopedPatchSplicer.BuildPlan(input.Html, input.Findings, out var skipReason);
        if (plan is null)
        {
            logger.LogWarning("PatchRenderActivity: nothing to send - {Reason}.", skipReason);
            return new PatchRenderOutput(input.Html, 0, Outcome: "skipped", RejectReason: skipReason);
        }

        AgentCallResult<string> answer;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, opts.PatchCallSeconds)));
        try
        {
            answer = await agent.PatchAsync(plan.RequestJson, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PatchRenderActivity: patch call failed for cards {Cards} - attempt discarded.", string.Join(',', plan.TargetOrdinals));
            return new PatchRenderOutput(input.Html, 0, Outcome: "failed", UnpatchableCardOrdinals: plan.UnpatchableOrdinals, RejectReason: ex.GetType().Name + ": " + ex.Message);
        }

        var splice = ScopedPatchSplicer.Apply(input.Html, answer.Value, plan.TargetOrdinals);
        if (splice.Outcome != "patched")
        {
            logger.LogWarning("PatchRenderActivity: model answer not applied ({Outcome}) - {Reasons}", splice.Outcome, string.Join("; ", splice.Rejections));
            return new PatchRenderOutput(input.Html, answer.TotalTokens, Outcome: splice.Outcome,
                UnpatchableCardOrdinals: plan.UnpatchableOrdinals, RejectReason: string.Join("; ", splice.Rejections));
        }

        var gate = PatchIntegrityGate.Check(input.Html, splice.Html, splice.PatchedOrdinals);
        if (!gate.Ok)
        {
            logger.LogWarning("PatchRenderActivity: patch of cards {Cards} rejected by the integrity gate - {Violations}",
                string.Join(',', splice.PatchedOrdinals), string.Join("; ", gate.Violations));
            return new PatchRenderOutput(input.Html, answer.TotalTokens, Outcome: "rejected",
                UnpatchableCardOrdinals: plan.UnpatchableOrdinals, RejectReason: string.Join("; ", gate.Violations));
        }

        logger.LogInformation("PatchRenderActivity: patched card(s) {Cards}; {Unpatchable} finding(s) could not be located.",
            string.Join(',', splice.PatchedOrdinals), plan.UnpatchableOrdinals.Count);
        return new PatchRenderOutput(splice.Html, answer.TotalTokens, Outcome: "patched",
            PatchedCardOrdinals: splice.PatchedOrdinals, UnpatchableCardOrdinals: plan.UnpatchableOrdinals);
    }
}

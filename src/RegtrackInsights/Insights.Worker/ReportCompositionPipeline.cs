using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker;

/// <summary>
/// Outcome of one report's compose -> reflect -> narrate -> reflect -> gate run.
/// <see cref="Approved"/> mirrors <see cref="GateResult"/>.Approved exactly - the gate is the
/// only thing that decides publish/refuse (design doc 3.5: reflection is "defence-in-depth on
/// judgement", never the arbiter). A run that exhausted its reflection budget without either
/// critic approving still reaches the gate and can still be Approved; the attempt counts are for
/// cost observability, not a second pass/fail signal.
/// </summary>
public sealed record ReportCompositionResult(
    bool Approved,
    CompositionPlan FinalPlan,
    int CompositionAttempts,
    NarrativeResult FinalNarrative,
    int NarrativeAttempts,
    PublishGateResult GateResult);

/// <summary>
/// compose -> reflect on composition -> narrate -> reflect on narrative -> DETERMINISTIC GATE
/// (design doc 3.5), with both reflection loops bounded by <c>maxReflectionIterations</c>
/// (Agents:MaxReflectionIterations, default 2 - reflection consumes tokens, per the same doc).
///
/// Plain sequential class, not a Durable Task orchestrator - that substrate is Phase 1d step 11,
/// not yet built, same reasoning as FreeDigestPipeline. When it lands, each step here becomes an
/// activity; the orchestrator body must stay deterministic, so the LLM calls stay exactly where
/// they already are.
/// </summary>
public sealed class ReportCompositionPipeline(
    ICompositionAgent compositionAgent,
    ICompositionReflectionAgent compositionReflectionAgent,
    INarrativeAgent narrativeAgent,
    INarrativeReflectionAgent narrativeReflectionAgent,
    PublishGate publishGate,
    int maxReflectionIterations)
{
    public async Task<ReportCompositionResult> RunAsync(
        int userId,
        int customerId,
        IReadOnlyDictionary<string, object> dimensionResults,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string tenantShape,
        string reportType,
        CancellationToken cancellationToken = default)
    {
        var plan = await compositionAgent.ComposeAsync(dimensionResults, tenantShape, reportType, cancellationToken: cancellationToken);
        var compositionAttempts = 1;

        for (var i = 0; i < maxReflectionIterations; i++)
        {
            var reflection = await compositionReflectionAgent.ReflectAsync(plan, assertions, findings, tenantShape, cancellationToken);
            if (reflection.Verdict == ReflectionVerdict.Approve)
                break;

            plan = await compositionAgent.ComposeAsync(dimensionResults, tenantShape, reportType, (plan, reflection.Issues), cancellationToken);
            compositionAttempts++;
        }

        var narrative = await narrativeAgent.NarrateAsync(plan, assertions, findings, cancellationToken: cancellationToken);
        var narrativeAttempts = 1;

        for (var i = 0; i < maxReflectionIterations; i++)
        {
            var reflection = await narrativeReflectionAgent.ReflectAsync(narrative, assertions, findings, cancellationToken);
            if (reflection.Verdict == ReflectionVerdict.Approve)
                break;

            narrative = await narrativeAgent.NarrateAsync(plan, assertions, findings, (narrative, reflection.Issues), cancellationToken);
            narrativeAttempts++;
        }

        // The non-negotiable arbiter. Runs regardless of whether either reflection loop ever
        // approved - a run that spent its whole budget without agreement is not automatically a
        // refusal; only the gate decides that.
        var gateResult = await publishGate.EvaluateAsync(userId, customerId, narrative, assertions, cancellationToken);

        return new ReportCompositionResult(gateResult.Approved, plan, compositionAttempts, narrative, narrativeAttempts, gateResult);
    }
}

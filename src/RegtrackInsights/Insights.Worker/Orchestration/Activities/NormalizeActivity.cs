using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record NormalizeInput(string Html);
public sealed record NormalizeOutput(string Html);

/// <summary>
/// Node 9, called TWICE by the orchestrator (Task 14) - once before sanitizing, once after, to
/// catch DOMPurify's own serialization side effects (e.g. the DOCTYPE-drop bug already found and
/// fixed in DomPurifySanitizer - see its doc comment). Same activity class both times; the
/// orchestrator distinguishes the two calls only by which reason code a refusal there should
/// surface as, not by anything in this activity itself.
/// </summary>
public sealed class NormalizeActivity : AsyncTaskActivity<NormalizeInput, NormalizeOutput>
{
    protected override Task<NormalizeOutput> ExecuteAsync(TaskContext context, NormalizeInput input) => RunAsync(input);

    internal Task<NormalizeOutput> RunAsync(NormalizeInput input)
    {
        var result = ReportEmitNormalizer.Evaluate(input.Html);
        if (!result.Approved)
        {
            // [TEMP DIAGNOSTIC 2026-09-07] remove after tenant 29 manual run is diagnosed.
            Console.Error.WriteLine($"[DIAG] NOT_NORMALIZABLE violations: {string.Join(" | ", result.Violations)}");
            throw new OrchestrationRefusedException("NOT_NORMALIZABLE", "We couldn't generate this report to our accuracy standard. Our team has been notified.", result.Violations);
        }

        return Task.FromResult(new NormalizeOutput(input.Html));
    }
}

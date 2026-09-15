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
///
/// <paramref name="localFallbackDirectory"/> [ADDED 2026-09-15, THROWAWAY DIAGNOSTIC] - same
/// Reports:LocalFallbackDirectory config PersistActivity already reuses for its own stopgap, only
/// to decide WHERE to dump the rejected HTML, not to change any real behavior (unset/empty means
/// completely unchanged - no dump, same throw as before). Added to see what ReportEmitNormalizer
/// is actually rejecting on a real run - CLAUDE.md's own "never leak gate diagnostics" rule means
/// nothing about WHY normally survives past this exception, so there was no other way to inspect
/// a real failure. Revert once the current investigation is done - this is not meant to ship.
///
/// [INVESTIGATION, ONGOING 2026-09-15] Real, reproducible pattern across 5 live runs
/// (dimension_selection:Users, tenant 1008/Minda): 3 of 5 refused here on "expected exactly one
/// </html> closing tag, found 0" - the model's output just stops mid-document and the captured
/// HTML ends with a real content-refusal string, "I'm sorry, but I cannot assist with that
/// request." Both captured rejections cut off inside the per-user leaderboard section
/// (`.ur-puser` blocks), immediately after a real named employee's row, mid-way through the
/// on-time/overdue split bar markup that follows it. Ruled out: reasoning-summary request
/// on/off (MafAgentFactory's own history on this option) - failures happened both ways, no real
/// correlation. Not yet tested: whether this reproduces on OTHER tenants' Users reports, or only
/// Minda's; whether it is specific to real employee names as a category, or to something about
/// this tenant's particular row count/content; whether a shorter per-user leaderboard (fewer
/// named rows) changes the failure rate.
/// </summary>
public sealed class NormalizeActivity(string? localFallbackDirectory = null) : AsyncTaskActivity<NormalizeInput, NormalizeOutput>
{
    protected override Task<NormalizeOutput> ExecuteAsync(TaskContext context, NormalizeInput input) => RunAsync(input);

    internal Task<NormalizeOutput> RunAsync(NormalizeInput input)
    {
        var result = ReportEmitNormalizer.Evaluate(input.Html);
        if (!result.Approved)
        {
            if (!string.IsNullOrWhiteSpace(localFallbackDirectory))
            {
                Directory.CreateDirectory(localFallbackDirectory);
                var path = Path.Combine(localFallbackDirectory, $"REJECTED-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
                File.WriteAllText(path, input.Html);
                File.WriteAllLines(Path.ChangeExtension(path, ".violations.txt"), result.Violations);
            }

            throw new OrchestrationRefusedException("NOT_NORMALIZABLE", "We couldn't generate this report to our accuracy standard. Our team has been notified.", result.Violations);
        }

        return Task.FromResult(new NormalizeOutput(input.Html));
    }
}

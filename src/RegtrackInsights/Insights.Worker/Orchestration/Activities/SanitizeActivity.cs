using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record SanitizeInput(string Html);
public sealed record SanitizeOutput(string Html);

/// <summary>Node 10 - real DOMPurify in headless Chromium, via the shared IBrowser (Task 3).</summary>
public sealed class SanitizeActivity(IDomPurifySanitizer sanitizer) : AsyncTaskActivity<SanitizeInput, SanitizeOutput>
{
    protected override Task<SanitizeOutput> ExecuteAsync(TaskContext context, SanitizeInput input) => RunAsync(input);

    internal async Task<SanitizeOutput> RunAsync(SanitizeInput input)
    {
        var sanitized = await sanitizer.SanitizeAsync(input.Html, CancellationToken.None);
        return new SanitizeOutput(sanitized);
    }
}

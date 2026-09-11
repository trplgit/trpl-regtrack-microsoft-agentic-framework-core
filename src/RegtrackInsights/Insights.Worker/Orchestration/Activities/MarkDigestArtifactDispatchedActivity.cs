using DurableTask.Core;
using Insights.Data;

namespace Insights.Worker.Orchestration.Activities;

public sealed record MarkDigestArtifactDispatchedInput(string ArtifactId);

/// <summary>MONDAY, final step: informational stamp only - see FreeDigestArtifact's own doc comment. The per-recipient claim in InsightsFreeDigestLog is what actually guarantees at-most-once delivery, not this.</summary>
public sealed class MarkDigestArtifactDispatchedActivity(IFreeDigestArtifactRepository repository)
    : AsyncTaskActivity<MarkDigestArtifactDispatchedInput, object?>
{
    protected override async Task<object?> ExecuteAsync(TaskContext context, MarkDigestArtifactDispatchedInput input)
    {
        await repository.MarkDispatchedAsync(Guid.Parse(input.ArtifactId));
        return null;
    }
}

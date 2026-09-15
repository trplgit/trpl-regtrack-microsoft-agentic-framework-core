using DurableTask.Core;
using Insights.Data;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ReleaseDigestArtifactInput(string ArtifactId);

/// <summary>GENERATE phase failure path: hands a claimed-but-never-completed slot back so a later tick can retry this scope group.</summary>
public sealed class ReleaseDigestArtifactActivity(IFreeDigestArtifactRepository repository)
    : AsyncTaskActivity<ReleaseDigestArtifactInput, object?>
{
    protected override async Task<object?> ExecuteAsync(TaskContext context, ReleaseDigestArtifactInput input)
    {
        await repository.ReleaseAsync(Guid.Parse(input.ArtifactId));
        return null;
    }
}

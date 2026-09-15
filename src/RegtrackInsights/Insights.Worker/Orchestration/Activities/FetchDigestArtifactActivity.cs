using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record FetchDigestArtifactInput(FreeDigestArtifact Artifact);

public sealed record FetchDigestArtifactOutput(string Html);

/// <summary>MONDAY, node 2: decrypt one scope group's artifact back to HTML. Once per group, not per recipient - the whole reason artifacts are stored per scope group in the first place.</summary>
public sealed class FetchDigestArtifactActivity(IDigestArtifactStore store)
    : AsyncTaskActivity<FetchDigestArtifactInput, FetchDigestArtifactOutput>
{
    protected override async Task<FetchDigestArtifactOutput> ExecuteAsync(TaskContext context, FetchDigestArtifactInput input) =>
        new FetchDigestArtifactOutput(await store.ReadAsync(input.Artifact));
}

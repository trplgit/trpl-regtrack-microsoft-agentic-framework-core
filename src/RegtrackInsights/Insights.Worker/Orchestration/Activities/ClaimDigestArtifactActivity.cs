using DurableTask.Core;
using Insights.Data;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ClaimDigestArtifactInput(
    int TenantId, string WeekEnding, string ScopeSignature, int RepresentativeUserId, string TenantName);

public sealed record ClaimDigestArtifactOutput(bool Claimed, string? ArtifactId);

/// <summary>
/// GENERATE phase, node 1b: reserve this (tenant, week, scope group)'s artifact slot before the
/// expensive compose step runs. Claimed = false means either the artifact already exists (the
/// normal re-tick case - nothing to do) or another attempt currently holds it - either way, the
/// caller must not compose again. See sql/29's own header for the full reasoning.
/// </summary>
public sealed class ClaimDigestArtifactActivity(IFreeDigestArtifactRepository repository)
    : AsyncTaskActivity<ClaimDigestArtifactInput, ClaimDigestArtifactOutput>
{
    protected override Task<ClaimDigestArtifactOutput> ExecuteAsync(TaskContext context, ClaimDigestArtifactInput input) => RunAsync(input);

    internal async Task<ClaimDigestArtifactOutput> RunAsync(ClaimDigestArtifactInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        var result = await repository.ClaimAsync(
            input.TenantId, weekEnding, input.ScopeSignature, input.RepresentativeUserId, input.TenantName);

        return new ClaimDigestArtifactOutput(result.Claimed, result.ArtifactId?.ToString());
    }
}

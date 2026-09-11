using DurableTask.Core;
using Insights.Data;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistDigestArtifactInput(
    string ArtifactId, string Body, string Source, string TenantName, string WeekEnding, DateTime AsOfUtc,
    int RecipientCount);

public sealed record PersistDigestArtifactOutput(bool Success);

/// <summary>
/// GENERATE phase, node 3: render the full email shell (with the unsubscribe link left as a
/// sentinel - see FreeDigestEmailRenderer.RenderHtmlForArtifactAsync), encrypt it, write it to the
/// digest blob container, and mark the artifact slot complete.
///
/// Deliberately does NOT catch its own exceptions - a failure here must propagate so
/// FreeDigestGenerateOrchestrator releases the claim (see that class's own doc comment), the same
/// "release on failure so a later tick can retry" shape ComposeDigestActivity/SendDigestActivity
/// already use elsewhere in this pipeline.
/// </summary>
public sealed class PersistDigestArtifactActivity(
    IDigestArtifactStore store, IFreeDigestArtifactRepository repository, FreeDigestEmailRenderer renderer, FreeDigestSettings settings)
    : AsyncTaskActivity<PersistDigestArtifactInput, PersistDigestArtifactOutput>
{
    protected override Task<PersistDigestArtifactOutput> ExecuteAsync(TaskContext context, PersistDigestArtifactInput input) => RunAsync(input);

    internal async Task<PersistDigestArtifactOutput> RunAsync(PersistDigestArtifactInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        var html = await renderer.RenderHtmlForArtifactAsync(
            input.Body, input.TenantName, weekEnding.ToDateTime(TimeOnly.MinValue), settings.UpgradeUrl);

        var content = await store.WriteAsync(html);

        await repository.CompleteAsync(
            Guid.Parse(input.ArtifactId), input.AsOfUtc, input.Source.ToLowerInvariant(), input.RecipientCount,
            content.BlobContainer, content.BlobPath, content.EncryptedAesKey, content.KeyVaultObjectName, content.KeyVaultObjectVersion);

        return new PersistDigestArtifactOutput(true);
    }
}

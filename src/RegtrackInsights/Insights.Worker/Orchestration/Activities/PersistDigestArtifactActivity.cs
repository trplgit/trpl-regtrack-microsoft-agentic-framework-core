using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistDigestArtifactInput(
    int CustomerId, string ArtifactId, string Body, string Source, string TenantName, string WeekEnding, DateTime AsOfUtc,
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
        var artifactId = Guid.Parse(input.ArtifactId);

        // A replayed pre-deploy TaskScheduled history entry binds a missing positional member to
        // its default rather than throwing (see PersistDigestArtifactInput's serialization
        // contract) - CustomerId=0 or an empty ArtifactId would otherwise write silently under a
        // wrong path prefix instead of failing. Fail closed instead.
        if (input.CustomerId <= 0)
            throw new InvalidOperationException($"PersistDigestArtifactActivity: CustomerId must be positive, got {input.CustomerId}.");
        if (artifactId == Guid.Empty)
            throw new InvalidOperationException("PersistDigestArtifactActivity: ArtifactId must not be empty.");

        // Same week -> edition answer ComposeDigestActivity used, so the masthead names
        // the month and topic the body was written for.
        var html = await renderer.RenderHtmlForArtifactAsync(
            input.Body, input.TenantName, MonthlyDigestCalendar.For(weekEnding), settings.UpgradeUrl, settings.PortalUrl);

        if (!string.IsNullOrWhiteSpace(settings.DebugDumpHtmlDir))
            await DumpForDebuggingAsync(settings.DebugDumpHtmlDir, input.CustomerId, input.WeekEnding, artifactId, html);

        var identity = new DigestArtifactIdentity(input.CustomerId, weekEnding, artifactId);
        var content = await store.WriteAsync(html, identity);

        await repository.CompleteAsync(
            artifactId, input.AsOfUtc, input.Source.ToLowerInvariant(), input.RecipientCount,
            content.BlobContainer, content.BlobPath, content.EncryptedAesKey, content.KeyVaultObjectName, content.KeyVaultObjectVersion);

        return new PersistDigestArtifactOutput(true);
    }

    /// <summary>
    /// TESTING ONLY - see FreeDigestSettings.DebugDumpHtmlDir's own doc comment. Writes the exact
    /// same HTML that is about to be encrypted and stored, unencrypted, to a local file - so a
    /// manual GENERATE run can be opened directly in a browser without decrypting the blob.
    /// Deliberately does not throw on a write failure (permissions, missing drive, etc.) - a debug
    /// convenience must never fail the real artifact write it is riding alongside.
    /// </summary>
    private static async Task DumpForDebuggingAsync(string dir, int customerId, string weekEnding, Guid artifactId, string html)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"tenant-{customerId}-{weekEnding}-{artifactId}.html");
            await File.WriteAllTextAsync(path, html);
        }
        catch
        {
            // Best-effort only - see the doc comment above.
        }
    }
}

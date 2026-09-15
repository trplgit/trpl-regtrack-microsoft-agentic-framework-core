using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [PATH LAYOUT 2026-09-11] Digest blobs now land at
/// &lt;customerId&gt;/free_digest/&lt;yyyy&gt;/&lt;mm&gt;/&lt;artifactId&gt;.html.enc, mirroring the
/// paid pipeline (ADR: Free-digest blob path). These tests cover the identity the activity builds
/// for the store and the fail-closed guard against a replayed, pre-deploy history entry binding
/// CustomerId/ArtifactId to a default value (see PersistDigestArtifactInput's doc comment).
/// </summary>
public sealed class PersistDigestArtifactActivityTests
{
    private static readonly FreeDigestSettings Settings = new()
    {
        TokenCap = 1500,
        FromAddress = "noreply@example.invalid",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://placeholder.invalid/upgrade",
        UnsubscribeBaseUrl = "https://placeholder.invalid/unsubscribe",
        UnsubscribeSigningKey = "test-signing-key",
    };

    // Real templates (src/RegtrackInsights/templates) - the guard-failure tests below never reach
    // the renderer, but the happy-path test does a real render, same as production.
    private static readonly string TemplateDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RegtrackInsights", "templates");

    private static readonly FreeDigestEmailRenderer Renderer = new(TemplateDirectory);

    private static PersistDigestArtifactInput ValidInput(int customerId = 1300, string artifactId = "9f3c1e7a-4b2d-4f10-a1c2-e3d4f5b6a7c8") =>
        new(customerId, artifactId, Body: "fallback body", Source: "fallback", TenantName: "Acme Pvt Ltd",
            WeekEnding: "2026-09-06", AsOfUtc: new DateTime(2026, 9, 7, 3, 0, 0, DateTimeKind.Utc), RecipientCount: 3);

    [Fact]
    public async Task RunAsync_BuildsIdentityFromCustomerIdWeekEndingAndArtifactId()
    {
        var artifactId = Guid.Parse("9f3c1e7a-4b2d-4f10-a1c2-e3d4f5b6a7c8");

        DigestArtifactIdentity? seenIdentity = null;
        var store = new Mock<IDigestArtifactStore>();
        store.Setup(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<DigestArtifactIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<string, DigestArtifactIdentity, CancellationToken>((_, identity, _) => seenIdentity = identity)
            .ReturnsAsync(new DigestArtifactContent("insights-digests", "1300/free_digest/2026/09/9f3c1e7a4b2d4f10a1c2e3d4f5b6a7c8.html.enc",
                [1, 2, 3], "TRPLCryptoKey-001", "https://vault.azure.net/keys/TRPLCryptoKey-001/abc123"));

        var repository = new Mock<IFreeDigestArtifactRepository>();
        var activity = new PersistDigestArtifactActivity(store.Object, repository.Object, Renderer, Settings);

        var result = await activity.RunAsync(ValidInput(customerId: 1300, artifactId: artifactId.ToString()));

        Assert.True(result.Success);
        Assert.NotNull(seenIdentity);
        Assert.Equal(1300, seenIdentity!.CustomerId);
        Assert.Equal(new DateOnly(2026, 9, 6), seenIdentity.WeekEnding);
        Assert.Equal(artifactId, seenIdentity.ArtifactId);

        repository.Verify(r => r.CompleteAsync(
            artifactId, It.IsAny<DateTime>(), "fallback", 3,
            "insights-digests", "1300/free_digest/2026/09/9f3c1e7a4b2d4f10a1c2e3d4f5b6a7c8.html.enc",
            It.IsAny<byte[]>(), "TRPLCryptoKey-001", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ThrowsAndNeverWritesOrCompletes_WhenCustomerIdIsNotPositive()
    {
        var store = new Mock<IDigestArtifactStore>();
        var repository = new Mock<IFreeDigestArtifactRepository>();
        var activity = new PersistDigestArtifactActivity(store.Object, repository.Object, Renderer, Settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => activity.RunAsync(ValidInput(customerId: 0)));

        store.VerifyNoOtherCalls();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_ThrowsAndNeverWritesOrCompletes_WhenArtifactIdIsEmpty()
    {
        var store = new Mock<IDigestArtifactStore>();
        var repository = new Mock<IFreeDigestArtifactRepository>();
        var activity = new PersistDigestArtifactActivity(store.Object, repository.Object, Renderer, Settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => activity.RunAsync(ValidInput(artifactId: Guid.Empty.ToString())));

        store.VerifyNoOtherCalls();
        repository.VerifyNoOtherCalls();
    }
}

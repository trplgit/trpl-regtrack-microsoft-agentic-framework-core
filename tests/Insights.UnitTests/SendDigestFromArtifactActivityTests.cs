using System.Net;
using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The send activity's outcomes: claim won, so deliver; claim lost, so do not; and (2026-09-25)
/// the per-tenant provider and the per-status-code failure handling.
///
/// Ported from the legacy SendDigestActivityTests (SendDigestActivity was removed once no
/// FreeDigestOrchestrator instances remained in-flight in the task hub) - the claim shape is
/// identical, so the same regression coverage applies here.
/// </summary>
public sealed class SendDigestFromArtifactActivityTests
{
    private static FreeDigestSettings Settings() => new()
    {
        FromAddress = "noreply@example.com",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://example.com/upgrade",
        UnsubscribeBaseUrl = "https://example.com/unsubscribe",
        UnsubscribeSigningKey = "test-key",
    };

    private static SendDigestFromArtifactInput Input(int? gatewayId = (int)EmailGateway.ElasticEmail) => new(
        TenantId: 23, TenantName: "ABC Training", UserId: 357,
        Email: "someone@example.com", Name: "Someone",
        ArtifactHtml: $"<html><body>Your compliance calendar. {FreeDigestEmailRenderer.UnsubscribeSentinel}</body></html>",
        WeekEnding: "2026-08-23",
        EmailGatewayId: gatewayId);

    private sealed record Harness(
        SendDigestFromArtifactActivity Activity,
        Mock<IFreeDigestRepository> Repo,
        Mock<IEmailSender> Elastic,
        Mock<IEmailSender> SendGrid,
        Mock<IEmailGatewayResolver> Resolver);

    private static Harness Build(bool claimWon)
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.TryClaimSendAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(claimWon);

        var elastic = new Mock<IEmailSender>();
        elastic.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult("ElasticEmail"));

        var sendGrid = new Mock<IEmailSender>();
        sendGrid.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult("SendGrid"));

        var registry = new EmailSenderRegistry(new Dictionary<EmailGateway, IEmailSender>
        {
            [EmailGateway.ElasticEmail] = elastic.Object,
            [EmailGateway.SendGrid] = sendGrid.Object,
        });

        var resolver = new Mock<IEmailGatewayResolver>(MockBehavior.Strict);

        var activity = new SendDigestFromArtifactActivity(
            repo.Object, registry, resolver.Object, Settings(), new FreeDigestMetrics(),
            NullLogger<SendDigestFromArtifactActivity>.Instance);

        return new Harness(activity, repo, elastic, sendGrid, resolver);
    }

    private static EmailProviderException ProviderError(HttpStatusCode status) =>
        new("ElasticEmail", status, "{\"Error\":\"provider says no\"}");

    /// <summary>THE REGRESSION. A won claim must actually send.</summary>
    [Fact]
    public async Task WhenTheClaimIsWon_ItSendsAndRecordsTheOutcome()
    {
        var h = Build(claimWon: true);

        var result = await h.Activity.RunAsync(Input());

        Assert.True(result.Sent);
        Assert.False(result.Failed);
        Assert.Equal("ElasticEmail", result.ProviderUsed);
        h.Elastic.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Repo.Verify(r => r.RecordOutcomeAsync(23, 357, It.IsAny<DateOnly>(), "sent", null, "ElasticEmail",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A lost claim must NOT send - that is the once-per-week guarantee.</summary>
    [Fact]
    public async Task WhenTheClaimIsLost_ItSendsNothing()
    {
        var h = Build(claimWon: false);

        var result = await h.Activity.RunAsync(Input());

        Assert.False(result.Sent);
        Assert.False(result.Failed);
        Assert.Contains("already sent", result.Reason);
        h.Elastic.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The tenant's resolved gateway picks the provider - SendGrid here, Elastic untouched.</summary>
    [Fact]
    public async Task ItSendsThroughTheTenantsGateway()
    {
        var h = Build(claimWon: true);

        var result = await h.Activity.RunAsync(Input((int)EmailGateway.SendGrid));

        Assert.True(result.Sent);
        Assert.Equal("SendGrid", result.ProviderUsed);
        h.SendGrid.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Elastic.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An input recorded before per-tenant routing carries no gateway. It is resolved LIVE -
    /// never assumed to be the default provider.
    /// </summary>
    [Fact]
    public async Task WhenTheInputHasNoGateway_ItResolvesItLive()
    {
        var h = Build(claimWon: true);
        h.Resolver.Setup(r => r.ResolveAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.TenantRow, "row", []));

        var result = await h.Activity.RunAsync(Input(gatewayId: null));

        Assert.True(result.Sent);
        Assert.Equal("SendGrid", result.ProviderUsed);
    }

    [Fact]
    public async Task WhenTheInputHasNoGatewayAndTheLiveLookupIsRefused_NothingIsClaimedOrSent()
    {
        var h = Build(claimWon: true);
        h.Resolver.Setup(r => r.ResolveAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailGatewayResolution(null, EmailGatewaySource.Refused, "bad row", []));

        var result = await h.Activity.RunAsync(Input(gatewayId: null));

        Assert.False(result.Sent);
        Assert.True(result.Failed);
        h.Repo.Verify(r => r.TryClaimSendAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Elastic.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// 400 / 403 / 422: the provider rejected THIS message. Recorded as failed with the provider's
    /// own error, returned (not thrown - so no retries), and the claim KEPT so the rest of this
    /// week's run does not try it again.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task WhenTheProviderRejectsTheMessage_ItRecordsFailedAndDoesNotThrow(HttpStatusCode status)
    {
        var h = Build(claimWon: true);
        h.Elastic.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ProviderError(status));

        var result = await h.Activity.RunAsync(Input());

        Assert.False(result.Sent);
        Assert.True(result.Failed);
        Assert.StartsWith(((int)status).ToString(), result.Reason);
        h.Repo.Verify(r => r.RecordOutcomeAsync(23, 357, It.IsAny<DateOnly>(), "failed", null, "ElasticEmail",
            It.Is<string>(d => d.Contains("provider says no")), It.IsAny<CancellationToken>()), Times.Once);
        h.Repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// 401 / 429 / 5xx and non-provider failures may clear up: the claim is handed back and the
    /// exception rethrown so the orchestrator's RetryOptions try again.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task WhenTheFailureMayClearUp_ItReleasesTheClaimAndRethrows(HttpStatusCode status)
    {
        var h = Build(claimWon: true);
        h.Elastic.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ProviderError(status));

        await Assert.ThrowsAsync<EmailProviderException>(() => h.Activity.RunAsync(Input()));

        h.Repo.Verify(r => r.ReleaseClaimAsync(23, 357, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Repo.Verify(r => r.RecordOutcomeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), "failed",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A failed send hands the claim back, or a transient provider error would silently consume
    /// that recipient's only attempt for the week.
    /// </summary>
    [Fact]
    public async Task WhenTheSendThrows_ItReleasesTheClaimAndRethrows()
    {
        var h = Build(claimWon: true);
        h.Elastic.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Activity.RunAsync(Input()));

        h.Repo.Verify(r => r.ReleaseClaimAsync(23, 357, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>If the 'failed' row itself cannot be written, the claim is released so it does not read as "already sent".</summary>
    [Fact]
    public async Task WhenRecordingThePermanentFailureFails_ItReleasesTheClaimAndRethrows()
    {
        var h = Build(claimWon: true);
        h.Elastic.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ProviderError(HttpStatusCode.BadRequest));
        h.Repo.Setup(r => r.RecordOutcomeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), "failed",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("sql blip"));

        await Assert.ThrowsAsync<TimeoutException>(() => h.Activity.RunAsync(Input()));

        h.Repo.Verify(r => r.ReleaseClaimAsync(23, 357, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The send activity's two outcomes: claim won, so deliver; claim lost, so do not.
///
/// These exist because a missing pair of braces once made the "already sent" return
/// unconditional - the claim succeeded, the row was written, and the method returned as though
/// somebody else had it. Valid C#, no compiler warning, and invisible in the orchestration output
/// beyond "Sent: 0". A test at this level is the only thing that catches it.
/// </summary>
public sealed class SendDigestActivityTests
{
    private static FreeDigestSettings Settings() => new()
    {
        TokenCap = 1500,
        FromAddress = "noreply@example.com",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://example.com/upgrade",
        UnsubscribeBaseUrl = "https://example.com/unsubscribe",
        UnsubscribeSigningKey = "test-key",
    };

    private static SendDigestInput Input() => new(
        TenantId: 23, TenantName: "ABC Training", UserId: 357,
        Email: "someone@example.com", Name: "Someone",
        Body: "Your compliance calendar.", Source: "Llm", WeekEnding: "2026-08-23");

    private static (SendDigestActivity Activity, Mock<IFreeDigestRepository> Repo, Mock<IEmailSender> Sender) Build(bool claimWon)
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.TryClaimSendAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(claimWon);

        var sender = new Mock<IEmailSender>();
        sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult("ElasticEmail"));

        var activity = new SendDigestActivity(
            repo.Object, new FreeDigestEmailRenderer("templates"), sender.Object, Settings(), new FreeDigestMetrics());

        return (activity, repo, sender);
    }

    /// <summary>THE REGRESSION. A won claim must actually send.</summary>
    [Fact]
    public async Task WhenTheClaimIsWon_ItSendsAndRecordsTheOutcome()
    {
        var (activity, repo, sender) = Build(claimWon: true);

        var result = await activity.RunAsync(Input());

        Assert.True(result.Sent);
        Assert.Equal("ElasticEmail", result.ProviderUsed);
        sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.RecordOutcomeAsync(23, 357, It.IsAny<DateOnly>(), "sent", "llm", "ElasticEmail",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A lost claim must NOT send - that is the once-per-week guarantee.</summary>
    [Fact]
    public async Task WhenTheClaimIsLost_ItSendsNothing()
    {
        var (activity, _, sender) = Build(claimWon: false);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Sent);
        Assert.Contains("already sent", result.Reason);
        sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A failed send hands the claim back, or a transient provider error would silently consume
    /// that recipient's only attempt for the week.
    /// </summary>
    [Fact]
    public async Task WhenTheSendThrows_ItReleasesTheClaimAndRethrows()
    {
        var (activity, repo, sender) = Build(claimWon: true);
        sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => activity.RunAsync(Input()));

        repo.Verify(r => r.ReleaseClaimAsync(23, 357, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

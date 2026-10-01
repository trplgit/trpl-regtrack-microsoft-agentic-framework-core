using Insights.Data;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>After every retry fails, the recipient is recorded 'failed' for THIS week only - nothing is suppressed.</summary>
public sealed class RecordDigestSendFailedActivityTests
{
    private static readonly RecordDigestSendFailedInput Input = new(23, 357, "2026-08-23", "SendGrid", "failed after 3 attempts: 503");

    [Fact]
    public async Task ItReclaimsTheReleasedSlotAndRecordsFailed()
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.TryClaimSendAsync(23, 357, new DateOnly(2026, 8, 23), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var recorded = await new RecordDigestSendFailedActivity(repo.Object, NullLogger<RecordDigestSendFailedActivity>.Instance).RunAsync(Input);

        Assert.True(recorded);
        repo.Verify(r => r.RecordOutcomeAsync(23, 357, new DateOnly(2026, 8, 23), "failed", null, "SendGrid",
            "failed after 3 attempts: 503", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SuppressAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Insights.Domain.DigestSuppressionReason>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Someone else already holds this week's row (sent or recorded meanwhile) - that record stands.</summary>
    [Fact]
    public async Task WhenTheSlotIsAlreadyHeld_ItLeavesTheExistingRecord()
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.TryClaimSendAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var recorded = await new RecordDigestSendFailedActivity(repo.Object, NullLogger<RecordDigestSendFailedActivity>.Instance).RunAsync(Input);

        Assert.False(recorded);
        repo.Verify(r => r.RecordOutcomeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

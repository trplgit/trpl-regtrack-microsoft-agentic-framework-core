using DurableTask.Core;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The digest orchestration's contract: one compose per SCOPE GROUP, one send per RECIPIENT.
/// That ratio is the cost model (design doc 10.5), and it is provable here without an LLM call.
/// </summary>
public sealed class FreeDigestOrchestratorTests
{
    private static Mock<OrchestrationContext> ContextWith(ResolveDigestRecipientsOutput resolved)
    {
        var context = new Mock<OrchestrationContext>();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));

        context.Setup(c => c.ScheduleTask<ResolveDigestRecipientsOutput>(
                typeof(ResolveDigestRecipientsActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync(resolved);

        context.Setup(c => c.ScheduleWithRetry<ComposeDigestOutput>(
                typeof(ComposeDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new ComposeDigestOutput("body", "Llm", null));

        context.Setup(c => c.ScheduleWithRetry<SendDigestOutput>(
                typeof(SendDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new SendDigestOutput(true, null, "ElasticEmail"));

        return context;
    }

    private static DigestRecipientRef Recipient(long id) => new(id, $"user{id}@example.com", $"User {id}");

    /// <summary>
    /// THE COST FIX, PROVEN. Five recipients sharing one scope must produce ONE compose call and
    /// FIVE sends. Per-recipient composition would be five LLM calls - the shape that breaks
    /// 10.5's ~30M tokens/year budget on a single large tenant.
    /// </summary>
    [Fact]
    public async Task FiveRecipientsInOneScopeGroup_ComposeOnce_SendFiveTimes()
    {
        var resolved = new ResolveDigestRecipientsOutput(
            true, "Proceed", "Entitled.", "ABC Training", "2026-08-23",
            [new DigestScopeGroup("sig-a", 357, [Recipient(1), Recipient(2), Recipient(3), Recipient(4), Recipient(5)])],
            RecipientsWithoutScope: 0);

        var context = ContextWith(resolved);

        var result = await new FreeDigestOrchestrator().RunTask(context.Object, new FreeDigestOrchestrationInput(23, null));

        Assert.Equal(1, result.LlmCalls);
        Assert.Equal(5, result.Sent);

        context.Verify(c => c.ScheduleWithRetry<ComposeDigestOutput>(
            typeof(ComposeDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()), Times.Once);
        context.Verify(c => c.ScheduleWithRetry<SendDigestOutput>(
            typeof(SendDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()), Times.Exactly(5));
    }

    /// <summary>Distinct scopes genuinely need distinct numbers, so they get their own call each.</summary>
    [Fact]
    public async Task TwoScopeGroups_ComposeTwice()
    {
        var resolved = new ResolveDigestRecipientsOutput(
            true, "Proceed", "Entitled.", "ABC Training", "2026-08-23",
            [
                new DigestScopeGroup("sig-a", 357, [Recipient(1), Recipient(2)]),
                new DigestScopeGroup("sig-b", 645, [Recipient(3)]),
            ],
            RecipientsWithoutScope: 0);

        var context = ContextWith(resolved);

        var result = await new FreeDigestOrchestrator().RunTask(context.Object, new FreeDigestOrchestrationInput(23, null));

        Assert.Equal(2, result.LlmCalls);
        Assert.Equal(3, result.Sent);
        Assert.Equal(2, result.ScopeGroups);
    }

    /// <summary>
    /// A refused tenant must cost nothing beyond the gate - no compose, no send. That is the
    /// cheapest-first property the gate exists for.
    /// </summary>
    [Fact]
    public async Task SupersededTenant_ComposesNothingAndSendsNothing()
    {
        var resolved = new ResolveDigestRecipientsOutput(
            false, "ExitSuperseded", "Paid tier active.", "ABCD Pvt Ltd", "2026-08-23", [], 0);

        var context = ContextWith(resolved);

        var result = await new FreeDigestOrchestrator().RunTask(context.Object, new FreeDigestOrchestrationInput(29, null));

        Assert.Equal("ExitSuperseded", result.Decision);
        Assert.Equal(0, result.LlmCalls);
        Assert.Equal(0, result.Sent);

        context.Verify(c => c.ScheduleWithRetry<ComposeDigestOutput>(
            typeof(ComposeDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()), Times.Never);
        context.Verify(c => c.ScheduleWithRetry<SendDigestOutput>(
            typeof(SendDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()), Times.Never);
    }

    /// <summary>An already-claimed recipient counts as skipped, not sent - the idempotency path.</summary>
    [Fact]
    public async Task AlreadyClaimedRecipient_CountsAsSkipped()
    {
        var resolved = new ResolveDigestRecipientsOutput(
            true, "Proceed", "Entitled.", "ABC Training", "2026-08-23",
            [new DigestScopeGroup("sig-a", 357, [Recipient(1)])], 0);

        var context = ContextWith(resolved);
        context.Setup(c => c.ScheduleWithRetry<SendDigestOutput>(
                typeof(SendDigestActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new SendDigestOutput(false, "already sent for week ending 2026-08-23", null));

        var result = await new FreeDigestOrchestrator().RunTask(context.Object, new FreeDigestOrchestrationInput(23, null));

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Skipped);
    }
}

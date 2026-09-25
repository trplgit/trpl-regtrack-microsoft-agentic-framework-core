using DurableTask.Core;
using DurableTask.Core.Exceptions;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// 1.1 (2026-09-25): one recipient failing on every retry is recorded and skipped past - it no
/// longer faults the orchestration and costs every later recipient their week's email.
/// </summary>
public sealed class FreeDigestSendOrchestratorTests
{
    private static FreeDigestArtifact Artifact() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), 23, new DateOnly(2026, 8, 23), "sig", 1, "ABC Training",
        DateTime.UtcNow, DateTime.UtcNow, "llm", "c", "p", [], "k", "v");

    private static Mock<OrchestrationContext> Context(int recipientCount, int? gatewayId, out List<SendDigestFromArtifactInput> sendInputs)
    {
        var recipients = Enumerable.Range(1, recipientCount)
            .Select(i => new DispatchRecipientRef(i, $"user{i}@example.com", null)).ToList();

        var context = new Mock<OrchestrationContext>();
        context.Setup(c => c.ScheduleWithRetry<ResolveDigestDispatchOutput>(typeof(ResolveDigestDispatchActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new ResolveDigestDispatchOutput(true, "Proceed", "ok", "ABC Training",
                [new DispatchGroup(Artifact(), recipients)], 0, 0, gatewayId));
        context.Setup(c => c.ScheduleWithRetry<FetchDigestArtifactOutput>(typeof(FetchDigestArtifactActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new FetchDigestArtifactOutput("<html></html>"));
        context.Setup(c => c.ScheduleTask<object?>(typeof(MarkDigestArtifactDispatchedActivity).Name, "1.0", It.IsAny<object[]>()))
            .ReturnsAsync((object?)null);
        context.Setup(c => c.ScheduleWithRetry<bool>(typeof(RecordDigestSendFailedActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var captured = new List<SendDigestFromArtifactInput>();
        sendInputs = captured;

        // Recipient 10 fails on every attempt (what ScheduleWithRetry surfaces once retries are
        // exhausted); recipient 12 is a permanent rejection the activity itself recorded.
        context.Setup(c => c.ScheduleWithRetry<SendDigestFromArtifactOutput>(typeof(SendDigestFromArtifactActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .Returns((string _, string _, RetryOptions _, object[] args) =>
            {
                var sendInput = (SendDigestFromArtifactInput)args[0];
                captured.Add(sendInput);

                return sendInput.UserId switch
                {
                    10 => Task.FromException<SendDigestFromArtifactOutput>(
                        new TaskFailedException("SendDigestFromArtifactActivity failed", new HttpRequestException("503 Service Unavailable"))),
                    12 => Task.FromResult(new SendDigestFromArtifactOutput(false, "400: bad address", "SendGrid", Failed: true)),
                    _ => Task.FromResult(new SendDigestFromArtifactOutput(true, null, "SendGrid")),
                };
            });

        return context;
    }

    [Fact]
    public async Task ARecipientFailingEveryRetry_IsRecordedAndEveryoneAfterThemStillGetsTheirEmail()
    {
        // 60 recipients = three batches of 25/25/10; the failure sits in the FIRST batch, so the
        // old behaviour would have lost the next two batches entirely.
        var context = Context(60, (int)EmailGateway.SendGrid, out var sendInputs);

        var result = await new FreeDigestSendOrchestrator().RunTask(context.Object, new FreeDigestSendOrchestrationInput(23, 3));

        Assert.Equal(60, sendInputs.Count);
        Assert.Equal(58, result.Sent);
        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Skipped);

        context.Verify(c => c.ScheduleWithRetry<bool>(typeof(RecordDigestSendFailedActivity).Name, "1.0", It.IsAny<RetryOptions>(),
            It.Is<object[]>(a => ((RecordDigestSendFailedInput)a[0]).UserId == 10
                                 && ((RecordDigestSendFailedInput)a[0]).ProviderUsed == "SendGrid"
                                 && ((RecordDigestSendFailedInput)a[0]).Detail.Contains("503"))), Times.Once);

        // The permanent rejection was already recorded by the activity - not recorded twice.
        context.Verify(c => c.ScheduleWithRetry<bool>(typeof(RecordDigestSendFailedActivity).Name, "1.0", It.IsAny<RetryOptions>(),
            It.Is<object[]>(a => ((RecordDigestSendFailedInput)a[0]).UserId == 12)), Times.Never);

        context.Verify(c => c.ScheduleTask<object?>(typeof(MarkDigestArtifactDispatchedActivity).Name, "1.0", It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task TheResolvedGatewayIsCarriedIntoEverySend()
    {
        var context = Context(3, (int)EmailGateway.SendGrid, out var sendInputs);

        await new FreeDigestSendOrchestrator().RunTask(context.Object, new FreeDigestSendOrchestrationInput(23, 3));

        Assert.All(sendInputs, s => Assert.Equal((int)EmailGateway.SendGrid, s.EmailGatewayId));
    }

    [Fact]
    public async Task EvenIfRecordingTheFailureFails_TheRunStillCompletes()
    {
        var context = Context(12, (int)EmailGateway.ElasticEmail, out _);
        context.Setup(c => c.ScheduleWithRetry<bool>(typeof(RecordDigestSendFailedActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ThrowsAsync(new TaskFailedException("RecordDigestSendFailedActivity failed", new TimeoutException()));

        var result = await new FreeDigestSendOrchestrator().RunTask(context.Object, new FreeDigestSendOrchestrationInput(23, 3));

        Assert.Equal(10, result.Sent);
        Assert.Equal(2, result.Failed);
    }

    [Fact]
    public async Task ARefusedGateway_SendsNothing()
    {
        var context = new Mock<OrchestrationContext>(MockBehavior.Strict);
        context.Setup(c => c.ScheduleWithRetry<ResolveDigestDispatchOutput>(typeof(ResolveDigestDispatchActivity).Name, "1.0", It.IsAny<RetryOptions>(), It.IsAny<object[]>()))
            .ReturnsAsync(new ResolveDigestDispatchOutput(false, ResolveDigestDispatchActivity.EmailGatewayRefusedDecision, "bad row", "ABC Training", [], 0, 0));

        var result = await new FreeDigestSendOrchestrator().RunTask(context.Object, new FreeDigestSendOrchestrationInput(23, 3));

        Assert.Equal(ResolveDigestDispatchActivity.EmailGatewayRefusedDecision, result.Decision);
        Assert.Equal(0, result.Sent);
    }
}

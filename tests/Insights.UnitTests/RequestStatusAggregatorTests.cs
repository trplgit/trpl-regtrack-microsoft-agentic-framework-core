using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// Rolls up several sub-report statuses (the N runIds under one fan-out reqId) into one combined
/// status for the frontend. Worst-first priority (2026-09-11 product decision): a single failure
/// anywhere surfaces as "error" immediately, even while other dimensions are still running or
/// already done - never hidden behind an optimistic "in_progress".
/// </summary>
public sealed class RequestStatusAggregatorTests
{
    [Fact]
    public void Aggregate_AllQueued_ReturnsQueued()
    {
        var result = RequestStatusAggregator.Aggregate(["queued", "queued", "queued"]);

        Assert.Equal("queued", result);
    }

    [Fact]
    public void Aggregate_AllComplete_ReturnsCompleted()
    {
        var result = RequestStatusAggregator.Aggregate(["complete", "complete"]);

        Assert.Equal("completed", result);
    }

    [Fact]
    public void Aggregate_AnyFailed_ReturnsError_EvenWithOthersStillRunning()
    {
        var result = RequestStatusAggregator.Aggregate(["complete", "running", "failed"]);

        Assert.Equal("error", result);
    }

    [Fact]
    public void Aggregate_AnyRunning_ReturnsInProgress_EvenWithSomeStillQueued()
    {
        var result = RequestStatusAggregator.Aggregate(["queued", "running", "complete"]);

        Assert.Equal("in_progress", result);
    }

    [Fact]
    public void Aggregate_SomeQueuedSomeComplete_NoneRunningOrFailed_ReturnsQueued()
    {
        // Worker has not picked the rest up yet - not "completed" (not everything is done) and
        // not "in_progress" (nothing is actually running right now).
        var result = RequestStatusAggregator.Aggregate(["complete", "queued", "complete"]);

        Assert.Equal("queued", result);
    }

    [Theory]
    [InlineData("queued", "queued")]
    [InlineData("running", "in_progress")]
    [InlineData("complete", "completed")]
    [InlineData("failed", "error")]
    public void Aggregate_SingleSubStatus_MapsToItsOwnAggregateValue(string subStatus, string expected)
    {
        var result = RequestStatusAggregator.Aggregate([subStatus]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Aggregate_FailedBeatsRunning_WorstFirstPriority()
    {
        var result = RequestStatusAggregator.Aggregate(["running", "failed"]);

        Assert.Equal("error", result);
    }

    [Fact]
    public void Aggregate_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => RequestStatusAggregator.Aggregate([]));
    }
}

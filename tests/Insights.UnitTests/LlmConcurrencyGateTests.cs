using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// Item 17 (design doc Sec.4.4) - "concurrency is governed by a token-bucket / semaphore sized to
/// LLM rate limits, not by worker count." These pin the semaphore half: a burst past the cap waits
/// rather than running unbounded, a released slot is handed to the next waiter, and a slot is
/// never leaked - not on normal completion, not on an exception, not on cancellation.
/// </summary>
public sealed class LlmConcurrencyGateTests
{
    [Fact]
    public async Task NeverLetsMoreThanTheCap_HoldASlotAtOnce()
    {
        const int cap = 3;
        const int callers = 10;
        var gate = new LlmConcurrencyGate(cap);

        var current = 0;
        var completed = 0;
        var release = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, callers).Select(async _ =>
        {
            using var _token = await gate.AcquireAsync(CancellationToken.None);
            Interlocked.Increment(ref current);
            await release.Task; // hold the slot until the test says go
            Interlocked.Decrement(ref current);
            Interlocked.Increment(ref completed);
        }).ToArray();

        // Poll rather than a flat sleep - bounded, not timing-guessed.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref current) < cap && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(cap, Volatile.Read(ref current));

        // Give the other (callers - cap) waiters a real chance to sneak through if the gate were
        // broken - they must NOT, since none have been released yet.
        await Task.Delay(100);
        Assert.Equal(cap, Volatile.Read(ref current));

        release.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(callers, completed);
    }

    [Fact]
    public async Task ReleasingASlot_LetsTheNextWaiterThrough()
    {
        var gate = new LlmConcurrencyGate(1);

        var first = await gate.AcquireAsync(CancellationToken.None);
        var secondAcquired = false;
        var secondTask = Task.Run(async () =>
        {
            using var _ = await gate.AcquireAsync(CancellationToken.None);
            secondAcquired = true;
        });

        await Task.Delay(100);
        Assert.False(secondAcquired); // still held by `first`

        first.Dispose();
        await secondTask;

        Assert.True(secondAcquired);
    }

    /// <summary>
    /// [TRAP] A caller that throws INSIDE the `using` block must still release - otherwise one
    /// failed LLM call permanently shrinks the gate's capacity for every run after it.
    /// </summary>
    [Fact]
    public async Task ReleasesTheSlot_EvenWhenTheGuardedWorkThrows()
    {
        var gate = new LlmConcurrencyGate(1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var _ = await gate.AcquireAsync(CancellationToken.None);
            throw new InvalidOperationException("simulated LLM call failure");
        });

        // If the slot leaked, this would hang - AcquireAsync would never return.
        using var _token = await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireAsync_RespectsCancellation_WhileWaiting()
    {
        var gate = new LlmConcurrencyGate(1);
        using var held = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.AcquireAsync(cts.Token));
    }

    [Fact]
    public void RejectsANonPositiveCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmConcurrencyGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmConcurrencyGate(-1));
    }

    /// <summary>
    /// Design doc Sec.4.4, item 1: "a human is waiting. Always drains first." The core claim of
    /// the whole priority-lane feature - an interactive call that arrives AFTER a batch call is
    /// already queued must still be handed the next freed slot ahead of it. Enqueue order into
    /// LlmConcurrencyGate's waiter list happens synchronously inside AcquireAsync (before its
    /// first real await), so by the time each AcquireAsync call below returns a Task, that waiter
    /// is already sitting in its queue - no race to guard against here.
    /// </summary>
    [Fact]
    public async Task InteractiveWaiter_JumpsAheadOfAnAlreadyQueuedBatchWaiter()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        var batchTask = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        var interactiveTask = gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        held.Dispose();

        var interactiveToken = await interactiveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(batchTask.IsCompleted); // still behind - the freed slot went to interactive, not to whoever queued first

        interactiveToken.Dispose();
        var batchToken = await batchTask.WaitAsync(TimeSpan.FromSeconds(5));
        batchToken.Dispose();
    }

    /// <summary>Batch is not starved outright - only deprioritised. With no interactive waiter contending, it drains normally.</summary>
    [Fact]
    public async Task BatchWaiter_IsServed_WhenNoInteractiveWaiterIsQueued()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        var batchTask = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        held.Dispose();

        var token = await batchTask.WaitAsync(TimeSpan.FromSeconds(5));
        token.Dispose();
    }

    /// <summary>Within one lane, priority does not also reorder - two batch jobs still drain in arrival order.</summary>
    [Fact]
    public async Task WithinTheSameLane_WaitersDrainInArrivalOrder()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);

        var first = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        var second = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        var third = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);

        held.Dispose();
        var firstToken = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        firstToken.Dispose();
        var secondToken = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);

        secondToken.Dispose();
        var thirdToken = await third.WaitAsync(TimeSpan.FromSeconds(5));
        thirdToken.Dispose();
    }

    /// <summary>
    /// [TRAP this guards] Cancelling a MID-queue waiter must remove exactly that node and leave
    /// the rest of its own lane's queue intact - a naive implementation that rebuilt the queue
    /// incorrectly, or that let Release() still hand a slot to an already-canceled TCS, would
    /// either lose a later waiter or leak a slot nobody claims.
    /// </summary>
    [Fact]
    public async Task CancellingAMidQueueWaiter_LeavesLaterWaitersInTheSameLaneIntact()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var toCancel = gate.AcquireAsync(LlmCallPriority.Batch, cts.Token);
        var survivor = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => toCancel);

        held.Dispose();
        var survivorToken = await survivor.WaitAsync(TimeSpan.FromSeconds(5));
        survivorToken.Dispose();
    }

    /// <summary>Cancelling a queued BATCH waiter must not disturb the INTERACTIVE lane's own queue.</summary>
    [Fact]
    public async Task CancellingAQueuedBatchWaiter_DoesNotDisturbTheInteractiveLane()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var canceledBatch = gate.AcquireAsync(LlmCallPriority.Batch, cts.Token);
        var interactive = gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceledBatch);

        held.Dispose();
        var interactiveToken = await interactive.WaitAsync(TimeSpan.FromSeconds(5));
        interactiveToken.Dispose();
    }

    /// <summary>The no-priority overload is not a third behaviour - it is Interactive, same as every pre-priority-lane call site meant.</summary>
    [Fact]
    public async Task NoPriorityOverload_BehavesAsInteractive()
    {
        var gate = new LlmConcurrencyGate(1);
        var held = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);

        var batchTask = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        var plainTask = gate.AcquireAsync(CancellationToken.None); // no priority specified

        held.Dispose();

        var plainToken = await plainTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(batchTask.IsCompleted);
        plainToken.Dispose();

        var batchToken = await batchTask.WaitAsync(TimeSpan.FromSeconds(5));
        batchToken.Dispose();
    }

    /// <summary>
    /// The read-only surface LlmConcurrencyGateMetrics polls (CONFIGURATION.md's
    /// insights.queue.depth{lane} / insights.governor.saturation_pct). Pinned directly against the
    /// gate, independent of OTel plumbing - if this drifts, the gauges silently report nonsense.
    /// </summary>
    [Fact]
    public async Task ObservabilitySurface_ReflectsCapacityAndQueueDepthAccurately()
    {
        var gate = new LlmConcurrencyGate(2);
        Assert.Equal(2, gate.Capacity);
        Assert.Equal(2, gate.AvailableSlots);
        Assert.Equal(0, gate.InteractiveQueueDepth);
        Assert.Equal(0, gate.BatchQueueDepth);
        Assert.Null(gate.OldestBatchWaitTime);

        var first = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);
        var second = await gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        Assert.Equal(0, gate.AvailableSlots); // both of the 2 slots taken, none queued yet

        var queuedInteractive = gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None);
        var queuedBatch = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None);
        Assert.Equal(1, gate.InteractiveQueueDepth);
        Assert.Equal(1, gate.BatchQueueDepth);
        Assert.NotNull(gate.OldestBatchWaitTime);
        Assert.True(gate.OldestBatchWaitTime >= TimeSpan.Zero);

        first.Dispose();
        second.Dispose();
        (await queuedInteractive).Dispose();
        (await queuedBatch).Dispose();

        Assert.Equal(2, gate.AvailableSlots);
        Assert.Equal(0, gate.InteractiveQueueDepth);
        Assert.Equal(0, gate.BatchQueueDepth);
        Assert.Null(gate.OldestBatchWaitTime); // no batch waiter left - nothing to report a wait time for
    }
}

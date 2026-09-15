using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Item 17 (design doc Sec.4.4) - "concurrency is governed by a token-bucket / semaphore sized to
/// LLM rate limits, not by worker count." Caps how many LLM calls are in flight AT ONCE across the
/// whole paid pipeline, so a burst of simultaneous Generate clicks queues safely behind the
/// provider's real rate limit instead of firing everything at once and tripping it. Jobs beyond
/// the cap wait - see <see cref="AcquireAsync(LlmCallPriority,CancellationToken)"/> - never dropped.
///
/// ONE shared instance across all five paid agents (compose, both reflections, narrate, report
/// HTML) - registered as a singleton in PaidReportAgentsRegistration and passed into every
/// MafAgentFactory.Create call. A per-agent-type gate would let N concurrent runs each grab their
/// own composition-stage slot at once and still blow past the real ceiling, since the limit is on
/// the PROVIDER, not on any one stage - the whole point is a shared budget across every call this
/// process makes.
///
/// PRIORITY LANES (Sec.4.4): paid_interactive always drains ahead of paid_batch. This is NOT plain
/// FIFO - two hand-rolled waiter queues (one per lane), and a freed slot is handed DIRECTLY to the
/// head of the interactive queue if it is non-empty, the batch queue otherwise. Direct handoff
/// (never incrementing a shared counter that any new caller could race for) is what makes the
/// ordering actually hold: a counter-based release would let a batch call that happens to arrive
/// microseconds after the release grab the slot ahead of an interactive call that has been
/// waiting for seconds, which is exactly backwards from "a human is waiting, always drains first."
///
/// Free weekly digest is NOT a third lane here - see LlmCallPriority's doc comment: it never
/// reaches this gate at all (a wholly separate IClaudeClient budget).
///
/// Priority only governs QUEUE draining order, never preempts a call already granted a slot - an
/// in-flight LLM call cannot be interrupted mid-stream, so an arriving interactive call waits
/// behind whatever is already running, same as before. What changes is who goes next once a slot
/// frees.
///
/// OBSERVABILITY: <see cref="InteractiveQueueDepth"/>, <see cref="BatchQueueDepth"/>,
/// <see cref="AvailableSlots"/>/<see cref="Capacity"/> and <see cref="OldestBatchWaitTime"/> are a
/// read-only surface for LlmConcurrencyGateMetrics (Insights.Worker) to poll from OTel
/// ObservableGauge callbacks - CONFIGURATION.md's suggested `insights.queue.depth{lane}` and
/// `insights.governor.saturation_pct`, and Sec.13's "alert on keep-warm batch not draining within
/// its window" (the metrics Meter belongs in Worker, matching InsightsCostMetrics/FreeDigestMetrics
/// - this class stays a pure concurrency primitive with no OTel dependency of its own).
/// </summary>
public sealed class LlmConcurrencyGate : IDisposable
{
    private readonly object _lock = new();
    private readonly LinkedList<(TaskCompletionSource<bool> Tcs, DateTime EnqueuedAtUtc)> _interactiveWaiters = new();
    private readonly LinkedList<(TaskCompletionSource<bool> Tcs, DateTime EnqueuedAtUtc)> _batchWaiters = new();
    private int _availableSlots;
    private bool _disposed;

    public int Capacity { get; }

    public LlmConcurrencyGate(int maxConcurrentCalls)
    {
        if (maxConcurrentCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentCalls), maxConcurrentCalls, "Must be at least 1 - a gate that admits nothing would deadlock every run, not throttle them.");

        Capacity = maxConcurrentCalls;
        _availableSlots = maxConcurrentCalls;
    }

    public int InteractiveQueueDepth { get { lock (_lock) return _interactiveWaiters.Count; } }

    public int BatchQueueDepth { get { lock (_lock) return _batchWaiters.Count; } }

    public int AvailableSlots { get { lock (_lock) return _availableSlots; } }

    /// <summary>How long the longest-waiting paid_batch job has been queued, or null if none is waiting - the raw signal behind Sec.13's starvation alert.</summary>
    public TimeSpan? OldestBatchWaitTime
    {
        get
        {
            lock (_lock)
                return _batchWaiters.First is { } node ? DateTime.UtcNow - node.Value.EnqueuedAtUtc : null;
        }
    }

    /// <summary>Back-compat convenience for every call site that predates priority lanes - same as passing <see cref="LlmCallPriority.Interactive"/>.</summary>
    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        AcquireAsync(LlmCallPriority.Interactive, cancellationToken);

    /// <summary>
    /// Waits for a free slot, then returns a token that releases it on Dispose. Callers hold this
    /// for exactly the duration of one LLM call:
    /// <code>using var _ = await gate.AcquireAsync(priority, cancellationToken);</code>
    /// A slot beyond the cap does not fail - it waits here until one frees, same as the durable
    /// queue itself: "jobs beyond the limit wait ... nothing is dropped" (Sec.4.4). Which of the two
    /// waiter queues it joins is what makes drain order priority-respecting instead of FIFO.
    /// </summary>
    public Task<IDisposable> AcquireAsync(LlmCallPriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_availableSlots > 0)
            {
                // Fast path taken under the SAME lock as the enqueue-else-branch below, so there is
                // no window where a slot looks free to a fast-path check but a concurrent Release()
                // has already handed it to a queued waiter (or vice versa).
                _availableSlots--;
                return Task.FromResult<IDisposable>(new Releaser(this));
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority == LlmCallPriority.Interactive ? _interactiveWaiters : _batchWaiters;
            var node = queue.AddLast((tcs, DateTime.UtcNow));
            return AwaitSlotAsync(tcs, node, priority, cancellationToken);
        }
    }

    private async Task<IDisposable> AwaitSlotAsync(
        TaskCompletionSource<bool> tcs, LinkedListNode<(TaskCompletionSource<bool> Tcs, DateTime EnqueuedAtUtc)> node,
        LlmCallPriority priority, CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(() =>
        {
            // node.List is null once Release() has already dequeued this waiter and handed it the
            // slot - in that race the waiter just won the slot despite the cancellation request,
            // same benign outcome SemaphoreSlim.WaitAsync(ct) itself can produce, and the caller's
            // own `using` around the returned token still releases it correctly a moment later once
            // the (already-canceled) downstream call observes the token and unwinds.
            lock (_lock)
            {
                if (node.List is null)
                    return;
                (priority == LlmCallPriority.Interactive ? _interactiveWaiters : _batchWaiters).Remove(node);
            }

            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // TaskCompletionSource.TrySetCanceled surfaces as the derived TaskCanceledException;
                // normalised to the exact OperationCanceledException type every other cancellable
                // await in this codebase throws, so a caller catching OperationCanceledException
                // (or asserting its exact type, as LlmConcurrencyGateTests does) sees one consistent
                // shape regardless of whether SemaphoreSlim or this hand-rolled queue is underneath.
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return new Releaser(this);
    }

    private void Release()
    {
        TaskCompletionSource<bool>? next = null;

        lock (_lock)
        {
            if (_interactiveWaiters.First is { } interactiveNode)
            {
                next = interactiveNode.Value.Tcs;
                _interactiveWaiters.RemoveFirst();
            }
            else if (_batchWaiters.First is { } batchNode)
            {
                next = batchNode.Value.Tcs;
                _batchWaiters.RemoveFirst();
            }
            else
            {
                _availableSlots++;
            }
        }

        // Completed OUTSIDE the lock - RunContinuationsAsynchronously already keeps the waiter's
        // continuation off this thread, but there is no reason to hold the lock across it either.
        next?.TrySetResult(true);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }

    private sealed class Releaser(LlmConcurrencyGate gate) : IDisposable
    {
        private int _released;

        // Guards against a double-release, which would let one MORE call through than the
        // configured cap. A caller holding the token in a `using` while also disposing it
        // explicitly is exactly the kind of double-Dispose C# does not prevent by construction.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}

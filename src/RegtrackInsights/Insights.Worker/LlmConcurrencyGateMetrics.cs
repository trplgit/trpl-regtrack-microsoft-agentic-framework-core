using System.Diagnostics.Metrics;
using Insights.Agents;

namespace Insights.Worker;

/// <summary>
/// The concurrency gate's own state, as metrics - CONFIGURATION.md's suggested
/// `insights.queue.depth{lane}` and `insights.governor.saturation_pct`, plus
/// `insights.governor.batch_max_wait_seconds`, the raw signal behind Sec.13's "alert on ...
/// keep-warm batch not draining within its window." The ALERT RULE itself belongs in Grafana
/// (Sec.13's own architecture: app emits metrics, Grafana/Loki owns alerting) - this class only
/// emits the number, same division of responsibility as every other alert Sec.13 names (gate
/// refusals, per-tenant budget) being a Grafana rule over an app-emitted metric, not in-process
/// alerting code.
///
/// ObservableGauge, not Counter/Histogram like InsightsCostMetrics/FreeDigestMetrics - queue depth
/// and saturation are current STATE, not events, and OTel's gauge instrument is exactly "poll a
/// callback for the current value," which is what LlmConcurrencyGate's read-only properties are
/// built to be polled through. Reading them takes gate's own lock briefly per callback; that lock
/// is already held for microseconds at a time elsewhere in the gate, so a metrics collection tick
/// contends no worse than a normal Acquire/Release does.
///
/// Built on System.Diagnostics.Metrics, like every other Meter in this codebase - in the BCL, no
/// package, and what an OpenTelemetry exporter subscribes to when observability lands.
/// </summary>
public sealed class LlmConcurrencyGateMetrics : IDisposable
{
    public const string MeterName = "RegTrack.Insights.Concurrency";

    private readonly Meter _meter;

    public LlmConcurrencyGateMetrics(LlmConcurrencyGate gate)
    {
        _meter = new Meter(MeterName);

        _meter.CreateObservableGauge(
            "insights.queue.depth",
            () => new[]
            {
                new Measurement<int>(gate.InteractiveQueueDepth, new KeyValuePair<string, object?>("lane", "paid_interactive")),
                new Measurement<int>(gate.BatchQueueDepth, new KeyValuePair<string, object?>("lane", "paid_batch")),
            },
            "job",
            "LLM calls waiting for a concurrency slot, by priority lane.");

        _meter.CreateObservableGauge(
            "insights.governor.saturation_pct",
            () => gate.Capacity == 0 ? 0.0 : (gate.Capacity - gate.AvailableSlots) / (double)gate.Capacity * 100.0,
            "%",
            "Share of the concurrency cap currently in use.");

        _meter.CreateObservableGauge(
            "insights.governor.batch_max_wait_seconds",
            () => gate.OldestBatchWaitTime?.TotalSeconds ?? 0.0,
            "s",
            "How long the longest-waiting paid_batch job has been queued. Sec.13: alert on this staying elevated - it means keep-warm is not draining within its window.");
    }

    public void Dispose() => _meter.Dispose();
}

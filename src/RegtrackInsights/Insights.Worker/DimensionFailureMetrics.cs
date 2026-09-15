using System.Diagnostics.Metrics;
using Insights.Data;

namespace Insights.Worker;

/// <summary>
/// CONFIGURATION.md's insights.dimension.block_failures_total{dimension} (Sec.13) - one dimension
/// block failing (design doc Sec.11.4, "Partial generation") is a block-level bug alert, not a
/// whole-run refusal, so it needs its own counter distinct from insights.gate.refusals_total.
///
/// Built on System.Diagnostics.Metrics, like every other Meter in this codebase - see
/// InsightsCostMetrics/FreeDigestMetrics/LlmConcurrencyGateMetrics' doc comments for why (BCL, no
/// package, what an OpenTelemetry exporter subscribes to when observability lands).
///
/// DIMENSION NAME IS A CLOSED SET - the nine values IDimensionRepository's methods use ("Location",
/// "Risk", ...), never anything else. Same cardinality discipline FreeDigestMetrics documents for
/// its own tag.
/// </summary>
public sealed class DimensionFailureMetrics : IDimensionFailureRecorder, IDisposable
{
    public const string MeterName = "RegTrack.Insights.Dimensions";

    private readonly Meter _meter;
    private readonly Counter<long> _blockFailures;

    public DimensionFailureMetrics()
    {
        _meter = new Meter(MeterName);
        _blockFailures = _meter.CreateCounter<long>(
            "insights.dimension.block_failures_total", "failure", "Dimension blocks degraded to a placeholder, by dimension (design doc Sec.11.4).");
    }

    public void RecordBlockFailure(string dimension) =>
        _blockFailures.Add(1, new KeyValuePair<string, object?>("dimension", dimension));

    public void Dispose() => _meter.Dispose();
}

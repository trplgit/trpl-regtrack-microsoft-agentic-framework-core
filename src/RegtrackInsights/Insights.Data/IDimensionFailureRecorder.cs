namespace Insights.Data;

/// <summary>
/// Design doc Sec.11.4/Sec.11.5: "Alert at block level so the specific failing procedure is
/// identified" / "Partial -> block-level bug". FetchDimensionsActivity calls this once per
/// dimension whose fetch it caught and degraded to a placeholder - never for the four other
/// dimension exceptions that still fail the whole run (see FetchDimensionsActivity's doc comment
/// for which is which).
///
/// Same shape as ILlmUsageRecorder (Insights.Agents) - an interface here so Insights.Data does not
/// need to know OpenTelemetry exists, and a concrete Meter-backed implementation lives in
/// Insights.Worker (DimensionFailureMetrics), matching InsightsCostMetrics/FreeDigestMetrics.
/// </summary>
public interface IDimensionFailureRecorder
{
    void RecordBlockFailure(string dimension);

    /// <summary>Discards everything. The default when nobody wires metrics - a manual run, a test.</summary>
    public static IDimensionFailureRecorder Null { get; } = new NullRecorder();

    private sealed class NullRecorder : IDimensionFailureRecorder
    {
        public void RecordBlockFailure(string dimension) { }
    }
}

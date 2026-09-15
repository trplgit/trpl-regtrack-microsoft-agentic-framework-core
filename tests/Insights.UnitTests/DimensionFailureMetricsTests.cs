using System.Diagnostics.Metrics;
using Insights.Worker;

namespace Insights.UnitTests;

/// <summary>
/// CONFIGURATION.md's insights.dimension.block_failures_total{dimension} (design doc Sec.11.4/
/// Sec.11.5 - "alert at block level"). Real MeterListener, same pattern as
/// LlmConcurrencyGateMetricsTests - pins the instrument NAME and the `dimension` TAG, not just
/// that some counter somewhere incremented.
/// </summary>
public sealed class DimensionFailureMetricsTests
{
    [Fact]
    public void RecordBlockFailure_EmitsACounterTaggedWithTheDimensionName()
    {
        using var metrics = new DimensionFailureMetrics();

        var measurements = new List<(string InstrumentName, long Value, string? Dimension)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DimensionFailureMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var dimension = tags.ToArray().FirstOrDefault(t => t.Key == "dimension").Value?.ToString();
            measurements.Add((instrument.Name, value, dimension));
        });
        listener.Start();

        metrics.RecordBlockFailure("Risk");
        metrics.RecordBlockFailure("Risk");
        metrics.RecordBlockFailure("Nature");

        Assert.Equal(2, measurements.Count(m => m.InstrumentName == "insights.dimension.block_failures_total" && m.Dimension == "Risk"));
        Assert.Single(measurements, m => m.InstrumentName == "insights.dimension.block_failures_total" && m.Dimension == "Nature");
    }
}

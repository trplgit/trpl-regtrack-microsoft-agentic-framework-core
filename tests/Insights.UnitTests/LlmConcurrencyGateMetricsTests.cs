using System.Diagnostics.Metrics;
using Insights.Agents;
using Insights.Domain;
using Insights.Worker;

namespace Insights.UnitTests;

/// <summary>
/// CONFIGURATION.md's insights.queue.depth{lane}, insights.governor.saturation_pct and
/// insights.governor.batch_max_wait_seconds (Sec.13). Uses a real MeterListener rather than
/// asserting against LlmConcurrencyGate's properties directly (LlmConcurrencyGateTests already
/// does that) - this pins that the ObservableGauge WIRING itself is correct: right instrument
/// name, right tag key/value on the queue-depth gauge, right callback reading the right property.
/// </summary>
public sealed class LlmConcurrencyGateMetricsTests
{
    [Fact]
    public async Task PublishesQueueDepthSaturationAndBatchWaitTime_ForTheCurrentGateState()
    {
        var gate = new LlmConcurrencyGate(1);
        using var metrics = new LlmConcurrencyGateMetrics(gate);

        var held = await gate.AcquireAsync(LlmCallPriority.Interactive, CancellationToken.None); // saturates the only slot
        var queuedBatch = gate.AcquireAsync(LlmCallPriority.Batch, CancellationToken.None); // queues behind it

        var intMeasurements = new List<(string InstrumentName, int Value, string? Lane)>();
        var doubleMeasurements = new List<(string InstrumentName, double Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LlmConcurrencyGateMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var lane = tags.ToArray().FirstOrDefault(t => t.Key == "lane").Value?.ToString();
            intMeasurements.Add((instrument.Name, value, lane));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            doubleMeasurements.Add((instrument.Name, value)));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Contains(intMeasurements, m => m is { InstrumentName: "insights.queue.depth", Lane: "paid_interactive", Value: 0 });
        Assert.Contains(intMeasurements, m => m is { InstrumentName: "insights.queue.depth", Lane: "paid_batch", Value: 1 });

        var saturation = Assert.Single(doubleMeasurements, m => m.InstrumentName == "insights.governor.saturation_pct");
        Assert.Equal(100.0, saturation.Value); // the gate's one and only slot is held

        var batchWait = Assert.Single(doubleMeasurements, m => m.InstrumentName == "insights.governor.batch_max_wait_seconds");
        Assert.True(batchWait.Value >= 0.0);

        held.Dispose();
        (await queuedBatch).Dispose();
    }

    [Fact]
    public void SaturationPct_IsZero_WhenTheGateIsIdle()
    {
        var gate = new LlmConcurrencyGate(3);
        using var metrics = new LlmConcurrencyGateMetrics(gate);

        var doubleMeasurements = new List<(string InstrumentName, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LlmConcurrencyGateMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            doubleMeasurements.Add((instrument.Name, value)));
        listener.Start();

        listener.RecordObservableInstruments();

        var saturation = Assert.Single(doubleMeasurements, m => m.InstrumentName == "insights.governor.saturation_pct");
        Assert.Equal(0.0, saturation.Value);

        var batchWait = Assert.Single(doubleMeasurements, m => m.InstrumentName == "insights.governor.batch_max_wait_seconds");
        Assert.Equal(0.0, batchWait.Value); // no batch waiter - nothing to report, must not be null/NaN
    }
}

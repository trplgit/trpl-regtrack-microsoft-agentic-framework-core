using System.Diagnostics.Metrics;
using Insights.Agents;

namespace Insights.Worker;

/// <summary>
/// LLM spend, as metrics (CLAUDE.md build order item 17 - a launch requirement).
///
/// Built on System.Diagnostics.Metrics like FreeDigestMetrics: in the BCL, no package, and it is
/// what an OpenTelemetry exporter subscribes to when observability lands. Nothing here presumes
/// which exporter wins - O-4 (the LangFuse .NET trace shape) is still an open spike, and this
/// does not wait on it.
///
/// [TRAP] NO TENANT TAG, EVER. FreeDigestMetrics states the rule and it applies with more force
/// here: ~2,290 tenants as a metric dimension is a cardinality explosion, and a tenant name in a
/// metrics backend is customer data in a place nobody audits. Tags are stage and model, both
/// closed sets. Per-tenant spend belongs in a log line or a SQL row where it can be joined,
/// retained and purged like the rest of the tenant's data.
/// </summary>
public sealed class InsightsCostMetrics : ILlmUsageRecorder, IDisposable
{
    public const string MeterName = "RegTrack.Insights.Cost";

    private readonly Meter _meter;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _calls;
    private readonly Histogram<long> _callTokens;

    public InsightsCostMetrics()
    {
        _meter = new Meter(MeterName);

        /*  Direction is a TAG rather than two separate counters because input and output tokens
            are priced differently - keeping them on one metric means a cost query is a single
            weighted sum, not a join across two series that can drift out of step.               */
        _tokens = _meter.CreateCounter<long>("insights.llm.tokens_total", "token", "LLM tokens billed, by stage, model and direction.");
        _calls = _meter.CreateCounter<long>("insights.llm.calls_total", "call", "LLM calls made, by stage and model.");
        _callTokens = _meter.CreateHistogram<long>("insights.llm.call_tokens", "token", "Tokens per individual LLM call.");
    }

    public void Record(LlmUsage usage)
    {
        /*  [TRAP] Never throws. This runs after a call the tenant has already been billed for;
            letting a metrics failure propagate would discard paid-for output and turn an
            observability problem into a spend problem.                                          */
        try
        {
            var stage = new KeyValuePair<string, object?>("stage", usage.Stage);
            var model = new KeyValuePair<string, object?>("model", usage.Model);

            _tokens.Add(usage.InputTokens, stage, model, new KeyValuePair<string, object?>("direction", "input"));
            _tokens.Add(usage.OutputTokens, stage, model, new KeyValuePair<string, object?>("direction", "output"));
            _calls.Add(1, stage, model);
            _callTokens.Record(usage.TotalTokens, stage, model);
        }
        catch
        {
            // Deliberately swallowed - see above.
        }
    }

    public void Dispose() => _meter.Dispose();
}

using System.Diagnostics.Metrics;

namespace Insights.Worker;

/// <summary>
/// The free digest's counters, per the Phase 1c checklist:
/// insights.digest.sent_total and insights.digest.skipped_total{reason}.
///
/// Built on System.Diagnostics.Metrics, which is in the BCL - no package, and it is what an
/// OpenTelemetry exporter subscribes to when observability lands (CLAUDE.md 7: OTel -> LangFuse
/// for LLM traces, Grafana/Loki for app metrics). Nothing here presumes which exporter wins.
///
/// SKIP REASONS ARE LOW-CARDINALITY BY CONSTRUCTION. A reason tag must never carry a tenant name,
/// an email address or a validator message - those explode cardinality and put customer data in a
/// metrics backend. The reasons are a closed set; the detail belongs in the log and in
/// InsightsFreeDigestLog.Detail.
/// </summary>
public sealed class FreeDigestMetrics : IDisposable
{
    public const string MeterName = "RegTrack.Insights.FreeDigest";

    private readonly Meter _meter;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _skipped;
    private readonly Histogram<double> _tenantDuration;

    public FreeDigestMetrics()
    {
        _meter = new Meter(MeterName);
        _sent = _meter.CreateCounter<long>("insights.digest.sent_total", "email", "Free digests delivered.");
        _skipped = _meter.CreateCounter<long>("insights.digest.skipped_total", "recipient", "Free digests not sent, by reason.");
        _tenantDuration = _meter.CreateHistogram<double>("insights.digest.tenant_duration_ms", "ms", "Wall time per tenant.");
    }

    /// <summary>Source is llm or fallback - the split matters, because a persistent fallback rate means LLM spend with no output.</summary>
    public void RecordSent(string source, string provider) =>
        _sent.Add(1, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("provider", provider));

    public void RecordSkipped(FreeDigestSkipReason reason) =>
        _skipped.Add(1, new KeyValuePair<string, object?>("reason", reason.ToTag()));

    public void RecordTenantDuration(double milliseconds) => _tenantDuration.Record(milliseconds);

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// The closed set of reasons a recipient did not receive a digest. Closed ON PURPOSE - see the
/// cardinality note on <see cref="FreeDigestMetrics"/>.
/// </summary>
public enum FreeDigestSkipReason
{
    /// <summary>Tenant not entitled, or disabled.</summary>
    NotEntitled,

    /// <summary>Paid tier active - the free digest self-skips (spec 5.3).</summary>
    Superseded,

    /// <summary>Tenant entitled but nobody is mapped to product 18.</summary>
    NoRecipients,

    /// <summary>Recipient has no authorised (branch, category) pairs - the digest would be all zeros.</summary>
    NoScope,

    /// <summary>Already sent to this recipient for this week.</summary>
    AlreadySent,

    /// <summary>The run threw - dictionary gap, connection failure, provider error.</summary>
    Failed,
}

public static class FreeDigestSkipReasonExtensions
{
    /// <summary>Stable snake_case tags - a metrics series name must not change when an enum is renamed.</summary>
    public static string ToTag(this FreeDigestSkipReason reason) => reason switch
    {
        FreeDigestSkipReason.NotEntitled => "not_entitled",
        FreeDigestSkipReason.Superseded => "superseded",
        FreeDigestSkipReason.NoRecipients => "no_recipients",
        FreeDigestSkipReason.NoScope => "no_scope",
        FreeDigestSkipReason.AlreadySent => "already_sent",
        FreeDigestSkipReason.Failed => "failed",
        _ => "unknown",
    };
}

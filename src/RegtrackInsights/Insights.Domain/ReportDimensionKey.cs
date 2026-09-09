namespace Insights.Domain;

/// <summary>
/// [TEMP WORKAROUND 2026-09-09, no schema change] The real fix for the dimension_selection
/// cooldown bug is a new GeneratedReport.RequestedDimensions column (see
/// sql/28_generated_report_dimension_key.sql, not yet deployed - SQL changes go through Vinay).
/// Until that lands, this is the zero-schema-change interim fix: fold the requested dimension(s)
/// into the PERIOD string itself before it is used for the cooldown check, the run id hash, and
/// persistence - Period is the one component of the (scope, reportType, period) key that is
/// never parsed or validated by anything downstream (unlike ScopeDescriptor, which
/// InsightsScopeRequest.Parse and ReportContentService.CoversReportScopeAsync both parse
/// strictly, or ReportType, which the orchestrator branches control-flow on) - see the reference
/// architecture review's rejection of overloading ReportType for the same reasoning.
///
/// [KNOWN TRADEOFF] The value stored in GeneratedReport.Period for a dimension_selection run is
/// no longer exactly the caller-supplied period - it carries a suffix. Nothing today reads that
/// column expecting the literal caller value back (no report-history endpoint exists yet -
/// API_CONTRACTS.md Sec.2 - and PaidKeepWarmScheduler only round-trips it verbatim), so this is
/// safe today. If/when a report-history UI displays Period to a user, this workaround must be
/// replaced by the real column before that ships - a displayed period reading
/// "FY2025-26::dim=Nature" is a real regression, not a cosmetic one.
/// </summary>
public static class ReportDimensionKey
{
    private const string Separator = "::dim=";

    /// <summary>
    /// Returns <paramref name="period"/> unchanged when <paramref name="requestedDimensions"/> is
    /// null/empty (every report type except dimension_selection, and dimension_selection's own
    /// "no dimensions" refusal path never reaches here). Otherwise appends a canonical,
    /// case/whitespace-normalised join of the requested dimension names, so two different
    /// selections against the identical caller-supplied period produce two different effective
    /// periods - which is what makes InsightsRunId.For, ICooldownRepository.CheckAsync, and
    /// GeneratedReport.Period all naturally distinguish them, with zero changes to any of those.
    /// </summary>
    public static string ForCooldownAndRunId(string period, IReadOnlyList<string>? requestedDimensions)
    {
        if (requestedDimensions is not { Count: > 0 })
            return period;

        var names = requestedDimensions
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .Select(d => d.ToLowerInvariant());

        var joined = string.Join(',', names);
        return joined.Length == 0 ? period : $"{period}{Separator}{joined}";
    }
}

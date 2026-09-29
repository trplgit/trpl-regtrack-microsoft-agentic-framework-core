using System.Globalization;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-27] The report's period, ready to print in the report header ("Last 30 days ·
/// 29 Aug 2026 – 27 Sep 2026"), carried from RenderHtmlActivity down to the render agent's payload
/// (<c>report_period</c>) without changing <c>IReportHtmlAgent.RenderAsync</c>'s shape - same
/// AsyncLocal-per-activity-call pattern as <see cref="LangfuseSessionContext"/>. Built in code
/// from the real window so the model never has to work out (or invent) a date range. Null when
/// the report has no window (BacklogAging - counted as of today).
/// </summary>
public static class ReportPeriodContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentLabel => Current.Value;

    /// <code>using var _ = ReportPeriodContext.Push(ReportPeriodContext.Describe(period, ws, we));</code>
    public static IDisposable Push(string? label)
    {
        var previous = Current.Value;
        Current.Value = label;
        return new Popper(previous);
    }

    /// <summary>
    /// "Last 30 days · 29 Aug 2026 – 27 Sep 2026", "Q1 FY2026-27 · 1 Apr 2026 – 30 Jun 2026".
    /// <paramref name="windowEnd"/> is exclusive (the day after the last day), as the procedures use it.
    /// </summary>
    public static string? Describe(string? period, DateTime? windowStart, DateTime? windowEnd)
    {
        if (windowStart is not { } ws || windowEnd is not { } we || we <= ws)
            return null;

        var lastDay = we.Date.AddDays(-1);
        var range = $"{ws.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {lastDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

        var p = period?.Trim().ToLowerInvariant();
        string? name = p switch
        {
            "last_30_days" => "Last 30 days",
            "last_60_days" => "Last 60 days",
            "last_90_days" => "Last 90 days",
            "q1" or "q2" or "q3" or "q4" => $"{p.ToUpperInvariant()} FY{FyStartYear(ws)}-{(FyStartYear(ws) + 1) % 100:00}",
            _ => null,
        };
        return name is null ? range : $"{name} · {range}";
    }

    /// <summary>
    /// [ADDED 2026-09-28] Same as <see cref="Describe"/>, but null for a dimension that ignores the
    /// window (BacklogAging - counted as of the run date; Licence follows the period since 2026-09-29): a Q2 request still carries a Q2
    /// window, and printing it over as-of-today data would state a period the numbers do not cover.
    /// A null dimension (fixed_holistic, several dimensions) keeps the window.
    /// </summary>
    public static string? DescribeFor(string? dimensionName, string? period, DateTime? windowStart, DateTime? windowEnd) =>
        dimensionName is not null && !Insights.Domain.ReportPeriodRequestParser.WindowRequiredDimensions.Contains(dimensionName)
            ? null
            : Describe(period, windowStart, windowEnd);

    /// <summary>Indian financial year: 1 April - 31 March.</summary>
    private static int FyStartYear(DateTime d) => d.Month >= 4 ? d.Year : d.Year - 1;

    private sealed class Popper(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

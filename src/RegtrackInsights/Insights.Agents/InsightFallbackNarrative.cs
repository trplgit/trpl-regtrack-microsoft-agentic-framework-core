using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Deterministic headline + explanation for when the LLM is skipped, over budget, truncated, or
/// its output is rejected by <see cref="FreeDigestValidator"/> - the insight JSON, like the free
/// digest email, must never fail to be produced (spec 10.5's "the email never fails to go out",
/// same principle applied here). One clause per <see cref="InsightFocus.Metric"/>, framed as a
/// concrete before -> after wherever <see cref="InsightFocus.Remainder"/> is available - see
/// prompts/07_insight_json_narrative.md, whose "better" framing this mirrors so a reader cannot
/// tell whether the LLM ran this week. Every number traces straight to
/// <see cref="InsightFocus.Value"/>/<see cref="InsightFocus.Denominator"/>/
/// <see cref="InsightFocus.Remainder"/> - nothing invented.
/// </summary>
public static class InsightFallbackNarrative
{
    public static (string Headline, string Explanation) Build(InsightFocus focus) => focus.Metric switch
    {
        nameof(FreeDigestAggregates.ImprisonmentDueNext7) =>
            ($"{focus.Value} of this week's {focus.Denominator} obligations carry personal liability for the responsible officer",
             $"Clearing those {focus.Value} first leaves {focus.Remainder} obligations this week with no personal-liability exposure attached."),

        nameof(FreeDigestAggregates.CriticalDueNext7) =>
            ($"{focus.Value} of this week's {focus.Denominator} obligations are rated critical",
             $"Closing those {focus.Value} first leaves {focus.Remainder} standard-priority obligations for the rest of the week."),

        nameof(FreeDigestAggregates.ImprisonmentDueNext30) =>
            ($"{focus.Value} of the next 30 days' {focus.Denominator} obligations carry personal liability for the responsible officer",
             $"That is {focus.Value} obligations where a missed deadline has consequences beyond a penalty, against {focus.Remainder} that do not."),

        nameof(FreeDigestAggregates.LicencesLapsingNext30) =>
            ($"{focus.Value} licence(s) are due to lapse in the next 30 days",
             $"A lapsed licence halts the activity it covers outright - renewing these {focus.Value} in time avoids a stoppage, not just a missed deadline."),

        nameof(FreeDigestAggregates.DueNext7) =>
            ($"{focus.Value} of the estate's {focus.Denominator} active obligations fall due this week",
             $"Clearing this week's {focus.Value} leaves {focus.Remainder} obligations with no deadline pressure until next week."),

        nameof(FreeDigestAggregates.DueNext30) =>
            ($"{focus.Value} of the estate's {focus.Denominator} active obligations fall due in the next 30 days",
             $"None of these fall due in the next seven days, so there is time to plan - {focus.Remainder} obligations carry no deadline in this window at all."),

        _ =>
            ($"{focus.Value} completions were recorded last week across the estate",
             "Nothing is due in the next seven days, so the estate enters this week clear."),
    };
}

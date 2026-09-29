using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// One fixed sentence per named finding, shared by BOTH deterministic fallbacks - the monthly email
/// (<see cref="FreeMonthlyFallbackBody"/>) and the insight card (<see cref="InsightCardFallback"/>).
/// ADR 2026-09-27-free-digest-reflection-design Sec.4.13.
///
/// <para><b>Why a template may name something here.</b> The old email fallback named nothing, on
/// the reasoning that "a template cannot judge whether the sentence around a name is true". That
/// holds for a generic template. It does not hold for a sentence fixed per (detector, metric) and
/// filled only with that candidate's own values: the proc decided what the numbers mean, and the
/// sentence says exactly that and nothing else. True by construction.</para>
///
/// <para><b>Keyed on (Detector, Metric), never the detector alone</b> (ADR C13). The candidate's
/// <see cref="MonthlyCandidate.Metric"/> is what fixes what its ItemCount and BaseCount count -
/// the pairs below are read from sql/36-41's own INSERTs. A detector arriving with an unexpected
/// metric, or a detector not listed here, returns null: that finding is not named. A raw detector
/// name never reaches a reader.</para>
///
/// <para>Output is PLACEHOLDER form ({{NAME_n}}, {{NAME_n_AT}}, {{DATE_n}}, {{PREV_MONTH}}), so the
/// text passes through the same validator and <see cref="FreeMonthlyPlaceholderBinder"/> as a model
/// draft. Every figure is a proc value the closed number set already contains.</para>
/// </summary>
public static class DetectorSentences
{
    /// <summary>Every (detector, metric) pair a sentence exists for - the test enumerates the known detector set against it.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownMetrics = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["last_month_slippage"] = "last_month_still_open_pct",              // sql/37
        ["liability_share"] = "overdue_with_liability_pct",                // sql/37
        ["chronic_backlog"] = "overdue_over_90_days_pct",                  // sql/37
        ["overdue_concentration"] = "share_of_all_overdue_pct",            // sql/37
        ["liability_overdue_location"] = "liability_overdue_items",        // sql/36
        ["category_overdue_skew"] = "obligation_overdue_rate_pct",         // sql/36
        ["deactivated_owner"] = "open_items_held_by_inactive_user",        // sql/38
        ["self_review"] = "open_items_self_reviewed",                      // sql/38
        ["single_point_of_failure"] = "open_items_with_one_performer",     // sql/39
        ["ghost_location"] = "no_obligations_configured",                  // sql/39
        ["multi_location_pattern"] = "locations_overdue_on_this_law",      // sql/40
        ["licence_expiring_unrenewed"] = "expires_on",                     // sql/36, sql/41
        ["licence_lapsed_recent_unrenewed"] = "expired_on",                // sql/41
        ["expired_unrenewed_location"] = "licences_expired_unrenewed_pct", // sql/41
        ["licence_type_lapse_rate"] = "licences_expired_unrenewed_pct",    // sql/41
    };

    /// <summary>A rate on a base this small is noise - the same floor FreeMonthlyDigestPrompt uses before sending one.</summary>
    private const int MinBaseForPercentage = 20;

    /// <summary>
    /// The finding's sentence, or null when it cannot be stated truthfully: unknown detector, a
    /// metric other than the one its sentence assumes, a missing figure, or no nameable label.
    /// </summary>
    /// <param name="emphasise">Bold the finding's lead figure - used when the finding IS the headline.</param>
    public static string? For(MonthlyNamedFinding finding, FreeMonthlyDigestPrompt prompt, bool emphasise = false)
    {
        var c = finding.Candidate;
        if (finding.NamePlaceholder is not { } name
            || !KnownMetrics.TryGetValue(c.Detector, out var expectedMetric)
            || !string.Equals(c.Metric, expectedMetric, StringComparison.Ordinal))
            return null;

        string N(int value) => emphasise ? $"**{InsightCardRules.Count(value)}**" : InsightCardRules.Count(value);
        var at = finding.AtPlaceholder is { } a ? $" at {a}" : string.Empty;
        var comparison = Comparison(c, prompt);

        return (c.Detector, c.ItemCount, c.BaseCount) switch
        {
            ("last_month_slippage", { } item, { } whole) when item <= whole =>
                $"{name}: {N(item)} of the {InsightCardRules.Count(whole)} obligations that fell due there in {{{{PREV_MONTH}}}} are still open{comparison}.",
            ("liability_share", { } item, { } whole) when item <= whole =>
                $"{name}: {N(item)} of its {InsightCardRules.Count(whole)} overdue obligations carry personal criminal liability for the responsible officer{comparison}.",
            ("chronic_backlog", { } item, { } whole) when item <= whole =>
                $"{name}: {N(item)} of its {InsightCardRules.Count(whole)} overdue obligations have been overdue for more than 90 days{comparison}.",
            ("overdue_concentration", { } item, { } whole) when item <= whole =>
                $"{name} holds {N(item)} of the {InsightCardRules.Count(whole)} overdue obligations in the standing backlog.",
            ("liability_overdue_location", { } item, _) =>
                $"{name} has {N(item)} overdue obligations that carry personal criminal liability for the responsible officer.",
            ("category_overdue_skew", _, _) when comparison.Length > 0 =>
                $"{name}: obligations of this kind are overdue far more often than the rest{comparison}.",
            ("deactivated_owner", { } item, _) =>
                $"{name} holds {N(item)} open obligations but is no longer an active user of RegTrack.",
            ("self_review", { } item, _) =>
                $"{name}: {N(item)} open obligations are performed and approved by the same person.",
            ("single_point_of_failure", { } item, _) =>
                $"{name}: all {N(item)} open obligations rest on one person, and nobody else is assigned to any of them.",
            ("ghost_location", _, _) =>
                $"{name} has no obligations configured at all, so it cannot be assessed.",
            ("multi_location_pattern", { } item, { } whole) when item <= whole =>
                $"{name}: obligations under this Act are overdue at {N(item)} of the {InsightCardRules.Count(whole)} locations it applies to.",
            ("licence_expiring_unrenewed", _, _) when finding.DatePlaceholder is { } date =>
                $"{name}{at} expires on {date} with no renewal filed.",
            ("licence_lapsed_recent_unrenewed", _, _) when finding.DatePlaceholder is { } date =>
                $"{name}{at} expired on {date} and still has no renewal in progress.",
            ("expired_unrenewed_location", { } item, { } whole) when item <= whole =>
                $"{name}: {N(item)} of its {InsightCardRules.Count(whole)} licences are currently expired with no renewal in progress{comparison}.",
            ("licence_type_lapse_rate", { } item, { } whole) when item <= whole =>
                $"{name}: {N(item)} of the {InsightCardRules.Count(whole)} licences of this type are currently expired with no renewal in progress{comparison}.",
            _ => null,
        };
    }

    /// <summary>
    /// ", against X% across your organisation" style comparison - only when both rates are in the
    /// closed percentage set and the base is large enough for a rate to mean anything.
    /// </summary>
    private static string Comparison(MonthlyCandidate c, FreeMonthlyDigestPrompt prompt)
    {
        if (c.MetricPct is not { } mine || c.TenantPct is not { } all
            || !prompt.AllowedPercentages.Contains(mine) || !prompt.AllowedPercentages.Contains(all)
            || c.BaseCount is < MinBaseForPercentage)
            return string.Empty;

        return $" ({mine}% here, against {all}% across your organisation)";
    }
}

using System.Globalization;

namespace Insights.Domain;

/// <summary>
/// The deterministic rules behind an <see cref="InsightCard"/>: type derivation, impact and
/// severity labels, the closed unit vocabulary, identity, and the recheck calendar (spec
/// 2026-09-23-weekly-insight-json-contract-design.md sections 5.2, 5.3, 6.1, 6.2, 6.5, 6.8).
/// Pure functions of proc values and the calendar - no clock, no I/O - so a Durable Task replay
/// produces an identical card.
/// </summary>
public static class InsightCardRules
{
    public const string Tier = "free";
    public const int NamedFindingsCap = 2;


    public static string SubjectOf(MonthlyDigestSlot slot) => slot.ToString().ToLowerInvariant();

    public static string InsightId(int customerId, long userId, DateOnly weekOf) =>
        $"ins_{customerId}_{userId}_{weekOf:yyyyMMdd}";


    /// <summary>
    /// The insight type is DERIVED from the winning fact, never promised in advance (spec 6.2):
    /// a closed set of three, and a context fact can never lead.
    /// </summary>
    public static string TypeForFact(string windowScope, string section, string factKey) => windowScope switch
    {
        "prev" => "descriptive",
        "stock" => "diagnostic",
        "curr" when section == "rest_of_month" || factKey.StartsWith("rm_", StringComparison.Ordinal) => "predictive",
        "curr" when section == "licences" && factKey.Contains("expiring", StringComparison.Ordinal) => "predictive",
        "curr" => "diagnostic",
        _ => throw new InvalidOperationException($"A fact with WindowScope '{windowScope}' ({factKey}) cannot lead an insight - refusing to build the card."),
    };

    /// <summary>What a detector measures, mapped onto the same three types.</summary>
    public static string TypeForDetector(string detector) => detector switch
    {
        "last_month_slippage" or "licence_lapsed_recent_unrenewed" => "descriptive",
        "licence_expiring_unrenewed" => "predictive",
        _ => "diagnostic",
    };


    /// <summary>The kind of problem a detector's finding is, in the same vocabulary the facts use.</summary>
    public static string ImpactClassForDetector(string detector) => detector switch
    {
        "liability_share" or "liability_overdue_location" => "personal_liability",
        var d when d.StartsWith("licence", StringComparison.Ordinal) || d.StartsWith("expired", StringComparison.Ordinal) => "licence_continuity",
        _ => "operational_continuity",
    };


    public static string Severity(int severityTier) => severityTier switch
    {
        <= 1 => "high",
        2 or 3 => "medium",
        _ => "low",
    };


    /// <summary>
    /// Closed unit vocabulary drawn from the fact's own key (spec 6.8): never "tasks", "stores"
    /// or "accounts". <c>Store</c> and <c>Branch</c> are one class in this schema (CLAUDE.md 5).
    /// </summary>
    public static string UnitForFact(string factKey)
    {
        if (factKey.StartsWith("lic_types", StringComparison.Ordinal))
            return "licence types";
        if (factKey.StartsWith("lic_", StringComparison.Ordinal))
            return "licences";
        if (factKey.StartsWith("loc_", StringComparison.Ordinal) || factKey.StartsWith("locations", StringComparison.Ordinal))
            return "locations";
        if (factKey.StartsWith("law_", StringComparison.Ordinal))
            return "Acts";
        if (factKey.StartsWith("u_people", StringComparison.Ordinal) || factKey.StartsWith("u_inactive_people", StringComparison.Ordinal))
            return "people";
        return "obligations";
    }

    /// <summary>A pattern fact (sql/36-41 <c>pat_*</c>/<c>pattern_*</c>) counts MEMBERS, not obligations.</summary>
    public static bool IsPatternFact(string factKey) =>
        factKey.StartsWith("pat_", StringComparison.Ordinal) || factKey.StartsWith("pattern_", StringComparison.Ordinal);

    /// <summary>
    /// What a pattern fact counts. Which members it counts follows the DETECTOR, not the fact
    /// key's wording - <c>pat_multi_location_pattern</c> counts the ACTS that are overdue at many
    /// locations, not the locations - so this is a closed map rather than a keyword test, which
    /// gets exactly that one backwards. The four shared detectors (sql/37) run for whichever
    /// dimension the subject is about, so they take the subject's own member unit.
    ///
    /// <para>[FOUND on tenant 1082, 2026-09-24] Without this, <c>pat_liability_share</c> on the
    /// People subject produced "6 obligations with personal liability" for a figure that counts
    /// six people - the false equivalence between members and obligations that CLAUDE.md Sec.4a
    /// exists to prevent, shipped in a display string.</para>
    /// </summary>
    public static string UnitForPattern(string factKey, MonthlyDigestSlot slot) => factKey switch
    {
        "pattern_liability_locations" or "pat_single_point_of_failure"
            or "pat_ghost_location" or "pat_expired_unrenewed_location" => "locations",
        "pattern_categories" => "categories",
        "pat_deactivated_owner" or "pat_self_review" => "people",
        "pat_multi_location_pattern" => "Acts",
        "pat_licence_type_lapse_rate" => "licence types",
        _ => MemberUnit(slot),
    };

    /// <summary>The thing a subject's rows ARE - what a shared detector counts when it runs for that subject.</summary>
    public static string MemberUnit(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Users => "people",
        MonthlyDigestSlot.Location or MonthlyDigestSlot.Overview => "locations",
        MonthlyDigestSlot.Act => "Acts",
        MonthlyDigestSlot.Licence => "licences",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };

    /// <summary>The fact's unit, with the subject in hand so a pattern fact counts members rather than obligations.</summary>
    public static string UnitForFact(string factKey, MonthlyDigestSlot slot) =>
        IsPatternFact(factKey) ? UnitForPattern(factKey, slot) : UnitForFact(factKey);

    public static string UnitForDetector(string detector) => detector switch
    {
        var d when d.StartsWith("licence", StringComparison.Ordinal) || d == "expired_unrenewed_location" || d == "licence_type_lapse_rate" => "licences",
        "multi_location_pattern" => "locations",
        _ => "obligations",
    };

    /// <summary>The short qualifier after a count in a compact line: "587 with personal liability", "144 unassigned".</summary>
    public static string ShortNounForFact(string factKey)
    {
        if (factKey.Contains("no_owner", StringComparison.Ordinal))
            return "unassigned";
        if (factKey.Contains("no_reviewer", StringComparison.Ordinal))
            return "with no reviewer";
        if (factKey.Contains("liability", StringComparison.Ordinal))
            return "with personal liability";
        if (factKey.Contains("never_touched", StringComparison.Ordinal))
            return "never started";
        if (factKey.Contains("over_90", StringComparison.Ordinal))
            return "overdue over 90 days";
        if (factKey.Contains("inactive", StringComparison.Ordinal))
            return "held by deactivated users";
        if (factKey.Contains("self_review", StringComparison.Ordinal))
            return "self-reviewed";
        if (factKey.Contains("single_performer", StringComparison.Ordinal))
            return "resting on one person";
        if (factKey.Contains("expiring", StringComparison.Ordinal))
            return "expiring";
        if (factKey.Contains("expired", StringComparison.Ordinal) || factKey.Contains("lapsed", StringComparison.Ordinal))
            return "expired";
        if (factKey.Contains("critical", StringComparison.Ordinal))
            return "critical";
        if (factKey.Contains("still_open", StringComparison.Ordinal) || factKey.Contains("_open", StringComparison.Ordinal))
            return "open";
        if (factKey.Contains("overdue", StringComparison.Ordinal) || factKey.StartsWith("od_", StringComparison.Ordinal) || factKey.StartsWith("t_od_", StringComparison.Ordinal))
            return "overdue";
        if (factKey.Contains("due", StringComparison.Ordinal))
            return "due";
        return string.Empty;
    }

    public static string ShortNounForDetector(string detector) => detector switch
    {
        "overdue_concentration" or "chronic_backlog" => "overdue",
        "liability_share" or "liability_overdue_location" => "with personal liability",
        "single_point_of_failure" => "resting on one person",
        "deactivated_owner" => "held by a deactivated user",
        "self_review" => "self-reviewed",
        "last_month_slippage" => "left open from last month",
        "licence_expiring_unrenewed" => "expiring unrenewed",
        "licence_lapsed_recent_unrenewed" or "expired_unrenewed_location" or "licence_type_lapse_rate" => "expired unrenewed",
        "multi_location_pattern" => "overdue at many sites",
        "category_overdue_skew" => "overdue",
        "ghost_location" => "with nothing configured",
        _ => string.Empty,
    };

    /// <summary>
    /// The two or three words that sit beside a figure on the card, so the hub can render
    /// "939 lapses · 345 locations" from <c>supporting_metrics</c> alone.
    ///
    /// <para>Deliberately shorter than <see cref="ShortNounForFact"/>: that one reads inside a
    /// sentence-length title and can afford "with personal liability", where a chip beside a bold
    /// numeral cannot. Same closed vocabulary rules either way - never "tasks", "stores" or
    /// "accounts" (CLAUDE.md Sec.5).</para>
    /// </summary>
    public static string MetricNoun(string factKey)
    {
        if (factKey.Contains("no_owner", StringComparison.Ordinal))
            return "unassigned";
        if (factKey.Contains("no_reviewer", StringComparison.Ordinal))
            return "unreviewed";
        if (factKey.Contains("liability", StringComparison.Ordinal))
            return "with liability";
        if (factKey.Contains("never_touched", StringComparison.Ordinal))
            return "never started";
        if (factKey.Contains("over_90", StringComparison.Ordinal))
            return "over 90 days";
        if (factKey.Contains("inactive", StringComparison.Ordinal))
            return "with inactive owners";
        if (factKey.Contains("self_review", StringComparison.Ordinal))
            return "self-reviewed";
        if (factKey.Contains("single_performer", StringComparison.Ordinal))
            return "on one person";
        if (factKey.Contains("expiring", StringComparison.Ordinal))
            return "expiring";
        if (factKey.Contains("expired", StringComparison.Ordinal) || factKey.Contains("lapsed", StringComparison.Ordinal))
            return "lapsed";
        if (factKey.Contains("critical", StringComparison.Ordinal))
            return "critical";
        if (factKey.Contains("still_open", StringComparison.Ordinal) || factKey.Contains("_open", StringComparison.Ordinal))
            return "open";
        if (factKey.Contains("overdue", StringComparison.Ordinal) || factKey.StartsWith("od_", StringComparison.Ordinal) || factKey.StartsWith("t_od_", StringComparison.Ordinal))
            return "overdue";
        if (factKey.Contains("due", StringComparison.Ordinal))
            return "due";
        return string.Empty;
    }

    /// <summary>
    /// The unit as it reads after the figure 1. "1 obligations self-reviewed" is the sort of thing
    /// a reader notices before they notice the number, so a chip of one says "obligation".
    /// </summary>
    public static string Singular(string unit) => unit switch
    {
        "obligations" => "obligation",
        "locations" => "location",
        "licences" => "licence",
        "licence types" => "licence type",
        "people" => "person",
        "Acts" => "Act",
        "categories" => "category",
        _ => unit.EndsWith('s') ? unit[..^1] : unit,
    };

    /// <summary>The same chip vocabulary for a detector-led headline.</summary>
    public static string MetricNounForDetector(string detector) => detector switch
    {
        "liability_share" or "liability_overdue_location" => "with liability",
        "single_point_of_failure" => "on one person",
        "deactivated_owner" => "with inactive owners",
        "self_review" => "self-reviewed",
        "last_month_slippage" => "left open",
        "licence_expiring_unrenewed" => "expiring",
        "licence_lapsed_recent_unrenewed" or "expired_unrenewed_location" or "licence_type_lapse_rate" => "lapsed",
        "ghost_location" => "with nothing configured",
        "overdue_concentration" or "chronic_backlog" or "category_overdue_skew" or "multi_location_pattern" => "overdue",
        _ => string.Empty,
    };

    public static string Direction(string impactClass, string factKey) => impactClass switch
    {
        "volume" => "neutral",
        "performance" when factKey.Contains("on_time", StringComparison.Ordinal) || factKey.Contains("already_closed", StringComparison.Ordinal) => "higher_is_better",
        _ => "lower_is_better",
    };

    /// <summary>
    /// The ideal the metric points at: zero for a count of something that should not exist, 100 for
    /// a rate that should be complete.
    ///
    /// <para>This is NOT a forecast, a commitment, or a value any procedure returned - it is a
    /// constant implied by <see cref="Direction"/> alone, and the gap between it and the current
    /// value must never be presented as a projection. That is the distinction ADR-0004 drew when it
    /// removed the earlier <c>projection{}</c> block (CLAUDE.md non-negotiable 5).</para>
    /// </summary>
    public static int Target(string direction, string factKey) =>
        direction == "higher_is_better" && factKey.EndsWith("_pct", StringComparison.Ordinal) ? 100 : 0;

    /// <summary>
    /// The title's own words for what the week is about.
    ///
    /// <para>Deliberately a THIRD vocabulary, distinct from <see cref="MetricNoun"/> (the chips) and
    /// from the procedure's own metric label. [FOUND on tenant 1082, 2026-09-24] Without it a card
    /// said "people with personal liability" in its title, then again in its metric label, then
    /// twice more in the two sentences between them - four sightings of one phrase before the
    /// reader reached anything new.</para>
    /// </summary>
    public static string TitleTheme(string factKey)
    {
        if (factKey.Contains("no_owner", StringComparison.Ordinal))
            return "work with no owner";
        if (factKey.Contains("no_reviewer", StringComparison.Ordinal))
            return "closures with no reviewer";
        if (factKey.Contains("liability", StringComparison.Ordinal))
            return "personal exposure";
        if (factKey.Contains("never_touched", StringComparison.Ordinal))
            return "work never started";
        if (factKey.Contains("over_90", StringComparison.Ordinal))
            return "the ageing backlog";
        if (factKey.Contains("inactive", StringComparison.Ordinal))
            return "work held by inactive users";
        if (factKey.Contains("self_review", StringComparison.Ordinal))
            return "work approved by its own performer";
        if (factKey.Contains("single_performer", StringComparison.Ordinal))
            return "sites resting on one person";
        if (factKey.Contains("expiring", StringComparison.Ordinal))
            return "licences coming up for renewal";
        if (factKey.Contains("expired", StringComparison.Ordinal) || factKey.Contains("lapsed", StringComparison.Ordinal))
            return "lapsed licences";
        if (factKey.Contains("critical", StringComparison.Ordinal))
            return "critical obligations";
        if (factKey.Contains("still_open", StringComparison.Ordinal) || factKey.Contains("_open", StringComparison.Ordinal))
            return "work still open";
        if (factKey.Contains("overdue", StringComparison.Ordinal) || factKey.StartsWith("od_", StringComparison.Ordinal) || factKey.StartsWith("t_od_", StringComparison.Ordinal))
            return "the overdue backlog";
        if (factKey.Contains("due", StringComparison.Ordinal))
            return "work falling due";
        return string.Empty;
    }

    /// <summary>The same title vocabulary for a detector-led headline.</summary>
    public static string TitleThemeForDetector(string detector) => detector switch
    {
        "liability_share" or "liability_overdue_location" => "personal exposure",
        "overdue_concentration" => "where the backlog sits",
        "chronic_backlog" => "the ageing backlog",
        "single_point_of_failure" => "sites resting on one person",
        "deactivated_owner" => "work held by inactive users",
        "self_review" => "work approved by its own performer",
        "last_month_slippage" => "work still open from last month",
        "licence_expiring_unrenewed" => "licences coming up for renewal",
        "licence_lapsed_recent_unrenewed" or "expired_unrenewed_location" or "licence_type_lapse_rate" => "lapsed licences",
        "multi_location_pattern" => "one Act overdue across sites",
        "category_overdue_skew" => "one category running late",
        "ghost_location" => "sites with nothing configured",
        _ => string.Empty,
    };

    /// <summary>
    /// The card's title: where the insight stands in time, then what it is about. Built in code,
    /// never written by the model - it carries no figure, so there is nothing in it for a
    /// closed-number check to defend.
    /// </summary>
    public static string Title(string type, string unit, string theme, string windowScope, string section)
    {
        var stance = type switch
        {
            "predictive" => "Likely to slip",
            "descriptive" => "What changed",
            _ => "Where it sits",
        };

        var what = theme.Length > 0 ? theme : unit;

        // A theme that already names its month does not take the suffix as well.
        var when = what.Contains("month", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : windowScope switch
            {
                "prev" => " last month",
                "curr" when section == "rest_of_month" => " in the rest of this month",
                "curr" => " this month",
                _ => string.Empty,
            };

        return $"{stance} — {what}{when}";
    }

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string LongDate(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);


    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

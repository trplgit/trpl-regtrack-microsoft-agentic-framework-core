using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The weekly insight JSON lane's OWN model input for one subject (ADR-0004, revised 2026-09-23).
///
/// <para>The lane reads the same monthly subject procedures (sql/36-41) as the email, but it has
/// its own prompt (07b_insight_card.md) and its own message shape, built here. It does NOT reuse
/// <see cref="FreeMonthlyDigestPrompt.UserMessage"/>: that message is tuned for a five-paragraph
/// email and changes whenever the email prompts are tuned, and a card must not change with it.</para>
///
/// <para>What IS shared is the data layer's guardrails - the placeholder bindings, the closed
/// number set and the named-finding choice that <see cref="FreeMonthlyDigestPrompt.Build"/>
/// derives from proc values (CLAUDE.md non-negotiable 5). Those are facts about the data, not
/// email prose, and one derivation is safer than two. Only facts whose value is in that closed set
/// are sent, so the model can never be handed a number the validator would reject.</para>
/// </summary>
public sealed class InsightCardInput
{
    public static readonly IReadOnlyList<string> Tokens = ["{{PREV_MONTH}}", "{{CURR_MONTH}}", "{{AS_AT}}"];

    /// <summary>The headline fact plus the four next most severe - all a two-sentence narrative can carry.</summary>
    public const int MaxFactsSent = 5;

    /// <summary>Denominators only, so a figure can be given its "of how many".</summary>
    public const int MaxScopeFactsSent = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public required MonthlyDigestSlot Subject { get; init; }
    public required MonthlyDigestData Data { get; init; }

    /// <summary>The closed sets (bindings, allowed numbers, named findings, headline marker). Its UserMessage is never used.</summary>
    public required FreeMonthlyDigestPrompt Guardrails { get; init; }

    public required string UserMessage { get; init; }

    /// <summary>The headline's own key: a fact key, or the detector when a named finding leads.</summary>
    public required string HeadlineKey { get; init; }

    /// <summary>The headline's severity tier from the proc (1 most severe) - what the weekly selection ranks on.</summary>
    public required int HeadlineSeverityTier { get; init; }

    /// <summary>The headline's impact class ("volume" ranks below everything else at the same tier).</summary>
    public required string HeadlineImpactClass { get; init; }

    /// <summary>The derived insight type - descriptive, diagnostic or predictive.</summary>
    public required string HeadlineType { get; init; }

    /// <summary>
    /// The card's title, built here rather than after the model runs so the model can be SHOWN it
    /// and told not to echo it. A headline that repeats its own title spends the reader's five
    /// seconds saying one thing twice.
    /// </summary>
    public required string Title { get; init; }

    public required IReadOnlyList<MonthlyNamedFinding> NamedFindings { get; init; }

    /// <summary>Examples of aggregate patterns, in placeholder order - never findings.</summary>
    public required IReadOnlyList<MonthlyBoundExample> Examples { get; init; }

    public static InsightCardInput Build(MonthlyDigestData raw)
    {
        var guardrails = FreeMonthlyDigestPrompt.Build(raw);
        var data = guardrails.Data;   // after the period re-pick - the headline the closed set was built for
        var edition = data.Edition;

        var candidateLeads = string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal);
        /*  [FOUND on tenant 1082, 2026-09-23] Two licence rows, both "Motor Vehicle Pollution under
            Control" at "Khavda" (expired a day apart), were both named, and the card read "X at
            Khavda, X at Khavda and X at Khavda". The card shows no dates, so a finding whose bound
            name and site repeat an earlier one's is the same name to the reader - keep the first. */
        var named = new List<MonthlyNamedFinding>();
        foreach (var n in guardrails.NamedFindings)
        {
            if (named.Count == InsightCardRules.NamedFindingsCap)
                break;
            if (!named.Any(earlier => ReadsTheSameOnTheCard(earlier, n, guardrails.Bindings)))
                named.Add(n);
        }

        var headlineFinding = candidateLeads ? named.FirstOrDefault(n => n.Slot == 1) : null;
        var headlineFact = candidateLeads ? null : data.Facts.SingleOrDefault(f => f.IsHeadline);

        if (headlineFinding is null && headlineFact is null)
            throw new InvalidOperationException($"The {edition.Slot} data carries no headline fact or finding - refusing to build a card input.");

        /*  [TRIMMED 2026-09-24] The card is two sentences about ONE figure, so it does not need the
            email's whole material set. The headline plus the four next most severe facts is what a
            40-to-70-word narrative can actually use, and every row beyond that is input billed on
            every call for text no reader will ever see.                                          */
        var facts = data.Facts
            .Where(f => f.WindowScope != "ctx")
            .Where(f => f.IsHeadline || (f.FactValue > 0 && f.SeverityTier <= 3))
            .Where(f => guardrails.AllowedNumbers.Contains(f.FactValue))
            .OrderBy(f => f.IsHeadline ? 0 : 1).ThenBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder)
            .Take(MaxFactsSent)
            .ToList();

        // Two denominators at most: the reader needs "of how many", not the whole estate breakdown.
        var scope = data.Facts
            .Where(f => f.WindowScope == "ctx" && f.FactValue > 0 && guardrails.AllowedNumbers.Contains(f.FactValue))
            .OrderBy(f => f.DisplayOrder)
            .Take(MaxScopeFactsSent)
            .Select(f => new { f.FactKey, f.FactValue, Label = Plain(f.DisplayLabel) })
            .ToList();

        var headlineType = headlineFinding is not null
            ? InsightCardRules.TypeForDetector(headlineFinding.Candidate.Detector)
            : InsightCardRules.TypeForFact(headlineFact!.WindowScope, headlineFact.Section, headlineFact.FactKey);

        var headlineUnit = headlineFinding is not null
            ? InsightCardRules.UnitForDetector(headlineFinding.Candidate.Detector)
            : InsightCardRules.UnitForFact(headlineFact!.FactKey, edition.Slot);

        var theme = headlineFinding is not null
            ? InsightCardRules.TitleThemeForDetector(headlineFinding.Candidate.Detector)
            : InsightCardRules.TitleTheme(headlineFact!.FactKey);

        var title = InsightCardRules.Title(
            headlineType, headlineUnit, theme,
            headlineFinding is not null ? "stock" : headlineFact!.WindowScope,
            headlineFinding is not null ? string.Empty : headlineFact!.Section);

        var message = new
        {
            Subject = MonthlyDigestCalendar.Title(edition.Slot),
            Title = title,
            Period = new
            {
                PrevMonth = "{{PREV_MONTH}}",
                CurrMonth = "{{CURR_MONTH}}",
                AsAt = "{{AS_AT}}",
                Note = "Write the tokens exactly; code replaces them with the real month names and date.",
            },
            Headline = headlineFinding is not null
                ? new { Source = "named_finding", Key = headlineFinding.Candidate.Detector, Placeholder = headlineFinding.NamePlaceholder, Value = (int?)null }
                : new { Source = "fact", Key = headlineFact!.FactKey, Placeholder = (string?)null, Value = (int?)headlineFact.FactValue },
            MustUse = named.Select(n => n.NamePlaceholder).Where(p => p is not null).ToList(),
            Scope = scope,
            Signals = FreeMonthlyDigestPrompt.SignalsFrom(data.Facts),
            Facts = facts.Select(f => new
            {
                f.FactKey,
                f.FactValue,
                Label = Plain(f.DisplayLabel),
                f.WindowScope,
                Period = PeriodOf(f.WindowScope, f.Section, f.FactKey),
                f.ImpactClass,
                AsAtRequired = f.AsAtRequired ? true : (bool?)null,
                IsHeadline = f.IsHeadline ? true : (bool?)null,
            }),
            Examples = guardrails.Examples.Select(e => new
            {
                Placeholder = e.Placeholder,
                AtPlaceholder = e.AtPlaceholder,
                e.Example.PatternFactKey,
                e.Example.Detector,
                e.Example.EntityKind,
                e.Example.ItemCount,
                e.Example.BaseCount,
                e.Example.UnitLabel,
            }),
            NamedFindings = named.Select(n => new
            {
                n.Slot,
                Placeholder = n.NamePlaceholder,
                AtPlaceholder = n.AtPlaceholder,
                DatePlaceholder = n.DatePlaceholder,
                n.Candidate.Detector,
                Means = Means(n.Candidate.Detector),
                Figures = Figures(n.Candidate),
                n.Candidate.EntityKind,
                n.Candidate.Metric,
                n.Candidate.ItemCount,
                n.Candidate.BaseCount,
                MetricPct = guardrails.AllowedPercentages.Contains(n.Candidate.MetricPct ?? -1) ? n.Candidate.MetricPct : null,
                TenantPct = guardrails.AllowedPercentages.Contains(n.Candidate.TenantPct ?? -1) ? n.Candidate.TenantPct : null,
                n.Candidate.ProblemCount,
                n.Candidate.PopulationCount,
                n.Candidate.ResidualCount,
                IsHeadline = n.Slot == 1 && candidateLeads ? true : (bool?)null,
            }),
        };

        return new InsightCardInput
        {
            Subject = edition.Slot,
            Data = data,
            Guardrails = guardrails,
            UserMessage = JsonSerializer.Serialize(message, JsonOptions),
            HeadlineKey = headlineFinding?.Candidate.Detector ?? headlineFact!.FactKey,
            HeadlineSeverityTier = headlineFinding?.Candidate.SeverityTier ?? headlineFact!.SeverityTier,
            HeadlineImpactClass = headlineFinding is not null ? InsightCardRules.ImpactClassForDetector(headlineFinding.Candidate.Detector) : headlineFact!.ImpactClass,
            HeadlineType = headlineType,
            Title = title,
            NamedFindings = named,
            Examples = guardrails.Examples,
        };
    }

    private static bool ReadsTheSameOnTheCard(MonthlyNamedFinding a, MonthlyNamedFinding b, IReadOnlyDictionary<string, string> bindings)
    {
        static string? Bound(string? placeholder, IReadOnlyDictionary<string, string> map) =>
            placeholder is not null && map.TryGetValue(placeholder, out var value) ? value : null;

        var nameA = Bound(a.NamePlaceholder, bindings);
        return nameA is not null
               && string.Equals(a.Candidate.EntityKind, b.Candidate.EntityKind, StringComparison.Ordinal)
               && string.Equals(nameA, Bound(b.NamePlaceholder, bindings), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Bound(a.AtPlaceholder, bindings), Bound(b.AtPlaceholder, bindings), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The time window a fact covers, in the words the reader should see. [2026-09-25] Readers
    /// could not tell whether a card's figures were last month's, this month's, or the whole
    /// backlog - the stock figures in particular count everything overdue, however old.
    /// </summary>
    internal static string PeriodOf(string windowScope, string section, string factKey) => windowScope switch
    {
        "prev" => "obligations that fell due in {{PREV_MONTH}}, as they stand on {{AS_AT}}",
        "curr" when section == "rest_of_month" || factKey.StartsWith("rm_", StringComparison.Ordinal) => "obligations still to fall due between {{AS_AT}} and the end of {{CURR_MONTH}}",
        "curr" => "obligations due in {{CURR_MONTH}} up to {{AS_AT}}",
        "stock" => "all obligations overdue as on {{AS_AT}}, including those that fell due in earlier months - not only this month",
        _ => "a total used for comparison",
    };

    /// <summary>
    /// What each number on a named finding counts, and over what period - by the proc's own Metric
    /// (sql/36-41). [FOUND 2026-09-25] Given only "metric_pct: 81" a draft wrote "holds 81% of the
    /// overdue work" for a site where 81% of ITS OWN overdue obligations carry liability: a true
    /// number attached to the wrong whole. The closed-number check cannot see that.
    /// </summary>
    internal static string Figures(MonthlyCandidate c)
    {
        const string asAt = "Period: overdue as on {{AS_AT}}, including obligations that fell due in earlier months.";
        var flagged = "problem_count = how many were flagged for this; population_count = how many were compared.";

        var figures = c.Metric switch
        {
            "overdue_with_liability_pct" =>
                "item_count = overdue obligations here that carry personal criminal liability; base_count = all overdue obligations here; "
                + "metric_pct = the share of THIS one's OWN overdue obligations that carry that liability (not a share of the organisation's overdue work); "
                + "tenant_pct = the same share across your whole organisation. " + asAt,
            "overdue_over_90_days_pct" =>
                "item_count = obligations here overdue for more than 90 days; base_count = all overdue obligations here; "
                + "metric_pct = the share of THIS one's OWN overdue obligations that are more than 90 days old; tenant_pct = the same share across your whole organisation. " + asAt,
            "share_of_all_overdue_pct" =>
                "item_count = overdue obligations held here; base_count = all overdue obligations in your organisation; "
                + "metric_pct = this one's share of ALL overdue obligations in your organisation. " + asAt,
            "last_month_still_open_pct" =>
                "item_count = obligations that fell due here in {{PREV_MONTH}} and are still open on {{AS_AT}}; base_count = all obligations that fell due here in {{PREV_MONTH}}; "
                + "metric_pct = the share of those still open; tenant_pct = the same share across your whole organisation.",
            "obligation_overdue_rate_pct" =>
                "metric_pct = the share of THIS category's obligations that are overdue; tenant_pct = the share of all obligations in your organisation that are overdue. " + asAt,
            "licences_expired_unrenewed_pct" =>
                "item_count = licences here that have expired with no renewal in progress; base_count = all licences held here; "
                + "metric_pct = the share of THIS one's OWN licences in that state; tenant_pct = the same share across your whole organisation. Period: the position on {{AS_AT}}.",
            "liability_overdue_items" =>
                "item_count = overdue obligations here that carry personal criminal liability. " + asAt,
            "open_items_with_one_performer" =>
                "item_count = open obligations here, every one assigned to the same single person. Period: open on {{AS_AT}}.",
            "expired_on" or "expires_on" =>
                "The date placeholder is the licence's expiry date.",
            _ => string.Empty,
        };

        return figures.Length == 0 ? flagged : figures + " " + flagged;
    }

    /// <summary>
    /// What a detector found, in plain words - the card lane's own glossary, sent only with a finding
    /// that uses it. Written the way the card should read (07b): everyday words, "your organisation",
    /// never "scope", because the model echoes the wording it is handed.
    /// </summary>
    internal static string Means(string detector) => detector switch
    {
        "last_month_slippage" => "More of last month's work is still open here than across the rest of your organisation.",
        "single_point_of_failure" => "Every open obligation here rests on one person; nobody else is assigned to any of it.",
        "overdue_concentration" => "This one holds a big part of all overdue work in your organisation.",
        "chronic_backlog" => "More of its overdue work is over 90 days old than across the rest of your organisation.",
        "liability_share" => "A higher share of its overdue obligations carry personal liability for the responsible officer than elsewhere in your organisation.",
        "multi_location_pattern" => "This Act is overdue at many of the sites it applies to.",
        "category_overdue_skew" => "Work of this kind is overdue far more often than other work.",
        "liability_overdue_location" => "This site has more overdue obligations that carry personal liability for the responsible officer than the rest of your organisation.",
        "deactivated_owner" => "Open obligations are held by someone who is no longer an active user of RegTrack.",
        "self_review" => "The same person does the work and approves it.",
        "ghost_location" => "A site in your organisation with no obligations set up at all.",
        "licence_expiring_unrenewed" => "This licence expires this month with no renewal filed.",
        "licence_lapsed_recent_unrenewed" => "This licence has expired and still has no renewal in progress.",
        "expired_unrenewed_location" => "Expired-and-unrenewed licences are concentrated at this one site.",
        "licence_type_lapse_rate" => "Licences of this type lapse without renewal more often than other types.",
        _ => string.Empty,
    };

    public string AsAtLabel => Data.AsOf.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The procs say "items" in some rows and "obligations" in others for the same unit; the card says one word.</summary>
    internal static string Plain(string label) =>
        System.Text.RegularExpressions.Regex.Replace(label, @"\bitems?\b", m => m.Value.EndsWith('s') ? "obligations" : "obligation",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace("your scope", "your organisation", StringComparison.OrdinalIgnoreCase)
            .Replace("the scope", "your organisation", StringComparison.OrdinalIgnoreCase)
            .Trim();
}

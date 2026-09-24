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
        var named = guardrails.NamedFindings.Take(InsightCardRules.NamedFindingsCap).ToList();
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

    /// <summary>What a detector found, in plain words - the card lane's own glossary, sent only with a finding that uses it.</summary>
    internal static string Means(string detector) => detector switch
    {
        "last_month_slippage" => "A higher share of this one's previous-month obligations is still open than across the whole scope.",
        "single_point_of_failure" => "Every open obligation here rests on one person; nobody else is assigned to any of it.",
        "overdue_concentration" => "This one holds a large share of everything that is overdue.",
        "chronic_backlog" => "Its overdue obligations have sat more than 90 days, at a higher rate than the rest of the scope.",
        "liability_share" => "More of its overdue obligations carry personal criminal liability than elsewhere in the scope.",
        "multi_location_pattern" => "This Act is overdue at many of the sites it applies to.",
        "category_overdue_skew" => "This category of obligation is overdue far more often than everything else.",
        "liability_overdue_location" => "This site is well above the scope's own rate on liability-bearing overdue obligations.",
        "deactivated_owner" => "Open obligations are held by someone who is no longer an active user of RegTrack.",
        "self_review" => "The same person performs the obligation and approves it.",
        "ghost_location" => "In scope, but with no obligations configured at all.",
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

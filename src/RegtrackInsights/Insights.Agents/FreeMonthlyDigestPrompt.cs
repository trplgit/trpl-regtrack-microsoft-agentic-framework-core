using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// One of the (at most two) candidates the email may point to - the SQL marks them with DefaultSlot
/// 1 / 2. The model only ever sees <see cref="NamePlaceholder"/>; the real label is bound after
/// validation (spec Sec.4: "a wrong or invented name is structurally impossible").
/// </summary>
public sealed record MonthlyNamedFinding(int Slot, MonthlyCandidate Candidate)
{
    /// <summary>Null when the SQL gave no label (names withheld, unnamed master row) - never named then.</summary>
    public string? NamePlaceholder => IsNameable(Candidate.EntityLabel) ? $"{{{{NAME_{Slot}}}}}" : null;

    public string? AtPlaceholder => IsNameable(Candidate.ContextLabel) ? $"{{{{NAME_{Slot}_AT}}}}" : null;

    private static bool IsNameable(string? label) =>
        label is not null && FreeMonthlyDigestPrompt.CleanLabel(label).Length > 0;

    public string? DatePlaceholder => Candidate.EventDate is null ? null : $"{{{{DATE_{Slot}}}}}";
}

/// <summary>
/// Everything the monthly writer, validator and binder need for ONE scope group, built once from the
/// slot proc's output (CLAUDE.md non-negotiable 5: every number and name is fixed before the LLM runs).
///
/// <see cref="UserMessage"/> is the closed input the prompts (06_freetier_monthly_*) describe;
/// <see cref="Bindings"/> maps every placeholder the model may write to its real text;
/// <see cref="AllowedNumbers"/> / <see cref="AllowedPercentages"/> are the validator's closed sets.
/// </summary>
public sealed partial class FreeMonthlyDigestPrompt
{
    /// <summary>Age-band boundaries the prompts let the model name ("more than 90 days") - shared Rule 1.</summary>
    public static readonly IReadOnlyList<int> BandLiterals = [30, 31, 60, 61, 90];

    /*  WhenWritingNull, never WhenWritingDefault - a FactValue of 0 is information ("none of these
        carry personal liability", shared Rule 11) and must reach the model.                     */
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required MonthlyDigestData Data { get; init; }
    public required string UserMessage { get; init; }
    public required IReadOnlyDictionary<string, string> Bindings { get; init; }
    public required IReadOnlyList<MonthlyNamedFinding> NamedFindings { get; init; }
    public required IReadOnlySet<int> AllowedNumbers { get; init; }
    public required IReadOnlySet<int> AllowedPercentages { get; init; }

    /// <summary>
    /// What the first paragraph after the greeting must contain (shared Rule 7): the headline fact's
    /// value, or the headline finding's placeholder. Null when the lead cannot be checked (a
    /// candidate headline with no nameable label).
    /// </summary>
    public string? HeadlineMarker { get; init; }

    public static FreeMonthlyDigestPrompt Build(MonthlyDigestData data)
    {
        var edition = data.Edition;

        var named = data.Candidates
            .Where(c => c.DefaultSlot is 1 or 2)
            .OrderBy(c => c.DefaultSlot)
            .Select(c => new MonthlyNamedFinding(c.DefaultSlot!.Value, c))
            .ToList();

        data = WithPeriodHeadline(data, named);

        var candidateLeads = string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal);
        var headlineFinding = candidateLeads ? named.FirstOrDefault(n => n.Slot == 1) : null;
        if (candidateLeads && headlineFinding is null)
            throw new InvalidOperationException("HeadlineSource is 'candidate' but no candidate carries DefaultSlot 1 - the slot proc contract is broken.");

        var headlineFact = candidateLeads ? null : data.Facts.SingleOrDefault(f => f.IsHeadline)
            ?? throw new InvalidOperationException("HeadlineSource is 'fact' but no fact carries IsHeadline - the slot proc contract is broken.");

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{PREV_MONTH}}"] = edition.PrevMonthStart.ToString("MMMM", CultureInfo.InvariantCulture),
            ["{{CURR_MONTH}}"] = edition.CurrMonthStart.ToString("MMMM", CultureInfo.InvariantCulture),
            ["{{AS_AT}}"] = data.AsOf.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };

        foreach (var finding in named)
        {
            if (finding.NamePlaceholder is { } name)
                bindings[name] = CleanLabel(finding.Candidate.EntityLabel!);
            if (finding.AtPlaceholder is { } at)
                bindings[at] = CleanLabel(finding.Candidate.ContextLabel!);
            if (finding.DatePlaceholder is { } date)
                bindings[date] = finding.Candidate.EventDate!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        /*  WHAT THE MODEL SEES IS FILTERED (shared Rule 15). Handed ~40 counts, a model writes a
            tour of the counts - the first real preview read as a list, not an email. The data layer
            has already ranked every fact by severity, so the model gets the headline, what is
            non-zero and material (tiers 1-3), the patterns, and whatever base fact a kept "of those"
            line needs to read correctly. The rest still exists in control totals and the fallback;
            it just is not the model's to list. It is also ~a third fewer input tokens per email.   */
        var sentFacts = ForTheModel(data.Facts, edition.Slot);

        var numbers = new HashSet<int>(BandLiterals);
        var percentages = new HashSet<int>();

        foreach (var fact in sentFacts)
        {
            numbers.Add(fact.FactValue);
            if (fact.FactKey.EndsWith("_pct", StringComparison.Ordinal))
                percentages.Add(fact.FactValue);
        }

        /*  "the 3 people / locations / laws holding the most" - the prompt tells the model to write
            "three", but quoting the label's own digit is not a fabrication, so it is allowed exactly
            when such a fact is present (the office/officer lesson in FreeDigestValidator).        */
        if (sentFacts.Any(f => f.FactKey.Contains("_top3_", StringComparison.Ordinal)))
            numbers.Add(3);

        foreach (var c in named.Select(n => n.Candidate))
        {
            foreach (var value in new[] { c.MetricPct, c.TenantPct, c.ItemCount, c.BaseCount })
                if (value is { } v)
                    numbers.Add(v);

            numbers.Add(c.ProblemCount);
            numbers.Add(c.PopulationCount);
            numbers.Add(c.ResidualCount);

            if (c.MetricPct is { } mp)
                percentages.Add(mp);
            if (c.TenantPct is { } tp)
                percentages.Add(tp);
        }

        /*  [MEASURED 2026-09-21] must_use repeats the placeholders as a bare list, right at the top
            of the input. The rule is already in the shared prompt, but with two findings the model
            routinely named only the first: three of five emails in one run failed on "never uses
            {{NAME_2}}". A requirement stated in the DATA the model is reading is followed; the same
            requirement stated in prose two thousand tokens earlier is not.                       */
        var message = new
        {
            slot = SlotName(edition.Slot),
            headline = candidateLeads ? "named_finding" : "fact",
            must_use = named.Select(n => n.NamePlaceholder).Where(p => p is not null).ToList(),
            /*  [2026-09-21] Section and SeverityTier are NOT sent. Both appeared only in the
                prompt's "what you are given" list and were never referenced by any instruction -
                the data layer has already used severity to choose the headline and to decide what
                survives ForTheModel, so restating it to the model influenced nothing. Ten fields
                across twenty facts is real input cost on every call; these two were paying nothing
                back. Everything below IS load-bearing: WindowScope drives tense, ImpactClass picks
                the consequence, AsAtRequired the "as at" line, Backlog the ordering rules.      */
            facts = sentFacts.Select(f => new
            {
                f.FactKey,
                f.FactValue,
                f.DisplayLabel,
                f.WindowScope,
                f.ImpactClass,
                f.AsAtRequired,
                Backlog = IsBacklogFact(f),
                f.IsHeadline,
            }),
            named_findings = named.Select(n => new
            {
                n.Slot,
                Placeholder = n.NamePlaceholder,
                AtPlaceholder = n.AtPlaceholder,
                DatePlaceholder = n.DatePlaceholder,
                n.Candidate.Detector,
                n.Candidate.EntityKind,
                n.Candidate.Metric,
                n.Candidate.ItemCount,
                n.Candidate.BaseCount,
                n.Candidate.MetricPct,
                n.Candidate.TenantPct,
                n.Candidate.ProblemCount,
                n.Candidate.PopulationCount,
                n.Candidate.ResidualCount,
                n.Candidate.AsAtRequired,
                Backlog = IsBacklogDetector(n.Candidate.Detector),
                IsHeadline = ReferenceEquals(n, headlineFinding),
            }),
        };

        return new FreeMonthlyDigestPrompt
        {
            Data = data,
            UserMessage = JsonSerializer.Serialize(message, JsonOptions),
            Bindings = bindings,
            NamedFindings = named,
            AllowedNumbers = numbers,
            AllowedPercentages = percentages,
            HeadlineMarker = headlineFact is not null
                ? headlineFact.FactValue.ToString(CultureInfo.InvariantCulture)
                : headlineFinding!.NamePlaceholder,
        };
    }

    /// <summary>
    /// Detectors measured over the ALL-TIME overdue / expired backlog (any due date), as opposed to
    /// last month, this month, or the current state of open work. Their findings may appear in the
    /// email, but never as its lead - see <see cref="WithPeriodHeadline"/>.
    /// </summary>
    private static readonly HashSet<string> BacklogDetectors = new(StringComparer.Ordinal)
    {
        "liability_share", "chronic_backlog", "overdue_concentration",       // sql/37 (users, location, act)
        "liability_overdue_location", "category_overdue_skew",               // sql/36 (overview)
        "multi_location_pattern",                                            // sql/40 (act)
        "expired_unrenewed_location", "licence_type_lapse_rate",             // sql/41 (licence)
    };

    public static bool IsBacklogDetector(string detector) => BacklogDetectors.Contains(detector);

    /// <summary>Aggregate-mode pattern facts of the backlog detectors above (their names carry no "overdue").</summary>
    private static readonly HashSet<string> BacklogPatternFacts = new(StringComparer.Ordinal)
    {
        "pat_liability_share", "pat_chronic_backlog", "pat_overdue_concentration", "pat_multi_location_pattern",
        "pat_expired_unrenewed_location", "pat_licence_type_lapse_rate",
        "pattern_liability_locations", "pattern_categories",
    };

    /// <summary>
    /// A fact about the all-time OVERDUE or EXPIRED backlog: a stock fact (as of today, any due
    /// date) that counts overdue / expired / lapsed work. NOT every stock fact - "people holding
    /// open work", "open items with no owner", "locations with one performer" describe the current
    /// state of work and may lead. Decided from the proc's own fact keys, never from values.
    /// </summary>
    public static bool IsBacklogFact(MonthlyFact fact) =>
        fact.WindowScope == "stock"
        && (BacklogKeyWords().IsMatch(fact.FactKey) || BacklogPatternFacts.Contains(fact.FactKey));

    [GeneratedRegex(@"(^|_)od_|overdue|over_90|expired|lapse")]
    private static partial Regex BacklogKeyWords();

    /// <summary>
    /// THE EMAIL IS ABOUT LAST MONTH AND THIS MONTH - the overdue backlog is context, never the lead.
    ///
    /// The slot procs rank period facts first, but fall back to an all-time stock fact (od_*,
    /// lic_expired_unrenewed ...) whenever every period fact is zero, and a dimension proc can lead
    /// with a backlog-based finding. On a quiet month (and on stale data) that makes "N items are
    /// overdue, whatever their due date" the hero of every email - the same number, every month.
    ///
    /// So the lead is re-decided here, from the data's own classification, never from tenant-specific
    /// values: a backlog finding (<see cref="IsBacklogDetector"/>) or backlog fact
    /// (<see cref="IsBacklogFact"/>) loses the lead, and is replaced by the most severe non-zero fact
    /// that is neither backlog nor context (last month, this month, or the current state of open
    /// work), preferring a finding over plain volume; if all of those are zero, by the first period
    /// volume fact ("N items fall due before month end"). The SQL's own choice stands whenever it is
    /// not backlog.
    /// </summary>
    /// <summary>
    /// The worst standing exposure that carries personal criminal liability, if any. This is the
    /// only backlog a headline may come from - see the reasoning in <see cref="WithPeriodHeadline"/>.
    /// </summary>
    private static MonthlyFact? LiabilityBacklog(MonthlyDigestData data) =>
        data.Facts
            .Where(f => f.FactValue > 0 && IsBacklogFact(f) && f.WindowScope != "ctx" && f.SeverityTier == 1)
            .OrderBy(f => f.DisplayOrder)
            .FirstOrDefault();

    internal static MonthlyDigestData WithPeriodHeadline(MonthlyDigestData data, IReadOnlyList<MonthlyNamedFinding> named)
    {
        if (string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal))
        {
            // No Slot 1 candidate = a broken proc contract: return unchanged so Build fails closed,
            // never paper over it by picking a fact.
            var lead = named.FirstOrDefault(n => n.Slot == 1);
            if (lead is null || !IsBacklogDetector(lead.Candidate.Detector))
                return data;
        }
        /*  A zero cannot lead an email. [FOUND LIVE on tenant 5, 2026-09-21] sql/40 marked
            law_with_last_month_open as the headline at a value of 0, and the Act email opened
            "0 laws still have items open from that period" - a true statement that tells the
            reader nothing and reads as an error. Re-pick exactly as for a backlog headline.   */
        else if (data.Facts.SingleOrDefault(f => f.IsHeadline) is { } current
                 && !IsBacklogFact(current) && current.FactValue > 0
                 && (current.SeverityTier == 1 || LiabilityBacklog(data) is null))
        {
            // The proc's choice stands, unless it is outranked by liability exposure - see below.
            return data;
        }

        static bool Eligible(MonthlyFact f) =>
            !IsBacklogFact(f) && f.WindowScope != "ctx" && !f.FactKey.EndsWith("_of", StringComparison.Ordinal);

        var ordered = data.Facts.Where(Eligible).OrderBy(f => f.DisplayOrder).ToList();
        var pick =
            ordered.Where(f => f.FactValue > 0 && f.ImpactClass != "volume")
                   .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder).FirstOrDefault()
            ?? ordered.FirstOrDefault(f => f.FactValue > 0 && f.WindowScope is "prev" or "curr")
            ?? ordered.FirstOrDefault(f => f.FactValue > 0)

            /*  [FOUND LIVE on tenant 5, 2026-09-21] When every period fact is zero - a tenant whose
                whole problem IS the standing backlog - the fallbacks below would hand back the same
                zero and the email opened "0 laws still have items open from it". A real backlog
                figure is a better lead than a true zero, so prefer any non-zero fact first.      */
            ?? data.Facts.Where(f => f.FactValue > 0 && f.WindowScope != "ctx" && !f.FactKey.EndsWith("_of", StringComparison.Ordinal))
                         .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder).FirstOrDefault()

            ?? ordered.FirstOrDefault(f => f.WindowScope is "prev" or "curr")
            ?? ordered.FirstOrDefault();

        /*  [FOUND LIVE on tenant 5, 2026-09-21] "The backlog never leads" is right for a tenant with
            a live month, and wrong for one whose month is nearly empty. Tenant 5's Overview led with
            a single lapsed licence (tier 2) while 50 overdue items carrying personal criminal
            liability (tier 1) sat unmentioned, purely because those are backlog. A reader opening
            that email learns the least important true thing about their estate.

            The standing instruction is that the backlog is never the hero of an email, and that
            holds: ordinary backlog volume never takes the lead however large it is - 22,070 overdue
            items still yield to a single obligation falling due this month, because the email is
            about the period. The ONE exception is severity tier 1, personal criminal liability,
            and only when the period has nothing at that tier itself. Exposure that can put the
            reader in the dock outranks a lapsed licence; a big number does not.                  */
        if (LiabilityBacklog(data) is { } liabilityBacklog && (pick is null || pick.SeverityTier > 1))
            pick = liabilityBacklog;

        // Only when a slot returns nothing at all - keep the SQL's choice rather than invent one.
        if (pick is null)
            return data;

        return data with
        {
            HeadlineSource = "fact",
            Facts = data.Facts.Select(f => f with { IsHeadline = ReferenceEquals(f, pick) }).ToList(),
        };
    }

    /// <summary>
    /// The facts the model is given: the headline, everything non-zero at severity tier 1-3, the
    /// non-zero patterns with their `_of` partner, and the base fact of any section whose kept line
    /// reads "of those ..." (those labels are meaningless without the fact above them).
    /// Deterministic, and driven only by the proc's own severity/labels - never by tenant values.
    /// </summary>
    private static readonly HashSet<string> AlwaysSent = new(StringComparer.Ordinal)
    {
        "lm_due",                    // 06a: the only denominator in the last-month section
        "law_in_scope",              // 06d
        "loc_in_scope",              // 06c
        "u_people_with_open_work",   // 06b
        "u_open_items",              // 06b, and its "no work in scope" branch
        "lic_total",                 // 06e, and its "no licences in scope" branch
    };

    internal static IReadOnlyList<MonthlyFact> ForTheModel(IReadOnlyList<MonthlyFact> facts, MonthlyDigestSlot slot)
    {
        /*  [MEASURED 2026-09-20] AlwaysSent exists because severity and usefulness are different
            axes. Every fact here is a denominator a slot prompt NAMES ("N of law_in_scope laws",
            "loc_with_last_month_open against loc_in_scope"), but each is tier 4-5 volume/ctx, so
            the tier filter below stripped them. Ordered to write "N of M" with no M in the input,
            the model took M from its worked example (37 laws) or built one by subtraction
            (45 - 1 - 3 = 41). Both drafts died on the validator. A fact a prompt requires must
            reach the model; the filter's job is to cut the tour, not the sentence.
            lic_total and u_open_items also carry the "nothing in scope" branch in 06e/06b, which
            is why shared Rule 11 names them as its zero exception.                             */
        var ordered = facts.OrderBy(f => f.DisplayOrder).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in ordered)
            if (f.IsHeadline || AlwaysSent.Contains(f.FactKey) || (f.FactValue > 0 && f.SeverityTier <= 3))
                keep.Add(f.FactKey);

        // A pattern is only legible with the population it was measured against.
        foreach (var f in ordered.Where(f => keep.Contains(f.FactKey) && f.FactKey.StartsWith("pat", StringComparison.Ordinal)))
            if (ordered.FirstOrDefault(o => o.FactKey == f.FactKey + "_of") is { } partner)
                keep.Add(partner.FactKey);

        /*  "of those ..." refers to the population its SECTION opens with, not to the line above
            it: in last_month, lm_due is "those" for every one of lm_on_time / lm_late /
            lm_still_open alike. Keeping the previous line instead handed the model a sibling
            count as if it were the denominator, which is how a draft reached "29 of the 41".  */
        foreach (var f in ordered.Where(f => keep.Contains(f.FactKey)
                                             && f.DisplayLabel.StartsWith("of those", StringComparison.OrdinalIgnoreCase)))
        {
            var sectionBase = ordered.FirstOrDefault(o => o.Section == f.Section
                                                          && !o.DisplayLabel.StartsWith("of those", StringComparison.OrdinalIgnoreCase));
            if (sectionBase is not null && sectionBase.DisplayOrder < f.DisplayOrder)
                keep.Add(sectionBase.FactKey);
        }

        var kept = ordered.Where(f => keep.Contains(f.FactKey)).ToList();

        /*  [MEASURED 2026-09-21] A hard ceiling on how much any one email is handed. The Overview
            was reaching the model with 30 facts where the dimension emails got 14 - and it read
            like a tour of figures while they read like findings, on the same tenant, same model,
            same rules. More material does not produce a better email; it produces a longer one.
            When the ceiling bites, the least severe ordinary facts go first: the headline, the
            denominators a slot prompt names, the pattern partners and the section bases that make
            an "of those" line readable are all structural and stay whatever their tier.         */
        if (kept.Count <= MaxFactsFor(slot))
            return kept;

        var structural = kept
            .Where(f => f.IsHeadline || AlwaysSent.Contains(f.FactKey) || f.FactKey.StartsWith("pat", StringComparison.Ordinal)
                        || f.FactKey.EndsWith("_of", StringComparison.Ordinal))
            .Select(f => f.FactKey)
            .ToHashSet(StringComparer.Ordinal);

        var room = MaxFactsFor(slot) - structural.Count;
        var bySeverity = kept
            .Where(f => !structural.Contains(f.FactKey))
            .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder)
            .Take(Math.Max(room, 0))
            .Select(f => f.FactKey);

        var final = structural.Concat(bySeverity).ToHashSet(StringComparer.Ordinal);
        return kept.Where(f => final.Contains(f.FactKey)).ToList();
    }

    /// <summary>
    /// The most facts an email may be given. Sized from the dimension emails, which read as findings
    /// at ~14 while the Overview toured its 30.
    ///
    /// <para>The Overview gets more, deliberately. [FOUND LIVE on tenant 5, 2026-09-21] Capping it
    /// at 14 stopped the touring but left it thin - three short paragraphs covering the whole estate
    /// in a month with little period activity. It is the only email that spans every section (last
    /// month, this month, the standing position, licences), so 14 facts is thinner per section than
    /// a dimension email gets for its one subject. It is also the flagship: the month's opening
    /// email and, for many readers, the only one read closely.</para>
    /// </summary>
    private static int MaxFactsFor(MonthlyDigestSlot slot) =>
        slot == MonthlyDigestSlot.Overview ? 20 : 14;

    public static string SlotName(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => "overview",
        MonthlyDigestSlot.Users => "users",
        MonthlyDigestSlot.Location => "location",
        MonthlyDigestSlot.Act => "act",
        MonthlyDigestSlot.Licence => "licence",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };

    /// <summary>
    /// A master-data label goes into prose verbatim, so the characters that carry meaning in the body
    /// are removed: <c>*</c> (bold markers) and braces (placeholder syntax). Whitespace runs, including
    /// line breaks, collapse to one space. The renderer HTML-encodes the result.
    /// </summary>
    internal static string CleanLabel(string label)
    {
        var clean = Whitespace().Replace(label.Replace("*", string.Empty).Replace("{", string.Empty).Replace("}", string.Empty), " ").Trim();

        /*  [FOUND LIVE on tenant 5, 2026-09-21] A law's master-data name can be several statutes
            joined together - one Act email carried "Maharashtra Shops and Establishments
            (Regulation of Employment and Conditions of Service) Act, 2017 and Maharashtra Shops
            and Establishments (Regulation of Employment and Conditions of Service) Rules, 2018",
            twice, in a 200-word email. Keeping only the first statute leaves a REAL name that the
            reader can search for, rather than a truncation ending mid-word. The rest of the
            grouping is exactly what the paid product exists to show.                           */
        foreach (var joiner in JoinedNames)
        {
            var at = clean.IndexOf(joiner, StringComparison.OrdinalIgnoreCase);
            if (at > 20)
                clean = clean[..at].TrimEnd(',', ' ');
        }

        // The commonest join is "<Act>, 2017 and <Rules>, 2018" - cut after the first statute's year.
        if (StatuteJoin().Match(clean) is { Success: true } join)
            clean = clean[..(join.Index + join.Groups["year"].Length + join.Groups["lead"].Length)].TrimEnd(',', ' ');

        return Capitalised(clean);
    }

    /// <summary>
    /// Master data holds some names entirely in lower case - "amruta nangal", "adi sangli" - and they
    /// were reaching a management inbox that way. Only a label with NO capital in it is touched, and
    /// then only the first letter of each word: anything already carrying a capital is somebody's
    /// deliberate spelling and is left exactly as it is. That protects "API Unit-2", "BITA Consulting
    /// Assam" and every statute name, where blind title-casing would produce "Api" and turn the
    /// "of"/"and" inside "(Regulation of Employment and Conditions of Service)" into capitals.
    /// </summary>
    private static string Capitalised(string label)
    {
        if (label.Any(char.IsUpper) || !label.Any(char.IsLetter))
            return label;

        var words = label.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            // "of", "and", "the" stay lower case unless they open the name.
            if (words[i].Length == 0 || (i > 0 && SmallWords.Contains(words[i])))
                continue;

            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        }

        return string.Join(' ', words);
    }

    private static readonly HashSet<string> SmallWords =
        new(StringComparer.Ordinal) { "of", "and", "the", "at", "in", "on", "for", "to", "by", "with" };

    private static readonly string[] JoinedNames = [" & ", " read with ", " along with "];

    [GeneratedRegex(@"(?<lead>,\s*)(?<year>(19|20)\d{2})\s+and\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex StatuteJoin();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

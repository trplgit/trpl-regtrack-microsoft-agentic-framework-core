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
/// Makes each fact say WHICH UNIT it counts, before the model ever sees it.
///
/// <para>[FOUND LIVE on tenant 1082, 2026-09-22] The procs use three words for two units. Every
/// <c>od_*</c>, <c>lm_*</c>, <c>tm_*</c> and <c>rm_*</c> count comes from <c>#sched</c> - individual
/// due dates - and the labels call those "items" in some rows and "obligations" in others.
/// <c>obligations_in_scope</c> comes from <c>#inst</c>, the obligations themselves, and is called
/// "obligations" too. An obligation filed monthly produces twelve schedules, so
/// "2,852 obligations are overdue" sitting beside "3,660 obligations in your scope" are not
/// comparable numbers - and a reader cannot tell, because the words are the same.</para>
///
/// <para>The fix is not to map one word onto the other: that would make the two units MORE alike,
/// which is the false-equivalence CLAUDE.md Sec.4a exists to prevent. Each label is instead made to
/// name its own unit. Overridden here rather than in SQL because the procs are deployed.</para>
/// </summary>
file static partial class FactLabels
{
    /*  Schedule-level counts: one obligation can appear many times. "Item" is replaced because it
        names nothing a reader recognises - the user's own complaint was "if it says 13 items, tell
        me are they licence or compliance or what".                                               */
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        // The estate context fact - a DIFFERENT unit from everything overdue, and said so.
        ["obligations_in_scope"] = "separate obligations tracked in your scope, each counted once however often it falls due",
        ["locations_with_obligations"] = "of those locations have any obligation configured",

        // Schedule-level. Every one of these counts a due date, not an obligation.
        ["od_total"] = "obligations are currently overdue, whatever date they were originally due",
        ["od_liability"] = "of those overdue obligations carry personal criminal liability for the responsible officer",
        ["od_over_90_liability"] = "overdue obligations are more than 90 days late AND carry personal criminal liability",
        ["od_never_touched"] = "of those overdue obligations have no action recorded against them at all",
        ["od_no_owner"] = "of those overdue obligations have nobody assigned to do them",
        ["lm_open_liability"] = "of last month's still-open obligations carry personal criminal liability",
        ["lm_open_critical"] = "of last month's still-open obligations are rated critical",
        ["lm_open_never_touched"] = "of last month's still-open obligations have no action recorded at all",
        ["tm_open_liability"] = "of those past-due obligations carry personal criminal liability",

        /*  [FOUND LIVE on tenant 1082, 2026-09-22] The Licence email read "3 licences have expired
            so far in September" and then "5 licences are expired" - which looks like a
            contradiction and is not. The 3 expired DURING September; the 5 are every licence
            expired today whatever its date, so the 3 sit inside the 5.

            Neither label said so. "licences have expired so far this month" and "licences are
            expired today" are both true and both read as totals, and nothing in the wording tells
            the reader one is a subset of the other. The nesting is stated here rather than left
            to the ordering of the facts, because a reader who spots two totals that disagree
            stops trusting the rest of the email.                                               */
        /*  NOT "expired today". [FOUND LIVE on tenant 1082, 2026-09-22] "5 licences are expired
            today" reads as five that expired ON today's date; it means five that STAND expired as
            at today, most from earlier months. A stock figure has to be worded as a state the
            reader is in, never as an event that happened - "are currently expired", never
            "expired today". The same trap applies to any `stock` fact.                          */
        ["lic_expired_total"] = "licences are currently expired, counting every expiry date including earlier months",
        /*  Worded to stand ALONE: in the Overview this fact arrives without lic_expired_total, so
            an "of those..." opening would have nothing to refer back to.                        */
        ["lic_expired_unrenewed"] = "licences are currently expired with no renewal in progress",
        ["lic_lapsed_this_month"] = "licences expired during the current month - these are part of the expired total, not additional to it",
        ["lic_lapsed_last_month"] = "licences expired during last month - also part of the expired total, not additional to it",

        /*  [FOUND LIVE on tenant 1082, 2026-09-22] The Overview says "the standing backlog is 2,853
            overdue obligations" and reads clearly; Location, Users and Act say "419 of its 1,209
            overdue obligations" and leave the reader unable to tell whether that is this month's
            slippage or years of accumulation.

            The difference was the label. od_total (Overview only) had already been reworded; every
            OTHER slot reads its scope-wide total from t_od_total, whose label still said "are
            overdue today" - which also carries the event/state trap: it means overdue NOW, not
            overdue on today's date. Naming it the standing backlog fixes both at once.          */
        ["t_od_total"] = "obligations make up the standing backlog - overdue now, whatever date each was originally due",
        ["t_od_liability"] = "of that standing backlog carry personal criminal liability for the responsible officer",
        ["t_od_over_90_days"] = "of that standing backlog has been overdue for more than 90 days",
    };

    /// <summary>
    /// [MADE STRUCTURAL 2026-09-22] The override table above fixed "item" only where it had been
    /// SEEN to fail - the <c>od_*</c> and <c>lm_*</c> families - so the Act email went on saying
    /// "189 items" from its own <c>law_*</c> labels. Fixing a word where it was noticed is how it
    /// keeps coming back.
    ///
    /// <para>Every remaining "item" in any label means a scheduled obligation: the procs use the
    /// two words interchangeably for <c>#sched</c> rows. The one label counting a DIFFERENT unit,
    /// <c>obligations_in_scope</c>, is overridden above and says so in its own words, so this
    /// substitution cannot blur the distinction Sec.4a protects.</para>
    ///
    /// <para><c>NoLabelSentToTheModelSaysItem</c> fails the build if one ever slips back.</para>
    /// </summary>
    public static string For(string factKey, string label) =>
        ItemWord.Replace(
            Overrides.TryGetValue(factKey, out var better) ? better : label,
            m => m.Value.EndsWith('s') ? "obligations" : "obligation");

    /// <summary>The bare noun only - never "itemised", never inside a longer word.</summary>
    private static readonly Regex ItemWord = new(@"\bitems?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    /// <summary>
    /// Whether a percentage means anything on this denominator. Below the floor the model is sent
    /// the two counts and no percentage at all, so it cannot state one.
    ///
    /// <para>[FOUND LIVE on tenant 1082, 2026-09-22] "2 of 6 remain open: 66%, against 24% across
    /// the organisation" reads as a severe outlier. It is two things. A percentage on a base this
    /// small converts noise into apparent significance, and one more closure would move it 17
    /// points. The counts are the honest form, and at this size they are also the shorter one.</para>
    ///
    /// <para>[CHANGED 2026-09-22] Withholding the percentages outright went too far. It also removed
    /// <c>TenantPct</c> - the rate across the WHOLE scope, always computed on a large base and
    /// always sound - which left the reader doing the division themselves: "4 of the 6 open,
    /// compared with 47 of 194 across your organisation" makes them work out that one is 66% and
    /// the other 24%. Both values are now sent, with <c>SmallBase</c> marking the finding so the
    /// prompt can require the COUNT to lead and the percentage to appear only beside the scope's
    /// own rate, where it is a comparison rather than a number standing on its own.</para>
    /// </summary>
    private static bool RatioWorthStating(int? baseCount) => baseCount is >= MinBaseForPercentage;

    private const int MinBaseForPercentage = 20;

    /// <summary>
    /// Two findings are OFFERED; two are not always worth naming. The second is withheld when it is
    /// trivially small next to the first, so the email cannot spend a paragraph on it.
    ///
    /// <para>[FOUND LIVE on tenant 1082, 2026-09-22] The Users email named a person holding 579 of
    /// 2,852 overdue obligations, and then named a second person for "1 of that person's 1 open
    /// obligations" being self-reviewed. Beside the first, the second is noise - and giving it equal
    /// billing teaches the reader that the back half of these emails is filler. The prompt was asked
    /// to make this judgement and did not; a proportion decides it reliably.</para>
    ///
    /// <para>The test is RELATIVE, per CLAUDE.md Sec.4 - a finding covering 1 of 3 things is
    /// significant on a small estate and noise on a large one, so an absolute floor would be wrong.
    /// The headline finding is never dropped, however small: the data layer chose it.</para>
    /// </summary>
    private static List<MonthlyNamedFinding> DropTheSecondIfItIsNotWorthNaming(List<MonthlyNamedFinding> named)
    {
        if (named.Count < 2)
            return named;

        var first = named[0].Candidate.ItemCount;
        var second = named[1].Candidate.ItemCount;

        // Both must be countable to compare them; a finding with no ItemCount is judged on its own.
        if (first is not > 0 || second is not > 0)
            return named;

        return second.Value * 100 < first.Value * MinSecondFindingPercentOfFirst
            ? named.Take(1).ToList()
            : named;
    }

    /// <summary>The second finding must cover at least this share of the first to earn a paragraph.</summary>
    private const int MinSecondFindingPercentOfFirst = 5;

    /// <summary>
    /// How big the organisation is, so a figure can be judged against it.
    ///
    /// <para>[MEASURED 2026-09-22] The model was asked whether 2,852 overdue obligations is a lot
    /// while being told nothing about the size of the estate - the scope facts are tier-5 `ctx`
    /// volume rows and the tier filter stripped them from most slots. Without a denominator it
    /// cannot reason about proportion, so it padded with further counts instead, which is the
    /// "list of numbers" failure every rule in the shared prompt is written against.</para>
    ///
    /// <para>Roughly forty tokens, and unlike a rule it lets the model work the judgement out for
    /// itself. Only non-null members are serialised, so a slot without a count omits it.</para>
    /// </summary>
    private static object ScopeOf(IReadOnlyList<MonthlyFact> facts)
    {
        int? Value(string key) => facts.FirstOrDefault(f => f.FactKey == key)?.FactValue;

        return new
        {
            obligations_tracked = Value("obligations_in_scope"),
            locations = Value("locations_in_scope"),
            locations_with_obligations_configured = Value("locations_with_obligations"),
            acts_applying = Value("law_in_scope"),
            licences_tracked = Value("lic_total"),
            people_with_open_work = Value("u_people_with_open_work"),
        };
    }

    /// <summary>
    /// The shape of the month, as judgements rather than numbers - what a reader would conclude
    /// after looking at all the figures together.
    ///
    /// <para>[ADDED 2026-09-22] The most interesting thing about tenant 1082 is a CONTRAST the
    /// model could not see: 99% of what closed last month closed on time, while 87% of the standing
    /// backlog is over 90 days old. Those two facts together say the process works now and the
    /// legacy was never cleared - which is the story, and neither figure tells it alone. The model
    /// is forbidden from doing arithmetic, so it cannot derive this; it has to be handed it.</para>
    ///
    /// <para>These are LABELS, never new numbers. Nothing here is quotable, so nothing widens the
    /// closed set the validator checks against - the model uses a signal to choose what to lead
    /// with and which figures belong together, then states the figures it was already given.</para>
    /// </summary>
    private static object SignalsFrom(IReadOnlyList<MonthlyFact> facts)
    {
        int? Value(string key) => facts.FirstOrDefault(f => f.FactKey == key)?.FactValue;

        var overdue = Value("od_total");
        var over90 = Value("od_over_90_days");
        var onTimePct = Value("lm_on_time_pct");
        var liability = Value("od_liability");
        var noOwner = Value("od_no_owner");
        var neverTouched = Value("od_never_touched");

        string? Share(int? part, int? whole, string mostly, string some, string little) =>
            part is null || whole is not > 0 ? null
                : (part.Value * 100 / whole.Value) switch { >= 75 => mostly, >= 25 => some, _ => little };

        return new
        {
            backlog_age = Share(over90, overdue, "almost_all_older_than_90_days", "mixed_ages", "mostly_recent"),
            last_month_closing = onTimePct switch { null => null, >= 90 => "on_time_almost_always", >= 60 => "mixed", _ => "often_late" },
            liability_in_backlog = Share(liability, overdue, "most_of_it", "a_meaningful_share", "a_small_share"),
            ownership = noOwner is > 0 ? "some_overdue_work_has_nobody_assigned" : null,
            never_started = neverTouched is > 0 ? "some_overdue_work_was_never_started" : null,
        };
    }

    /// <summary>
    /// What the figures in this email CANNOT cover, where the gap is big enough to change how a
    /// count should be read.
    ///
    /// <para>[FOUND LIVE on tenant 1082, 2026-09-22] <c>licences_without_end_date</c> was 25 of 47
    /// licences - a licence with no end date cannot be assessed for lapse and is excluded from
    /// every licence fact. The Licence email said "3 licences are expired" against "22 licences
    /// tracked" with no hint that half the estate was unassessable. Every number was true and the
    /// claim was still incomplete.</para>
    ///
    /// <para>Only non-zero rows are sent, and only ones that bound a figure the email uses - a
    /// data-quality row at 0 is noise. The model is told what it does not know, which is context
    /// it has never had, rather than being given another rule about hedging.</para>
    /// </summary>
    private static IReadOnlyList<object> NotAssessable(IReadOnlyList<MonthlyDataQuality> dataQuality) =>
        dataQuality
            .Where(d => d.ItemCount > 0 && BoundsAFigure.Contains(d.Code))
            .Select(object (d) => new { d.Code, d.ItemCount, d.Detail })
            .ToList();

    /// <summary>
    /// Data-quality codes that LIMIT a figure the email states, as opposed to reporting on the
    /// pipeline's own health. Only these are worth the reader's attention.
    /// </summary>
    /*  [NARROWED 2026-09-22] This started with five codes and produced "This comparison is limited
        to authorised branches, with no category link on record" in a customer email - internal
        methodology, meaningless to a compliance head, and exactly the jargon shared Rule 9 bans.

        Only a code that EXCLUDES THINGS FROM A COUNT THE READER SEES belongs here. "25 of the 47
        licences have no end date, so they are not in the 3" changes how the 3 should be read.
        "Licences are scoped by branch rather than by category" does not - it describes how the
        query was built, which is our problem and never the reader's.                            */
    private static readonly HashSet<string> BoundsAFigure = new(StringComparer.Ordinal)
    {
        "licences_without_end_date",   // cannot be assessed for lapse, so excluded from every licence fact
    };

    /// <summary>
    /// The consequence sentences this email's own input supports - nothing else may be written.
    ///
    /// <para>[MOVED FROM THE PROMPT 2026-09-22] This was a three-row table in the shared rules,
    /// sent on every call with the conditions spelled out for the model to evaluate. It is a
    /// deterministic test over facts we already hold, so it is done here: the model receives the
    /// one or two sentences it may actually use, or an empty list. That removes a table and a
    /// paragraph of conditions from every call, and removes the judgement that produced the live
    /// defect where a merely-late person was described as having left the company.</para>
    /// </summary>
    private static IReadOnlyList<string> ConsequencesAvailable(
        IReadOnlyList<MonthlyFact> facts, IReadOnlyList<MonthlyNamedFinding> named)
    {
        var available = new List<string>();

        var hasLicence = facts.Any(f => f.FactKey.StartsWith("lic_", StringComparison.Ordinal) && f.FactValue > 0)
                         || named.Any(n => n.Candidate.Detector.StartsWith("licence", StringComparison.Ordinal));
        if (hasLicence)
            available.Add("Until a licence is renewed, there is no valid licence on record for that activity.");

        if (named.Any(n => n.Candidate.Detector == "deactivated_owner"))
            available.Add("The person they are assigned to can no longer act on them in RegTrack.");

        if (named.Any(n => n.Candidate.Detector == "single_point_of_failure"))
            available.Add("If that person is unavailable, no one else is assigned to that work in RegTrack.");

        return available;
    }

    /// <summary>
    /// What a detector actually found, in plain words - sent WITH the finding that uses it.
    ///
    /// <para>[MEASURED 2026-09-22] This was an 18-row table in the shared rules, sent on every call
    /// whatever the email contained. An email carries at most two findings, so sixteen of those
    /// rows were always waste - and the static prompt was 82% of the input while the tenant's own
    /// data was 18%. Moving the glossary here sends one or two rows instead of eighteen, and sends
    /// them attached to the thing they describe rather than in a lookup table the model has to
    /// resolve. Same information, targeted, and the shared rules get shorter.</para>
    /// </summary>
    private static string DetectorMeaning(string detector) => detector switch
    {
        "last_month_slippage" => "A higher share of this one's previous-month work is still open than across the whole scope.",
        "single_point_of_failure" => "Every open obligation here rests on one person; nobody else is assigned to any of it.",
        "overdue_concentration" => "This one holds a large share of everything that is overdue.",
        "chronic_backlog" => "Its overdue work has sat more than 90 days, at a higher rate than the rest of the scope.",
        "liability_share" => "More of its overdue work carries personal criminal liability than elsewhere in the scope.",
        "multi_location_pattern" => "This Act is overdue at many of the sites it applies to - a process problem, not one site's.",
        "category_overdue_skew" => "This category of obligation is overdue far more often than everything else.",
        "liability_overdue_location" => "This site is well above the scope's own rate on liability-bearing overdue work.",
        "deactivated_owner" => "Open work is held by someone who is no longer an active user of RegTrack.",
        "self_review" => "The same person performs the work and approves it.",
        "ghost_location" => "In scope, but with no obligations configured at all - it cannot be assessed.",
        "licence_expiring_unrenewed" => "This licence expires this month with no renewal filed.",
        "licence_lapsed_recent_unrenewed" => "This licence has expired and still has no renewal in progress.",
        "expired_unrenewed_location" => "Expired-and-unrenewed licences are concentrated at this one site.",
        "licence_type_lapse_rate" => "Licences of this type lapse without renewal more often than other types.",

        // A detector with no entry still reaches the model with its raw name; it is never hidden.
        _ => string.Empty,
    };

    public static FreeMonthlyDigestPrompt Build(MonthlyDigestData data)
    {
        var edition = data.Edition;

        var named = DropTheSecondIfItIsNotWorthNaming(
            data.Candidates
                .Where(c => c.DefaultSlot is 1 or 2)
                .OrderBy(c => c.DefaultSlot)
                .Select(c => new MonthlyNamedFinding(c.DefaultSlot!.Value, c))
                .ToList());

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

            /*  A number the LABEL states is the data's own, not the model's invention.

                [FOUND LIVE on tenant 1082, 2026-09-22] The label reads "laws are overdue at two or
                more locations at once". The model wrote "2 or more locations"; the repair converts
                spelled numbers to digits so the closed-set check can see them; 2 was in no fact,
                so a true sentence was deleted as a fabrication. Quoting the label back is the one
                thing we can always be sure of.                                                  */
            foreach (Match m in LabelNumber().Matches(fact.DisplayLabel))
                if (int.TryParse(m.Value, out var stated))
                    numbers.Add(stated);

            /*  ...including one the label SPELLS. [FOUND LIVE on tenant 1082, 2026-09-22] The label
                reads "overdue at two or more locations at once"; the model wrote "2 or more
                locations"; the repair turns spelled numbers into digits so they can be checked; 2
                was in no fact and the sentence was deleted as invention. The digits-only pass above
                could not see it, because the label never wrote a digit.                          */
            foreach (var (word, value) in SpelledInLabels)
                if (fact.DisplayLabel.Contains(word, StringComparison.OrdinalIgnoreCase))
                    numbers.Add(value);
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

            /*  THE LITERAL 1 IN "one of 19 Acts". [FOUND LIVE on tenant 1082, 2026-09-22] Shared
                Rule 7 asks for exactly this construction, and the Act email lost its residual count
                to it: the model wrote "1 of 19 Acts sharing this pattern", 19 was allowed, and the
                bare 1 was not - so a sentence the prompt had requested was trimmed as invention.

                A 1 beside a finding is grammatical, not quantitative: it says "this is one of
                them". It is allowed only where a finding exists to be one of.                   */
            numbers.Add(1);

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
            scope = ScopeOf(data.Facts),
            signals = SignalsFrom(data.Facts),
            not_assessable = NotAssessable(data.DataQuality),
            allowed_consequences = ConsequencesAvailable(data.Facts, named),
            facts = sentFacts.Select(f => new
            {
                f.FactKey,
                f.FactValue,
                DisplayLabel = FactLabels.For(f.FactKey, f.DisplayLabel),
                f.WindowScope,
                f.ImpactClass,

                /*  A FLAG APPEARS ONLY WHEN IT IS TRUE, and the shared rules say so. One fact in
                    twenty carries IsHeadline, so nineteen "IsHeadline":false pairs were shipping
                    on every call to say nothing - the same for Backlog and AsAtRequired on most
                    rows. Null is omitted by JsonOptions, so this drops them.                    */
                AsAtRequired = f.AsAtRequired ? true : (bool?)null,
                Backlog = IsBacklogFact(f) ? true : (bool?)null,
                IsHeadline = f.IsHeadline ? true : (bool?)null,
            }),
            named_findings = named.Select(n => new
            {
                n.Slot,
                Placeholder = n.NamePlaceholder,
                AtPlaceholder = n.AtPlaceholder,
                DatePlaceholder = n.DatePlaceholder,
                n.Candidate.Detector,
                Means = DetectorMeaning(n.Candidate.Detector),
                n.Candidate.EntityKind,
                n.Candidate.Metric,
                n.Candidate.ItemCount,
                n.Candidate.BaseCount,
                MetricPct = RatioWorthStating(n.Candidate.BaseCount) ? n.Candidate.MetricPct : null,
                TenantPct = RatioWorthStating(n.Candidate.BaseCount) ? n.Candidate.TenantPct : null,
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

        /*  [ADDED 2026-09-22] THE SIZE AND SHAPE OF THE ESTATE, in every slot.

            These are tier-5 `ctx` volume facts, so the tier filter stripped them from every email
            except the two that name them. The model was therefore asked to say whether 2,852
            overdue obligations is a lot - while being told nothing about how big the estate is.
            It cannot reason about proportion from a number with no denominator, so it padded with
            more counts instead, which is the "data slapping" this whole prompt set fights.

            Three facts, about forty tokens. Cheaper than any rule written to compensate for their
            absence, and unlike a rule they let the model work the proportion out for itself.    */
        "obligations_in_scope",      // how much there is to be overdue ON
        "locations_in_scope",        // how many sites the scope covers
        "locations_with_obligations",// and how many of those are actually configured
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
    /// <para>[RAISED TO 30/22 AND PUT BACK, 2026-09-22] Raising it was a fair experiment - the cap
    /// had been set when the model was given numbers and nothing to judge them by - and it did
    /// improve the writing. It also cost 75% more OUTPUT: Location's thinking went from 1,790 to
    /// 4,589 tokens to produce 291 tokens of email, because ten more facts is ten more things to
    /// weigh. Output bills several times higher than input, so it was the most expensive change of
    /// the day.</para>
    ///
    /// <para>What actually made the writing better was `signals` - the judgements handed over
    /// ready-made. With those in place the extra facts are paying for weighing that has already
    /// been done, so the cap goes back.</para>
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
        /*  [FOUND LIVE on tenant 1082, 2026-09-22] A joiner INSIDE a statute's own name is not a
            join between two statutes. "Sexual Harassment of Women at Workplace (Prevention,
            Prohibition & Redressal) Act, 2013 & ... Rules 2013" contains " & " twice: the second
            one separates the Act from its Rules, the first is part of the Act's title. Cutting at
            the first produced "Sexual Harassment of Women at Workplace (Prevention, Prohibition"
            in a customer email - truncated mid-title, with an unclosed bracket.

            A cut is only safe where the brackets before it are balanced, which is exactly the
            test for "am I between two names rather than inside one".                           */
        foreach (var joiner in JoinedNames)
        {
            for (var at = clean.IndexOf(joiner, StringComparison.OrdinalIgnoreCase);
                 at > 20;
                 at = clean.IndexOf(joiner, at + joiner.Length, StringComparison.OrdinalIgnoreCase))
            {
                if (!BracketsBalanced(clean[..at]))
                    continue;

                clean = clean[..at].TrimEnd(',', ' ');
                break;
            }
        }

        // The commonest join is "<Act>, 2017 and <Rules>, 2018" - cut after the first statute's year.
        if (StatuteJoin().Match(clean) is { Success: true } join && BracketsBalanced(clean[..join.Index]))
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

    /// <summary>True when every bracket opened has been closed - so a cut here is between names, not inside one.</summary>
    private static bool BracketsBalanced(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
        }

        return depth == 0;
    }

    /// <summary>Numbers the procs' labels write as words - the model may quote either form.</summary>
    private static readonly (string Word, int Value)[] SpelledInLabels =
    [
        ("two or more", 2), ("three or more", 3), ("one or more", 1),
    ];

    /// <summary>A small number a DisplayLabel states in its own words ("the 3 holding the most").</summary>
    [GeneratedRegex(@"\b\d{1,2}\b")]
    private static partial Regex LabelNumber();

    [GeneratedRegex(@"(?<lead>,\s*)(?<year>(19|20)\d{2})\s+and\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex StatuteJoin();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

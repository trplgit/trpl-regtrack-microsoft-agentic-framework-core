using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// One figure the model may write, as a token instead of a number.
/// </summary>
/// <param name="Token">What the model writes: <c>{{F:t_od_total}}</c>, <c>{{NAME_1:item}}</c>, <c>{{EG_2:base}}</c>, <c>{{Q:code}}</c>.</param>
/// <param name="Owner">For a finding or example field, the name placeholder it belongs to (<c>{{NAME_1}}</c>); empty for a fact.</param>
/// <param name="Field">fact | item | base | pct | tenant_pct | others | flagged | compared | quality.</param>
/// <param name="Unit">compliances | locations | acts | people | categories | licences | licencetypes | percent | "" (no unit check).</param>
/// <param name="Period">prev | sofar | rest | stock | ctx.</param>
/// <param name="Bases">Tokens this one may be "N of" - its whole.</param>
/// <param name="Defining">Words the token's sentence (or its paragraph before it) must carry, or null.</param>
/// <param name="Comparison">A pattern count, a finding's share or its "others" - the only tokens comparison words may describe.</param>
public sealed record FactToken(
    string Token, string Owner, string Field, int Value, bool IsPercent, string Unit, string Period,
    string? FactKey, string? Detector, IReadOnlySet<string> Bases, Regex? Defining, string? DefiningWhat, bool Comparison)
{
    /// <summary>What the reader sees: a count with thousands commas, a share with its percent sign.</summary>
    public string Rendered => IsPercent
        ? Value.ToString(CultureInfo.InvariantCulture) + "%"
        : Value.ToString("#,0", CultureInfo.InvariantCulture);
}

/// <summary>
/// [ADDED 2026-10-12] Fact-bound figures for the free-tier monthly email.
///
/// <para>The model never types a number. Every figure is written as a token naming the fact or
/// finding field it comes from, and code puts the number in. That closes the one gap the closed-set
/// check left open: a REAL number written under the WRONG description - live on prod tenant 1008,
/// "137 of the 210 Acts ... higher share" (137 was law_overdue_at_2plus_locs; the pattern was 59 of
/// 210), "21 of the 22 sites where it applies" (a slippage example counting compliances), an
/// all-months figure under a last-month frame, an open count described as "fell due".</para>
///
/// <para>Because every figure now names its source, the checks here are exact rather than
/// heuristic: each "X of Y" is checked against X's declared whole, the unit noun against the
/// token's unit, the period phrase against its window, and the words that define what it counts.
/// All checks are deterministic (CLAUDE.md non-negotiables 1 and 5) and run on the RAW draft;
/// <see cref="Bind"/> then turns the tokens into digits, so every existing digit-based repair and
/// validator step downstream runs unchanged.</para>
///
/// <para>The card lane and the deterministic fallback do not use tokens and are unaffected.</para>
/// </summary>
public static partial class FreeMonthlyFactTokens
{
    // ---------------------------------------------------------------------------------------------
    //  Registry - built once per prompt, from exactly what the user message shows the model
    // ---------------------------------------------------------------------------------------------

    internal static IReadOnlyDictionary<string, FactToken> Build(
        MonthlyDigestData data,
        IReadOnlyList<MonthlyFact> sentFacts,
        IReadOnlyList<MonthlyNamedFinding> named,
        IReadOnlyList<MonthlyBoundExample> examples,
        Func<int?, bool> ratioWorthStating,
        Func<string, bool> boundsAFigure)
    {
        var slot = data.Edition.Slot;
        var byKey = sentFacts.GroupBy(f => f.FactKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var tokens = new Dictionary<string, FactToken>(StringComparer.Ordinal);

        string FactTokenOf(string key) => "{{F:" + key + "}}";

        foreach (var f in sentFacts)
        {
            if (Forbidden(slot, f.FactKey))
                continue;

            var bases = BaseKeysOf(f.FactKey, sentFacts)
                .Where(byKey.ContainsKey)
                .Where(k => !Forbidden(slot, k))
                .Select(FactTokenOf)
                .ToHashSet(StringComparer.Ordinal);

            var isPattern = IsPatternKey(f.FactKey);
            tokens[FactTokenOf(f.FactKey)] = new FactToken(
                FactTokenOf(f.FactKey), string.Empty, "fact", f.FactValue,
                IsPercent: f.FactKey.EndsWith("_pct", StringComparison.Ordinal),
                Unit: UnitOfFact(f.FactKey, slot),
                Period: PeriodOfFact(f),
                FactKey: f.FactKey, Detector: null, Bases: bases,
                Defining: DefiningOfFact(f.FactKey, out var what), DefiningWhat: what,
                Comparison: isPattern);
        }

        foreach (var n in named)
        {
            if (n.NamePlaceholder is not { } owner)
                continue;

            var c = n.Candidate;
            var stem = owner[..^2];                                    // "{{NAME_1"
            string T(string field) => stem + ":" + field + "}}";
            var itemUnit = UnitOfDetectorItem(c.Detector);
            var memberUnit = UnitOfEntity(c.EntityKind);
            var period = PeriodOfDetector(c.Detector);
            var comparisonDetector = ComparisonDetectors.Contains(c.Detector);
            var definingItem = DefiningOfDetector(c.Detector, out var itemWhat);

            if (c.ItemCount is { } item)
                tokens[T("item")] = new FactToken(T("item"), owner, "item", item, false, itemUnit, period, null, c.Detector,
                    c.BaseCount is null ? Empty : new HashSet<string>(StringComparer.Ordinal) { T("base") },
                    definingItem, itemWhat, Comparison: false);
            if (c.BaseCount is { } baseCount)
                tokens[T("base")] = new FactToken(T("base"), owner, "base", baseCount, false, itemUnit, period, null, c.Detector,
                    Empty, null, null, Comparison: false);

            var ratio = ratioWorthStating(c.BaseCount);
            if (ratio && c.MetricPct is { } mp)
                tokens[T("pct")] = new FactToken(T("pct"), owner, "pct", mp, true, "percent", period, null, c.Detector,
                    Empty, definingItem, itemWhat, Comparison: c.TenantPct is not null && comparisonDetector);
            if (ratio && c.TenantPct is { } tp)
                tokens[T("tenant_pct")] = new FactToken(T("tenant_pct"), owner, "tenant_pct", tp, true, "percent", period, null, c.Detector,
                    Empty, null, null, Comparison: true);

            // A zero is never stated (shared rules Sec.8), so a zero count gets no token: an absent
            // `others` is what tells the model to write "no other site is in that situation".
            // Members already named in this email are not "others" (FreeMonthlyDigestPrompt.UnnamedOthers).
            var others = FreeMonthlyDigestPrompt.UnnamedOthers(c, named);
            if (others > 0)
                tokens[T("others")] = new FactToken(T("others"), owner, "others", others, false, memberUnit, period, null, c.Detector,
                    Empty, null, null, Comparison: true);
            if (c.ProblemCount > 0 && c.PopulationCount > 0)
            {
                tokens[T("flagged")] = new FactToken(T("flagged"), owner, "flagged", c.ProblemCount, false, memberUnit, period, null, c.Detector,
                    new HashSet<string>(StringComparer.Ordinal) { T("compared") }, null, null, Comparison: true);
                tokens[T("compared")] = new FactToken(T("compared"), owner, "compared", c.PopulationCount, false, memberUnit, period, null, c.Detector,
                    Empty, null, null, Comparison: false);
            }
        }

        foreach (var e in examples)
        {
            var owner = e.Placeholder;
            var stem = owner[..^2];
            string T(string field) => stem + ":" + field + "}}";
            var unit = UnitOfExample(e.Example.UnitLabel);
            var period = byKey.TryGetValue(e.Example.PatternFactKey, out var pf) ? PeriodOfFact(pf) : "stock";
            var defining = DefiningOfFact(e.Example.PatternFactKey, out var what);
            /*  [FOUND 2026-10-14, Minda Act - fell back] An example inherited its pattern's "must say a comparison"
                while ComparisonWithoutPattern forbids comparing an example: attempt 1 was rejected for leaving
                the comparison out, attempt 2 for putting it in. An example states only its own count (shared
                rules Sec.2), so it never carries the pattern's comparison requirement.                     */
            if (ReferenceEquals(defining, ComparisonWords))
                (defining, what) = (null, null);

            if (e.Example.ItemCount is { } item)
                tokens[T("item")] = new FactToken(T("item"), owner, "item", item, false, unit, period, e.Example.PatternFactKey, e.Example.Detector,
                    e.Example.BaseCount is null ? Empty : new HashSet<string>(StringComparer.Ordinal) { T("base") },
                    defining, what, Comparison: false);
            if (e.Example.BaseCount is { } baseCount)
                tokens[T("base")] = new FactToken(T("base"), owner, "base", baseCount, false, unit, period, e.Example.PatternFactKey, e.Example.Detector,
                    Empty, null, null, Comparison: false);
        }

        foreach (var dq in data.DataQuality.Where(d => d.ItemCount > 0 && boundsAFigure(d.Code)))
        {
            var t = "{{Q:" + dq.Code + "}}";
            tokens[t] = new FactToken(t, string.Empty, "quality", dq.ItemCount, false, "licences", "ctx", null, null, Empty, null, null, false);
        }

        return tokens;
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Facts a slot prompt forbids stating get no token, so they cannot be written at all.</summary>
    internal static bool Forbidden(MonthlyDigestSlot slot, string key) =>
        slot == MonthlyDigestSlot.Licence && key == "pat_expired_unrenewed_location";   // 06e: one plain location count only

    internal static bool IsPatternKey(string key) =>
        (key.StartsWith("pat_", StringComparison.Ordinal) || key.StartsWith("pattern_", StringComparison.Ordinal))
        && !key.EndsWith("_of", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------------
    //  Units, periods, wholes and defining words - from key families, detectors and labels
    // ---------------------------------------------------------------------------------------------

    internal static string UnitOfFact(string key, MonthlyDigestSlot slot)
    {
        if (key.EndsWith("_pct", StringComparison.Ordinal)) return "percent";
        if (key.StartsWith("pattern_categories", StringComparison.Ordinal)) return "categories";
        if (key.StartsWith("pattern_liability_locations", StringComparison.Ordinal)) return "locations";
        if (key.StartsWith("pat_licence_type_lapse_rate", StringComparison.Ordinal)) return "licencetypes";
        if (key.StartsWith("pat_expired_unrenewed_location", StringComparison.Ordinal)) return "locations";
        if (key.StartsWith("pat_", StringComparison.Ordinal) || key.StartsWith("pattern_", StringComparison.Ordinal))
            return slot switch
            {
                MonthlyDigestSlot.Users => "people",
                MonthlyDigestSlot.Act => "acts",
                _ => "locations",
            };
        if (key.StartsWith("loc_", StringComparison.Ordinal) || key.StartsWith("locations_", StringComparison.Ordinal)) return "locations";
        if (key.StartsWith("law_", StringComparison.Ordinal)) return "acts";
        if (key.StartsWith("u_people_", StringComparison.Ordinal) || key.StartsWith("u_inactive_people", StringComparison.Ordinal)) return "people";
        if (key.StartsWith("lic_", StringComparison.Ordinal) || key.StartsWith("licences_", StringComparison.Ordinal) || key == "untyped_licences") return "licences";
        if (key.StartsWith("lm_", StringComparison.Ordinal) || key.StartsWith("tm_", StringComparison.Ordinal) || key.StartsWith("rm_", StringComparison.Ordinal)
            || key.StartsWith("od_", StringComparison.Ordinal) || key.StartsWith("t_", StringComparison.Ordinal)
            || key.StartsWith("u_open_", StringComparison.Ordinal) || key.StartsWith("open_items_", StringComparison.Ordinal)
            || key == "obligations_in_scope") return "compliances";
        return string.Empty;
    }

    private static string UnitOfDetectorItem(string detector) => detector switch
    {
        "multi_location_pattern" => "locations",
        "licence_type_lapse_rate" or "expired_unrenewed_location" => "licences",
        "ghost_location" => string.Empty,
        _ => "compliances",
    };

    private static string UnitOfEntity(string kind) => kind.ToLowerInvariant() switch
    {
        "location" or "site" or "branch" => "locations",
        "person" or "user" => "people",
        "act" or "law" => "acts",
        "category" => "categories",
        "licence" or "license" => "licences",
        "licence_type" or "license_type" or "licencetype" => "licencetypes",
        _ => string.Empty,
    };

    private static string UnitOfExample(string unitLabel)
    {
        var l = unitLabel.ToLowerInvariant();
        if (Regex.IsMatch(l, @"\blocations\b") && !Regex.IsMatch(l, @"^(of the |of its )?(obligations|items|compliances)"))
            return l.Contains("licence") ? "licences" : "locations";
        if (l.Contains("licence") || l.Contains("license")) return "licences";
        return "compliances";
    }

    /// <summary>
    /// prev | sofar | rest | stock | ctx. [Architect 2026-10-12] "curr" splits in two: what has
    /// fallen due so far this month, and what still falls due before it ends - the shared 'context'
    /// section's t_rm_* and lic_expiring_rest_of_month are the latter (the tenant-1926 slip).
    /// </summary>
    internal static string PeriodOfFact(MonthlyFact f) => f.WindowScope switch
    {
        "prev" => "prev",
        "ctx" => "ctx",
        "curr" when f.FactKey.StartsWith("rm_", StringComparison.Ordinal) || f.FactKey.StartsWith("t_rm_", StringComparison.Ordinal)
                    || f.FactKey.StartsWith("lic_expiring", StringComparison.Ordinal) || f.Section == "rest_of_month" => "rest",
        "curr" => "sofar",
        _ => "stock",
    };

    private static string PeriodOfDetector(string detector) => detector == "last_month_slippage" ? "prev" : "stock";

    private static readonly HashSet<string> ComparisonDetectors = new(StringComparer.Ordinal)
    {
        "liability_share", "chronic_backlog", "liability_overdue_location", "category_overdue_skew",
        "last_month_slippage", "multi_location_pattern", "expired_unrenewed_location", "licence_type_lapse_rate",
    };

    /// <summary>
    /// The facts a fact may be "N of". [Architect 2026-10-12] Explicit, one key at a time - never
    /// "any fact with the same value": equal values are exactly how the live slips got through.
    /// obligations_in_scope (instances) is never the whole of a schedule-level count (CLAUDE.md Sec.4a).
    /// </summary>
    internal static IEnumerable<string> BaseKeysOf(string key, IReadOnlyList<MonthlyFact> facts)
    {
        if (FreeMonthlyDigestPrompt.FactParent.TryGetValue(key, out var parent))
            yield return parent;

        if (ExtraBases.TryGetValue(key, out var extras))
            foreach (var e in extras)
                yield return e;

        if (IsPatternKey(key))
            yield return key + "_of";

        if (key.EndsWith("_pct", StringComparison.Ordinal))
        {
            // A share is "of all" the overdue total, never of a member count.
            if (key.Contains("top3", StringComparison.Ordinal))
            {
                yield return "t_od_total";
                yield return "od_total";
            }
            yield break;
        }

        // Member counts: any member count is "N of" the members in scope, or of those with overdue work.
        if (key.StartsWith("loc_", StringComparison.Ordinal) && key is not ("loc_in_scope"))
        {
            yield return "loc_in_scope";
            if (key != "loc_with_overdue") yield return "loc_with_overdue";
            if (key != "loc_with_obligations") yield return "loc_with_obligations";
        }
        if (key.StartsWith("law_", StringComparison.Ordinal) && key != "law_in_scope")
        {
            yield return "law_in_scope";
            if (key != "law_with_overdue") yield return "law_with_overdue";
        }
        if (key.StartsWith("u_people_", StringComparison.Ordinal) && key != "u_people_with_open_work")
            yield return "u_people_with_open_work";
        if (key.StartsWith("u_open_", StringComparison.Ordinal) && key != "u_open_items")
            yield return "u_open_items";
        if (key == "locations_with_obligations")
            yield return "locations_in_scope";

        // The section fallback ParentOf already uses, for "of those" facts the map does not list.
        if (parent is null)
        {
            var me = facts.FirstOrDefault(f => f.FactKey == key);
            if (me is not null && me.DisplayLabel.StartsWith("of those", StringComparison.OrdinalIgnoreCase)
                && FreeMonthlyDigestPrompt.ParentOf(me, facts) is { } p)
                yield return p.FactKey;
        }
    }

    private static readonly Dictionary<string, string[]> ExtraBases = new(StringComparer.Ordinal)
    {
        ["lm_open_liability"] = ["lm_still_open"],
        ["lm_open_critical"] = ["lm_still_open"],
        ["lm_open_never_touched"] = ["lm_still_open"],
        ["od_liability"] = ["od_total"],
        ["od_over_90_liability"] = ["od_over_90_days", "od_total"],
        ["od_never_touched"] = ["od_total"],
        ["od_no_owner"] = ["od_total"],
        ["t_od_liability"] = ["t_od_total"],
        ["lic_expired_total"] = ["lic_total"],
        ["lic_expired_unrenewed"] = ["lic_expired_total", "lic_total"],
        ["lic_expiring_rest_of_month"] = ["lic_total"],
        ["lic_lapsed_last_month"] = ["lic_total"],
        ["lic_lapsed_this_month"] = ["lic_total"],
        ["loc_with_expired_unrenewed"] = ["loc_with_licences", "pat_expired_unrenewed_location_of"],
        ["u_open_self_reviewed"] = ["u_open_items"],
    };

    private static readonly Regex Liability = new(@"liabil", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Over90 = new(@"\b90 days\b|\bninety days\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Critical = new(@"\bcritical\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SelfReview = new(@"\breview|\bsame person\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OnePerson = new(@"\b(one|single|a single) (person|individual|user|performer)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoRenewal = new(@"\bno renewal\b|\bnot (yet )?(been )?renewed\b|\bwithout (a )?renewal\b|\bunrenewed\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TwoOrMore = new(@"\b(2|two) or more\b|\bmultiple\b|\bmore than one\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Inactive = new(@"\bno longer active\b|\binactive\b|\bdeactivated\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoOwner = new(@"\bno (one|owner|performer)\b|\bunassigned\b|\bnobody\b|\bnot assigned\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NeverStarted = new(@"\bnot (yet )?(been )?started\b|\bnever\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NothingConfigured = new(@"\bno compliances?\b|\bnothing (is )?(configured|mapped|set up)\b|\bnot (been )?set up\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static Regex? DefiningOfFact(string key, out string? what)
    {
        (what, var re) = key switch
        {
            _ when key.Contains("unrenewed", StringComparison.Ordinal) => ("'no renewal in progress'", NoRenewal),
            _ when key.Contains("liability", StringComparison.Ordinal) => ("'personal liability'", Liability),
            _ when key.Contains("over_90", StringComparison.Ordinal) => ("'more than 90 days'", Over90),
            _ when key.Contains("critical", StringComparison.Ordinal) => ("'rated critical'", Critical),
            _ when key.Contains("self_review", StringComparison.Ordinal) => ("'the same person as performer and reviewer'", SelfReview),
            _ when key.Contains("single_performer", StringComparison.Ordinal) || key.Contains("single_point_of_failure", StringComparison.Ordinal)
                => ("'rests with one person'", OnePerson),
            _ when key == "law_overdue_at_2plus_locs" => ("'at 2 or more locations'", TwoOrMore),
            _ when key.Contains("inactive", StringComparison.Ordinal) || key.Contains("deactivated", StringComparison.Ordinal) => ("'no longer active'", Inactive),
            _ when key.Contains("no_owner", StringComparison.Ordinal) => ("'no one assigned'", NoOwner),
            _ when key.Contains("never_touched", StringComparison.Ordinal) => ("'not started'", NeverStarted),
            _ when key.Contains("ghost_location", StringComparison.Ordinal) || key.Contains("without_obligations", StringComparison.Ordinal)
                => ("'has no compliance mapped to it'", NothingConfigured),
            _ when IsPatternKey(key) => ("a comparison such as 'a higher share than the other ...' or 'above your company average'", ComparisonWords),
            _ => ((string?)null, (Regex?)null),
        };
        return re;
    }

    private static Regex? DefiningOfDetector(string detector, out string? what)
    {
        (what, var re) = detector switch
        {
            "liability_share" or "liability_overdue_location" => ("'personal liability'", Liability),
            "chronic_backlog" => ("'more than 90 days'", Over90),
            "single_point_of_failure" => ("'rests with one person'", OnePerson),
            "self_review" => ("'the same person as performer and reviewer'", SelfReview),
            "deactivated_owner" => ("'no longer active'", Inactive),
            "expired_unrenewed_location" or "licence_type_lapse_rate" or "licence_lapsed_recent_unrenewed" or "licence_expiring_unrenewed"
                => ("'no renewal in progress'", NoRenewal),
            _ => ((string?)null, (Regex?)null),
        };
        return re;
    }

    // ---------------------------------------------------------------------------------------------
    //  Binding
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every token replaced by its value. A stray "%" or "per cent" after a share token is dropped (the token already carries it).</summary>
    public static string Bind(string draft, FreeMonthlyDigestPrompt prompt)
    {
        if (prompt.FactTokens.Count == 0)
            return draft;

        var bound = TokenPattern().Replace(draft, m =>
            prompt.FactTokens.TryGetValue(m.Groups["t"].Value, out var t) ? t.Rendered : m.Value);
        return StrayPercent().Replace(bound, "$1");
    }

    // ---------------------------------------------------------------------------------------------
    //  Checks - on the raw, tokenised draft
    // ---------------------------------------------------------------------------------------------

    /// <summary>Everything wrong with how the draft writes its figures. Empty means bind and continue.</summary>
    public static IReadOnlyList<string> Problems(string draft, FreeMonthlyDigestPrompt prompt) => Check(draft, prompt, literalChecks: true);

    /// <summary>
    /// [OWNER, 2026-10-15, tenant 1708] "In September, 149 compliances from September are still open." - the
    /// opener names the month and so does the sentence. Rejecting it made the Users email fall back twice
    /// (the slot prompts ask for "people with work due in {{PREV_MONTH}}" under an "In {{PREV_MONTH}}"
    /// paragraph), so the redundant opener is dropped instead: the sentence still names its period.
    /// Runs on the raw draft, before every check.
    /// </summary>
    public static string WithoutRedundantMonthOpener(string draft) =>
        RedundantMonthOpener().Replace(draft, m => m.Groups["b"].Value + char.ToUpperInvariant(m.Groups["first"].Value[0]) + m.Groups["first"].Value[1..]);

    [GeneratedRegex(@"(?<=^|[.!?]\s+|\n)(?<b>\*\*)?(?:In \{\{PREV_MONTH\}\}|So far in \{\{CURR_MONTH\}\}),\s+(?<first>\S+)(?=[^.!?\n]*\{\{(?:PREV|CURR)_MONTH\}\})")]
    private static partial Regex RedundantMonthOpener();

    /// <summary>Any figure token still in text meant for the reader - the fail-closed check on the final body.</summary>
    public static IReadOnlyList<string> Unbound(string finalText) =>
        TokenPattern().Matches(finalText).Select(m => m.Value).Distinct()
            .Select(t => $"leaves {t} unbound in the email - write only tokens given in the input")
            .ToList();

    private static IReadOnlyList<string> Check(string draft, FreeMonthlyDigestPrompt prompt, bool literalChecks)
    {
        var problems = new List<string>();
        if (prompt.FactTokens.Count == 0)
            return problems;

        var tokens = prompt.FactTokens;
        var text = draft.Replace("\r\n", "\n");

        // Unknown tokens, and tokens that look like ours but are malformed.
        foreach (Match m in TokenPattern().Matches(text))
            if (!tokens.ContainsKey(m.Groups["t"].Value))
                problems.Add($"writes {m.Value}, which is not one of the figures in the input - use only the tokens listed in the input, exactly as written");

        if (literalChecks)
        {
            problems.AddRange(LiteralFigures(text, prompt));
            problems.AddRange(NumberWords(text));
            problems.AddRange(Wording(text));
        }

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var paragraph in paragraphs)
        {
            var sentences = SentenceSplit().Split(paragraph.Trim()).Where(s => s.Trim().Length > 0).ToList();
            var paragraphOpener = sentences.Select(s => OpenerOf(s)).FirstOrDefault(o => o is not null);
            var tokensSoFar = new List<FactToken>();

            for (var i = 0; i < sentences.Count; i++)
            {
                var sentence = sentences[i];
                var plain = sentence.Replace("**", string.Empty);
                var found = TokenPattern().Matches(plain).Cast<Match>()
                    .Select(m => (Match: m, Token: tokens.GetValueOrDefault(m.Groups["t"].Value)))
                    .Where(x => x.Token is not null)
                    .Select(x => (x.Match, Token: x.Token!))
                    .ToList();

                if (found.Count == 0)
                    continue;

                var earlierInParagraph = string.Join(" ", sentences.Take(i)).Replace("**", string.Empty);

                foreach (var (m, t) in found)
                {
                    problems.AddRange(PartOfWhole(plain, m, t, tokens, tokensSoFar, found));
                    problems.AddRange(UnitOf(plain, m, t, tokens));
                    problems.AddRange(PeriodOf(plain, m, t, paragraphOpener));
                    problems.AddRange(DefiningWords(plain, earlierInParagraph, t));
                    problems.AddRange(Owner(plain, t));
                    problems.AddRange(AllOrBoth(plain, m, t, tokens));
                    problems.AddRange(BaseNotSaid(plain, t, tokensSoFar));
                }

                problems.AddRange(ComparisonWithoutPattern(plain, found.Select(f => f.Token).ToList()));
                problems.AddRange(ShareDirection(plain, found.Select(f => f.Token).ToList()));
                problems.AddRange(DueWords(plain, found));
                tokensSoFar.AddRange(found.Select(f => f.Token));
            }
        }

        return problems.Distinct().ToList();
    }

    // -- wording - [FOUND in the 2026-10-13 Minda Overview, old vs new] -----------------------------

    /// <summary>
    /// A part stated after its whole, in the same paragraph, must say it is a part: "561 of those
    /// 2,047" / "Of those, 561" - never a bare "561 are still open", which reads as a new total.
    /// </summary>
    private static IEnumerable<string> BaseNotSaid(string sentence, FactToken t, List<FactToken> tokensSoFar)
    {
        if (t.IsPercent || t.Bases.Count == 0)
            yield break;
        var whole = tokensSoFar.FirstOrDefault(p => t.Bases.Contains(p.Token));
        if (whole is null || Regex.IsMatch(TokenPattern().Replace(sentence, " "), @"\b(?:of|out of|among)\b", RegexOptions.IgnoreCase))
            yield break;
        yield return $"states {t.Token} without its base - it is part of {whole.Token} said just before, so write \"{t.Token} of those {whole.Token}\"";
    }

    /// <summary>
    /// The input's labels say what a figure counts; they are not wording. Copied into the email they
    /// read as database text ("whatever their due date", "between the 1st of this month and today")
    /// and repeat the period the opener already gave ("In September, 2,047 compliances fell due last month").
    /// </summary>
    private static readonly (Regex Pattern, string Say)[] LabelPhrases =
    [
        (new(@"\bwhatever (?:their|its|the)? ?(?:original )?(?:due )?date", RegexOptions.IgnoreCase), "leave it out - 'overdue' already says it"),
        (new(@"\bbetween the 1st of this month and today\b", RegexOptions.IgnoreCase), "'So far in {{CURR_MONTH}}' at the start already says it"),
        (new(@"\bbetween today and the end of the month\b", RegexOptions.IgnoreCase), "'Before the end of {{CURR_MONTH}}' at the start already says it"),
        (new(@"\brunning well above\b|\byour own average\b", RegexOptions.IgnoreCase), "'higher than your company average'"),
        (new(@"\bin your scope\b|\btracked in your scope\b", RegexOptions.IgnoreCase), "'across your organisation'"),
        (new(@"\bthe population\b|\bwere compared\b", RegexOptions.IgnoreCase), "the reader's words from the shared rules"),
        // [OWNER, 2026-10-15, tenant 1708] "The group includes 1 other people." - which group? Say what the others share.
        (new(@"\b(?:the|this|that) group\b", RegexOptions.IgnoreCase), "what the others share, in the finding's own words (\"N other people also hold a large share of all overdue compliances\")"),
    ];

    private static IEnumerable<string> Wording(string text)
    {
        foreach (var (pattern, say) in LabelPhrases)
            foreach (Match m in pattern.Matches(text))
                yield return $"copies the input's label wording '{m.Value}' - write {say}";

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

        // The introduction is fixed wording (shared rules Sec.4) - anything else is deleted by repair, leaving a bare date line.
        var intro = paragraphs.Skip(1).FirstOrDefault();
        if (intro is not null && !intro.StartsWith("Here is your monthly update on ", StringComparison.Ordinal))
            yield return "the introduction does not start 'Here is your monthly update on ...' - write it exactly as the shared rules give it";

        /*  [OWNER, 2026-10-14] "the sentences are very repetitive, like personal liability" - the most serious
            fact in the email loses its weight when every paragraph says it. At most three paragraphs carry it
            (the closing line's "personally liable" is not counted).                                       */
        var liabilityParagraphs = paragraphs.Count(p => Regex.IsMatch(p, @"\bpersonal (?:criminal )?liability\b", RegexOptions.IgnoreCase));
        if (liabilityParagraphs > 3)
            yield return $"mentions personal liability in {liabilityParagraphs} paragraphs - keep it to at most three (the headline, the all-months total and a named finding about it) and give the other paragraphs their own angle";

        /*  [OWNER, 2026-10-15, Minda Act] "The other 32 Acts are in the same situation." - a count of others
            must say what they share, in the finding's own words ("also hold a large share of all overdue
            compliances"); "the same situation" leaves the reader to guess which one.                     */
        foreach (Match m in Regex.Matches(text, @"\{\{(?:NAME|EG)_\d+:others\}\}[^.]*\b(?:in|share) (?:the same|this|that|a similar) (?:situation|position)", RegexOptions.IgnoreCase))
            yield return $"writes \"{m.Value}\" - say what the others share, in the finding's own words (for example \"also hold a large share of all overdue compliances\"), never \"the same situation\"";

        // A closing line said twice reads as boilerplate - each paragraph has its own meaning.
        var repeatedLine = paragraphs
            .SelectMany(p => SentenceSplit().Split(p))
            .Select(s => s.Replace("**", "").Trim())
            .Where(s => s.Length > 20 && !TokenPattern().IsMatch(s) && !s.Contains("{{AS_AT}}", StringComparison.Ordinal)
                        && !s.Contains("RegInsights Ultimate", StringComparison.Ordinal))   // the pointer may appear twice (Sec.7)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (repeatedLine is not null)
            yield return $"uses the same closing line twice (\"{repeatedLine.Key}\") - each paragraph ends with the line for its own subject";

        foreach (var paragraph in paragraphs)
        {
            foreach (var sentence in SentenceSplit().Split(paragraph))
            {
                // The period is said once, by the opener - never again in the same sentence.
                if (Regex.IsMatch(sentence, @"\{\{PREV_MONTH\}\}") && Regex.IsMatch(sentence, @"\blast month\b", RegexOptions.IgnoreCase))
                    yield return "says the period twice in one sentence ('In {{PREV_MONTH}}' and 'last month') - say it once";
                if (Regex.IsMatch(sentence, @"So far in \{\{CURR_MONTH\}\}") && Regex.IsMatch(sentence, @"\bthis month\b", RegexOptions.IgnoreCase))
                    yield return "says the period twice in one sentence ('So far in {{CURR_MONTH}}' and 'this month') - say it once";
            }

            /*  The same run of seven words twice in a paragraph is repetition the reader pays for ("a higher share
                than in other categories" after every name). [2026-10-14] Seven, not four: four words rejected
                ordinary English ("compliances are still open" twice) and two named findings written in the same
                shape, which the prompts ask for - Users, Location and Act all fell back on it. Each token or
                placeholder becomes a word of its own, so two sentences about different figures never match, and
                the RegInsights Ultimate pointer (allowed twice, Sec.7) is left out.                          */
            var counter = 0;
            var marked = Regex.Replace(paragraph.Replace("**", ""), @"\{\{[^}]+\}\}", _ => " tok" + counter++ + " ");
            var withoutPointer = string.Join(" ", SentenceSplit().Split(marked).Where(s => !s.Contains("RegInsights Ultimate", StringComparison.Ordinal)));
            var words = Regex.Matches(withoutPointer, @"[A-Za-z0-9']+").Select(w => w.Value.ToLowerInvariant()).ToList();
            var repeated = Enumerable.Range(0, Math.Max(0, words.Count - 6))
                .Select(i => string.Join(" ", words.Skip(i).Take(7)))
                .GroupBy(g => g)
                .Where(g => g.Count() >= 2)
                .Select(g => Regex.Replace(g.Key, @"\btok\d+\b", "N"))
                .FirstOrDefault();
            if (repeated is not null)
                yield return $"repeats '{repeated}' within one paragraph - say a shared description once for the whole list, e.g. \"A, B and C have a higher overdue share than your other categories\"";
        }
    }

    // -- X of Y ----------------------------------------------------------------------------------

    private static IEnumerable<string> PartOfWhole(
        string sentence, Match m, FactToken x, IReadOnlyDictionary<string, FactToken> tokens,
        IReadOnlyList<FactToken> earlier, IReadOnlyList<(Match Match, FactToken Token)> inSentence)
    {
        if (x.IsPercent && x.FactKey?.Contains("top3", StringComparison.Ordinal) != true)
            yield break;

        // "X of [the|its|...] Y", "X out of Y", "X among the Y"
        var after = sentence[(m.Index + m.Length)..];
        var direct = OfToken().Match(after);
        if (direct.Success && tokens.TryGetValue(direct.Groups["t"].Value, out var y))
        {
            if (!Acceptable(x, y))
                yield return WrongWhole(x, y);
            yield break;
        }

        /*  "X of those / of these / of them" -> the NEAREST earlier count must be X's whole, because that is
            the figure the reader takes "those" to mean. [FOUND 2026-10-14, Minda Overview] "9,475 of those
            have been overdue for more than 90 days. 3,937 of those carry personal liability." - 3,937 is part
            of the 11,945 total, not of the 9,475; any-of-the-last-three let it through.               */
        if (OfThose().IsMatch(after))
        {
            var before = inSentence.TakeWhile(f => f.Match.Index < m.Index).Select(f => f.Token).Reverse()
                .Concat(earlier.AsEnumerable().Reverse()).Where(t => !t.IsPercent).ToList();
            if (before.Count > 0 && x.Bases.Count > 0 && !Acceptable(x, before[0]))
                yield return $"writes {x.Token} \"of those\" right after {before[0].Token}, but {x.Token} is part of {string.Join(" or ", x.Bases)}, not of {before[0].Token} - write \"{x.Token} of the {x.Bases.First()}\"";
            yield break;
        }

        // Inverted: "Of [the] Y [words], X ..." - X sits in a clause opened by "Of Y".
        var inverted = InvertedOf().Match(sentence);
        if (inverted.Success && inverted.Groups["x"].Index == m.Index
            && tokens.TryGetValue(inverted.Groups["y"].Value, out var iy) && !Acceptable(x, iy))
            yield return WrongWhole(x, iy);
    }

    private static bool Acceptable(FactToken x, FactToken y) =>
        x.Bases.Contains(y.Token)
        // A pattern's "of M" may also be written with its members-in-scope count.
        || (x.Owner.Length == 0 && x.FactKey is { } k && IsPatternKey(k) && y.FactKey is { } yk && yk == k + "_of");

    private static string WrongWhole(FactToken x, FactToken y)
    {
        var expected = x.Bases.Count == 0 ? "it is not a part of another figure - state it on its own" : "its whole is " + string.Join(" or ", x.Bases);
        return $"writes {x.Token} as part of {y.Token}, but {y.Token} is not what {x.Token} is counted against - {expected}";
    }

    // -- unit ------------------------------------------------------------------------------------

    private static IEnumerable<string> UnitOf(string sentence, Match m, FactToken t, IReadOnlyDictionary<string, FactToken> tokens)
    {
        if (t.IsPercent || t.Unit.Length == 0)
            yield break;

        var after = sentence[(m.Index + m.Length)..];
        // Skip "of [the|its|...] Y" - the noun after Y describes both.
        var of = OfToken().Match(after);
        if (of.Success)
            after = after[(of.Index + of.Length)..];

        var window = TokenPattern().Split(after)[0];           // stop at the next token
        var words = WordPattern().Matches(window).Cast<Match>().Select(w => w.Value).Take(7).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (StopWords.Contains(w.ToLowerInvariant()))
                yield break;                                     // reached the verb - no unit noun named
            var next = i + 1 < words.Count ? words[i + 1].ToLowerInvariant() : string.Empty;
            var unit = NounUnit(w, next);
            if (unit is null)
                continue;
            if (unit != t.Unit && !(unit == "licences" && t.Unit == "licencetypes"))
                yield return $"writes {t.Token} as {w} - it counts {UnitWord(t.Unit)}, not {UnitWord(unit)}";
            yield break;
        }
    }

    /// <summary>The unit a noun names. A noun used as a modifier ("compliance categories", "licence types") names the next one.</summary>
    private static string? NounUnit(string word, string next)
    {
        var w = word.ToLowerInvariant();
        if (next.StartsWith("categor", StringComparison.Ordinal) && w is "compliance" or "compliances")
            return null;
        if (next.StartsWith("type", StringComparison.Ordinal) && w is "licence" or "license")
            return "licencetypes";
        return w switch
        {
            "compliance" or "compliances" or "obligation" or "obligations" => "compliances",
            "location" or "locations" or "site" or "sites" or "branch" or "branches" => "locations",
            "act" or "acts" or "law" or "laws" => "acts",
            "people" or "person" or "persons" or "user" or "users" => "people",
            "category" or "categories" => "categories",
            "licence" or "licences" or "license" or "licenses" => "licences",
            "type" or "types" => null,
            _ => null,
        };
    }

    private static string UnitWord(string unit) => unit switch
    {
        "licencetypes" => "licence types",
        "acts" => "Acts",
        _ => unit,
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "are", "is", "was", "were", "have", "has", "had", "carry", "carries", "remain", "remains", "fell", "fall", "falls",
        "hold", "holds", "held", "sit", "sits", "account", "accounts", "left", "reached", "reach", "reaches", "expire",
        "expires", "expired", "rest", "rests", "include", "includes", "including", "or", "compared", "than", "and", "with",
    };

    // -- period ----------------------------------------------------------------------------------

    private static IEnumerable<string> PeriodOf(string sentence, Match m, FactToken t, string? paragraphOpener)
    {
        if (t.Period == "ctx")
            yield break;

        var before = OpenerMatches(sentence).Where(o => o.Index < m.Index).Select(o => o.Period).LastOrDefault();
        var afterIt = OpenerMatches(sentence).Where(o => o.Index > m.Index).Select(o => o.Period).FirstOrDefault();
        var period = before ?? afterIt ?? paragraphOpener ?? (StockKeywords().IsMatch(sentence) ? "stock" : null);

        if (period is null)
        {
            if (t.Period is "prev" or "sofar" or "rest")
                yield return $"states {t.Token} without saying which period it is from - it is {PeriodWords(t.Period)}";
            yield break;
        }

        if (period != t.Period)
            yield return $"puts {t.Token} under {PeriodWords(period)}, but it is {PeriodWords(t.Period)}";
    }

    private static string? OpenerOf(string sentence) =>
        OpenerMatches(sentence.Replace("**", string.Empty)).Select(o => o.Period).FirstOrDefault();

    private static IEnumerable<(int Index, string Period)> OpenerMatches(string sentence)
    {
        foreach (Match o in OpenerPattern().Matches(sentence))
        {
            var period = o.Groups["prev"].Success ? "prev"
                : o.Groups["sofar"].Success ? "sofar"
                : o.Groups["rest"].Success ? "rest"
                : "stock";
            yield return (o.Index, period);
        }
    }

    private static string PeriodWords(string period) => period switch
    {
        "prev" => "last month (\"In {{PREV_MONTH}}\")",
        "sofar" => "this month so far (\"So far in {{CURR_MONTH}}\")",
        "rest" => "still to fall due (\"Before the end of {{CURR_MONTH}}\")",
        _ => "all months (\"In total, from all months\" / \"Currently\")",
    };

    // -- defining words --------------------------------------------------------------------------

    private static IEnumerable<string> DefiningWords(string sentence, string earlierInParagraph, FactToken t)
    {
        if (t.Defining is not { } re)
            yield break;
        // A token's own key ("{{F:t_od_liability}}") must never count as the words that define it.
        if (re.IsMatch(TokenPattern().Replace(sentence, " ")) || re.IsMatch(TokenPattern().Replace(earlierInParagraph, " ")))
            yield break;
        yield return $"states {t.Token} without saying what it counts - its sentence must say {t.DefiningWhat}";
    }

    private static IEnumerable<string> ComparisonWithoutPattern(string sentence, IReadOnlyList<FactToken> found)
    {
        var withoutNinety = MoreThanNinety().Replace(sentence, " ");
        if (!ComparisonWords.IsMatch(withoutNinety))
            yield break;
        if (found.Any(t => t.Comparison || t.IsPercent))
            yield break;
        var counts = found.Where(t => !t.IsPercent).Select(t => t.Token).ToList();
        if (counts.Count > 0)
            yield return $"describes {string.Join(", ", counts)} with a comparison ('higher', 'above average', 'than the rest'), but none of them is a comparison - only a pattern count or a finding's share may be described that way";
    }

    private static IEnumerable<string> ShareDirection(string sentence, IReadOnlyList<FactToken> found)
    {
        foreach (var pct in found.Where(t => t.Field == "pct"))
        {
            var tenant = found.FirstOrDefault(t => t.Field == "tenant_pct" && t.Owner == pct.Owner);
            if (tenant is null)
                continue;
            if (pct.Value < tenant.Value && Higher().IsMatch(sentence))
                yield return $"says {pct.Token} is higher than {tenant.Token}, but it is lower";
            if (pct.Value > tenant.Value && Lower().IsMatch(sentence))
                yield return $"says {pct.Token} is lower than {tenant.Token}, but it is higher";
        }

        foreach (var tenant in found.Where(t => t.Field == "tenant_pct"))
        {
            var idx = sentence.IndexOf(tenant.Token, StringComparison.Ordinal);
            var lead = sentence[Math.Max(0, idx - 70)..Math.Max(0, idx)];
            if (!TenantLead().IsMatch(lead))
                yield return $"states {tenant.Token} without saying it is the rate across your organisation - write it as \"compared with {tenant.Token} across your organisation\"";
        }
    }

    /// <summary>"N compliances fell due" may only say that of a *_due figure (or a slippage example's whole).</summary>
    private static IEnumerable<string> DueWords(string sentence, IReadOnlyList<(Match Match, FactToken Token)> found)
    {
        foreach (Match d in DuePhrase().Matches(sentence))
        {
            var subject = found.Where(f => f.Match.Index < d.Index).Select(f => f.Token).LastOrDefault(t => !t.IsPercent);
            if (subject is null)
                continue;
            var isDue = subject.FactKey?.Contains("_due", StringComparison.Ordinal) == true
                        || (subject.Field == "base" && subject.Period == "prev");
            if (!isDue && subject.Field == "fact")
                yield return $"says {subject.Token} {d.Value.Trim()}, but {subject.Token} counts work still open or overdue, not work that fell due";
        }
    }

    // -- owner / all-both ------------------------------------------------------------------------

    private static IEnumerable<string> Owner(string sentence, FactToken t)
    {
        if (t.Owner.Length == 0 || t.Field is "others" or "flagged" or "compared")
            yield break;
        if (!sentence.Contains(t.Owner, StringComparison.Ordinal))
            yield return $"states {t.Token} in a sentence that does not name {t.Owner} - give each named figure in the same sentence as its name";
    }

    private static IEnumerable<string> AllOrBoth(string sentence, Match m, FactToken t, IReadOnlyDictionary<string, FactToken> tokens)
    {
        var lead = sentence[Math.Max(0, m.Index - 10)..m.Index];
        var word = AllBoth().Match(lead);
        if (!word.Success)
            yield break;

        if (word.Groups[1].Value.Equals("both", StringComparison.OrdinalIgnoreCase) && t.Value != 2)
            yield return $"says 'both' of {t.Token}, which is {t.Value}";
        if (word.Groups[1].Value.Equals("all", StringComparison.OrdinalIgnoreCase)
            && t.Bases.Select(b => tokens.GetValueOrDefault(b)).Where(b => b is not null).ToList() is { Count: > 0 } wholes
            && !wholes.Any(b => b!.Value == t.Value))
            yield return $"says 'all' of {t.Token}, but it is only part of its whole";
    }

    // -- typed figures and number words ------------------------------------------------------------

    private static IEnumerable<string> LiteralFigures(string text, FreeMonthlyDigestPrompt prompt)
    {
        var stripped = TokenPattern().Replace(text, " ");
        stripped = Regex.Replace(stripped, @"\{\{[A-Z0-9_]+\}\}", " ");             // name and date placeholders
        stripped = Regex.Replace(stripped, @"\b\d+(st|nd|rd|th)\b", " ");           // ordinals
        stripped = DayBands().Replace(stripped, " ");
        stripped = Regex.Replace(stripped, @"\b2 or more\b", " ", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"\b1 of\b", " ");
        if (prompt.FactTokens.Keys.Any(k => k.Contains("top3", StringComparison.Ordinal)))
            stripped = Regex.Replace(stripped, @"\b3(?=\s+(?:\w+\s+){0,2}(?:locations|sites|people|Acts|categories|persons)\b)", " ");

        var typed = Regex.Matches(stripped, @"\d[\d,]*%?").Select(x => x.Value).Distinct().Take(5).ToList();
        if (typed.Count > 0)
            yield return $"types the figure(s) {string.Join(", ", typed)} - never type a figure; write the token for it from the input (for example {{{{F:<fact key>}}}} or {{{{NAME_1:item}}}})";
    }

    private static IEnumerable<string> NumberWords(string text)
    {
        // "two or more" is a fixed phrase; "at least one such compliance" is a definition, not a count (Minda Users, 2026-10-14).
        var t = Regex.Replace(text, @"\btwo or more\b|\bat least one\b", " ", RegexOptions.IgnoreCase);
        foreach (Match w in NumberWord().Matches(t))
            yield return $"spells the figure '{w.Value}' as a word - write the token for it from the input";
        foreach (Match w in QuantityOne().Matches(t))
            yield return $"writes '{w.Value.Trim()}' as a quantity - write the token for that figure from the input";
    }

    // ---------------------------------------------------------------------------------------------
    //  After repair - the repair step may trim a sentence; what is left must still be right
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// [Architect 2026-10-12] Repair runs on the bound text and may cut a trailing clause - e.g.
    /// ", reflecting a higher share ..." - after the checks above passed, which would let a pattern
    /// count ship as a plain count. The repaired body is mapped back to tokens (each sentence to the
    /// raw sentence it came from, its figures back to that sentence's tokens, in order) and the
    /// sentence-level checks run again. Typed-figure checks are skipped: repair may legitimately
    /// rewrite "2 of the 2" to "both".
    /// </summary>
    public static IReadOnlyList<string> ProblemsAfterRepair(string rawDraft, string repairedBody, FreeMonthlyDigestPrompt prompt)
    {
        if (prompt.FactTokens.Count == 0)
            return [];

        var rawSentences = rawDraft.Replace("\r\n", "\n").Split("\n\n")
            .SelectMany(p => SentenceSplit().Split(p.Trim()))
            .Select(s => s.Trim()).Where(s => s.Length > 0 && TokenPattern().IsMatch(s))
            .Select(s => (Raw: s, Bound: Normalise(Bind(s, prompt))))
            .ToList();

        var paragraphs = repairedBody.Replace("\r\n", "\n").Split("\n\n");
        var rebuilt = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var sentences = SentenceSplit().Split(paragraph.Trim()).Where(s => s.Trim().Length > 0);
            var parts = new List<string>();
            foreach (var s in sentences)
            {
                var ns = Normalise(s);
                var origin = rawSentences.FirstOrDefault(r => r.Bound == ns);
                if (origin.Raw is not null)
                {
                    parts.Add(origin.Raw);
                    continue;
                }

                var trimmedFrom = rawSentences.FirstOrDefault(r => ns.Length > 15 && r.Bound.StartsWith(ns.TrimEnd('.', '!', '?'), StringComparison.Ordinal));
                parts.Add(trimmedFrom.Raw is not null ? Retokenise(s, trimmedFrom.Raw, prompt) : s);
            }
            rebuilt.Append(string.Join(" ", parts)).Append("\n\n");
        }

        return Check(rebuilt.ToString(), prompt, literalChecks: false)
            .Select(p => "after repair shortened a sentence, it " + p)
            .ToList();
    }

    private static string Retokenise(string repaired, string raw, FreeMonthlyDigestPrompt prompt)
    {
        var result = repaired;
        var from = 0;
        foreach (Match m in TokenPattern().Matches(raw))
        {
            if (!prompt.FactTokens.TryGetValue(m.Groups["t"].Value, out var t))
                continue;
            var at = result.IndexOf(t.Rendered, from, StringComparison.Ordinal);
            if (at < 0)
                continue;
            result = result[..at] + t.Token + result[(at + t.Rendered.Length)..];
            from = at + t.Token.Length;
        }
        return result;
    }

    private static string Normalise(string s) => Regex.Replace(s.Replace("**", string.Empty), @"\s+", " ").Trim();

    // ---------------------------------------------------------------------------------------------
    //  Making the validator's own messages fixable on a redraft
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The downstream validator runs on bound digits and names figures as digits. On a redraft the
    /// model cannot type digits, so each figure in a message is followed by the token(s) it came from.
    /// </summary>
    public static string Annotate(string message, string rawDraft, FreeMonthlyDigestPrompt prompt)
    {
        if (prompt.FactTokens.Count == 0)
            return message;

        var used = TokenPattern().Matches(rawDraft).Select(m => m.Groups["t"].Value)
            .Where(prompt.FactTokens.ContainsKey).Distinct().Select(k => prompt.FactTokens[k]).ToList();

        return FigureInMessage().Replace(message, m =>
        {
            var raw = m.Value.Replace(",", string.Empty).TrimEnd('%');
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                return m.Value;
            var isPct = m.Value.EndsWith('%');
            var matches = used.Where(t => t.Value == v && t.IsPercent == isPct).Select(t => t.Token).ToList();
            return matches.Count == 0 ? m.Value : $"{m.Value} ({string.Join(" / ", matches)})";
        });
    }

    // ---------------------------------------------------------------------------------------------
    //  Patterns
    // ---------------------------------------------------------------------------------------------

    private static readonly Regex ComparisonWords = new(
        @"\bhigher\b|\babove (?:your |the )?(?:company |organisation(?:'s)? )?(?:average|rest|others?)\b|\b(?:larger|bigger|greater) share\b|\bmore than (?:the )?(?:rest|others?|average)\b|\bthan (?:the )?(?:rest|other)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"(?<t>\{\{(?:F|Q):[a-z0-9_]+\}\}|\{\{(?:NAME|EG)_\d+:[a-z_]+\}\})")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^\s*(?:\*\*)?\s*(?:of|out of|among)\s+(?:all\s+)?(?:the\s+|its\s+|their\s+|your\s+|these\s+|those\s+)?(?:\*\*)?\s*(?<t>\{\{(?:F|Q):[a-z0-9_]+\}\}|\{\{(?:NAME|EG)_\d+:[a-z_]+\}\})")]
    private static partial Regex OfToken();

    [GeneratedRegex(@"^\s*(?:\*\*)?\s*(?:of|out of|among)\s+(?:those|these|them)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OfThose();

    [GeneratedRegex(@"^\W*Of\s+(?:all\s+)?(?:the\s+|these\s+|those\s+|your\s+)?(?<y>\{\{(?:F|Q):[a-z0-9_]+\}\}|\{\{(?:NAME|EG)_\d+:[a-z_]+\}\})[^.]*?,\s*(?<x>\{\{(?:F|Q):[a-z0-9_]+\}\}|\{\{(?:NAME|EG)_\d+:[a-z_]+\}\})")]
    private static partial Regex InvertedOf();

    [GeneratedRegex(@"(?<=[.!?](?:\*\*)?)\s+(?=[A-Z0-9*{])")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<prev>\bIn \{\{PREV_MONTH\}\}|\{\{PREV_MONTH\}\}|\blast month\b)|(?<sofar>\bSo far in \{\{CURR_MONTH\}\}|\bso far this month\b)|(?<rest>\bBefore the end of \{\{CURR_MONTH\}\}|\bbefore \{\{CURR_MONTH\}\} ends\b|\bbetween today and\b)|(?<stock>\bIn total, from all months\b|\bfrom all months\b|\bacross all months\b|\bCurrently\b)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenerPattern();

    [GeneratedRegex(@"\boverdue\b|\bexpired\b|\bopen compliances?\b|\brests? with one person\b|\bperformer and reviewer\b", RegexOptions.IgnoreCase)]
    private static partial Regex StockKeywords();

    [GeneratedRegex(@"\bmore than 90 days\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoreThanNinety();

    [GeneratedRegex(@"\bhigher\b|\babove\b|\bmore than\b|\blarger\b|\bgreater\b", RegexOptions.IgnoreCase)]
    private static partial Regex Higher();

    [GeneratedRegex(@"\blower\b|\bbelow\b|\bless than\b|\bsmaller\b", RegexOptions.IgnoreCase)]
    private static partial Regex Lower();

    [GeneratedRegex(@"compared with|compared to|against|versus|\bvs\.?|average|\bthan\b|across your organisation|organisation-wide", RegexOptions.IgnoreCase)]
    private static partial Regex TenantLead();

    /// <summary>Only the verbs that assert a total fell due - "compliances due in {{PREV_MONTH}} are still open" is a true description of an open subset.</summary>
    [GeneratedRegex(@"\b(?:fell|fall|falls|were|was) due\b", RegexOptions.IgnoreCase)]
    private static partial Regex DuePhrase();

    [GeneratedRegex(@"\b(all|both)\s+(?:\*\*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AllBoth();

    [GeneratedRegex(@"\b(?:more than |over |at least |up to )?(?:30|31|60|61|90)(?:\s*(?:to|-)\s*(?:30|31|60|61|90))?\s+days\b|\b(?:0|1)\s*(?:to|-)\s*30\s+days\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayBands();

    [GeneratedRegex(@"\b(?:two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|dozen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWord();

    [GeneratedRegex(@"\bone\s+(?=(?:\w+\s+)?(?:compliances?|obligations?|locations?|sites?|licen[cs]es?|Acts?|categor(?:y|ies))\b)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityOne();

    [GeneratedRegex(@"(\d%)\s*(?:%|per ?cent\b)", RegexOptions.IgnoreCase)]
    private static partial Regex StrayPercent();

    [GeneratedRegex(@"(?<![\w{:])\d{1,3}(?:,\d{3})+%?(?![\w}])|(?<![\w{:])\d+%?(?![\w}])")]
    private static partial Regex FigureInMessage();
}

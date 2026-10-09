using System.Globalization;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>A deterministic monthly body, and whether the send-quality build had to give way to the legacy floor.</summary>
/// <param name="Body">Bound, with the closing lines - ready to render.</param>
/// <param name="UsedFloor">True when the send-quality build failed its own validation and <see cref="FreeMonthlyFallbackBody.BuildLegacy"/> shipped instead. Always an error worth a look.</param>
/// <param name="FloorReasons">Why the send-quality build was refused - empty unless <paramref name="UsedFloor"/>.</param>
public sealed record FreeMonthlyFallback(string Body, bool UsedFloor, IReadOnlyList<string> FloorReasons);

/// <summary>
/// The deterministic monthly body used whenever the LLM path cannot produce a valid email
/// - the email still goes out (design doc 10.5), same guarantee as the weekly lane.
///
/// <para><b>[REBUILT 2026-09-27, ADR 2026-09-27-free-digest-reflection-design Sec.4.13]</b> The
/// fallback must read like the real email, not like a table. It now:</para>
/// <list type="bullet">
/// <item>opens with the greeting and leads with the headline - the finding's own sentence, or the
/// headline fact inside its section, emphasised;</item>
/// <item>groups the rest by period, in <see cref="FreeMonthlyParagraphOrder"/>'s order;</item>
/// <item>NAMES the findings, through <see cref="DetectorSentences"/> - fixed sentences per
/// (detector, metric), true by construction - with each aggregate pattern's examples beside it;</item>
/// <item>states what the figures cannot cover (the <c>not_assessable</c> caveat, ADR C10).</item>
/// </list>
///
/// <para>It is built over <see cref="FreeMonthlyDigestPrompt"/> - the same facts, placeholders and
/// closed sets the model gets - in placeholder form, and passes the SAME validator a model draft
/// does before it is bound (ADR C11: the old builder wrote real month names and dates mid-sentence
/// and read facts outside the closed set, so it could never have passed). Every sentence is checked
/// on its own as it is built; one that fails is left out rather than costing the email.</para>
///
/// <para><b>The floor.</b> If the finished body still fails validation, <see cref="BuildLegacy"/>
/// ships instead - unchanged, and never selectable - and the caller logs and counts it. A silent
/// failure of the new template would be the one outcome this class must never produce.</para>
/// </summary>
public static partial class FreeMonthlyFallbackBody
{
    private const string Greeting = "Good morning,";

    /// <summary>A section base followed by up to this many of its non-zero "of those" lines - a fallback is a summary, not an inventory.</summary>
    private const int MaxLinesPerSection = 3;

    /// <summary>Backlog stays a line of context, never the body of the email.</summary>
    private const int MaxBacklogLinesPerSection = 2;

    public static FreeMonthlyFallback Build(FreeMonthlyDigestPrompt prompt)
    {
        IReadOnlyList<string> problems;
        string? draft = null;
        try
        {
            draft = Draft(prompt);
            problems = FreeMonthlyDigestValidator.Validate(draft, prompt).FailedChecks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*  Anything at all. This runs after the writer was billed; an exception escaping here
                would make the activity retry re-bill it. The floor exists
                for exactly this.                                                               */
            problems = [$"{ex.GetType().Name}: {ex.Message}"];
        }

        if (draft is not null && problems.Count == 0)
            return new FreeMonthlyFallback(
                FreeMonthlyPlaceholderBinder.Bind(draft, prompt.Bindings) + "\n\n" + FreeMonthlyClosing.For(prompt.Data.Edition),
                false, []);

        return new FreeMonthlyFallback(BuildLegacy(prompt.Data), true, problems);
    }

    /// <summary>The body in placeholder form, before validation and binding, without the closing lines.</summary>
    internal static string Draft(FreeMonthlyDigestPrompt prompt)
    {
        var data = prompt.Data;
        var slot = data.Edition.Slot;
        var facts = FreeMonthlyDigestPrompt.ForTheModel(data.Facts, slot);
        var headlineFact = facts.FirstOrDefault(f => f.IsHeadline);
        var candidateLeads = string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal);
        var leadFinding = candidateLeads ? prompt.NamedFindings.FirstOrDefault(n => n.Slot == 1) : null;

        var title = MonthlyDigestCalendar.Title(slot).ToLowerInvariant();
        var sentences = new List<Line>
        {
            // [2026-10-08] The same introduction as the written email: topic, then the date line, once.
            new(Period.Lead, $"Here is your {title} for {{{{CURR_MONTH}}}}. The figures below are as of {{{{AS_AT}}}}.", Priority: 0, Removable: false),
        };

        var leadSectionName = headlineFact?.Section;

        // -- the lead: the headline finding's sentence, or the headline fact inside its section ------
        var leadNamed = false;
        if (leadFinding is not null && DetectorSentences.For(leadFinding, prompt, emphasise: true) is { } leadSentence && Fits(leadSentence, prompt))
        {
            sentences.Add(new Line(Period.Lead, leadSentence, Priority: 0, Removable: false));
            leadNamed = true;
        }

        // -- facts, section by section, each section in the period its facts cover -----------------
        var sections = facts
            .Where(f => f.WindowScope != "ctx" || f.IsHeadline)
            .GroupBy(f => f.Section, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => f.DisplayOrder).ToList())
            .OrderBy(g => g[0].DisplayOrder);

        foreach (var section in sections)
        {
            var isLead = section[0].Section == leadSectionName && leadFinding is null;
            var period = isLead ? Period.Lead : PeriodOf(section[0]);
            string? lastBaseKey = null;
            var shown = 0;
            var backlogShown = 0;

            for (var i = 0; i < section.Count; i++)
            {
                var f = section[i];
                var nests = Nests(f);
                var isBacklog = FreeMonthlyDigestPrompt.IsBacklogFact(f);

                if (!f.IsHeadline)
                {
                    /*  [2026-10-07] "of those" must sit under ITS OWN parent - the base line stated
                        last in this section - not merely under some base. In the shared "context"
                        section t_rm_liability followed t_lm_due and read as last month's.        */
                    if (nests && (lastBaseKey is null || FreeMonthlyDigestPrompt.ParentOf(f, facts)?.FactKey != lastBaseKey))
                        continue;                                   // an "of those" line with no (or the wrong) "those" before it
                    if (i > 0 && (f.FactValue == 0 || shown >= MaxLinesPerSection))
                        continue;
                    if (isBacklog && backlogShown >= MaxBacklogLinesPerSection)
                        continue;
                }

                var text = FactSentence(f, emphasise: f.IsHeadline && headlineFact is not null);
                if (!Fits(text, prompt))
                    continue;

                if (!nests)
                    lastBaseKey = f.FactKey;
                shown++;
                if (isBacklog)
                    backlogShown++;

                // Only an "of those" line may be trimmed for length: removing a base would orphan the lines under it.
                sentences.Add(new Line(period, text, Priority: f.IsHeadline ? 0 : f.SeverityTier, Removable: !f.IsHeadline && nests));

                // Examples sit beside the pattern fact they illustrate - never a paragraph of their own.
                if (ExamplesSentence(f, prompt) is { } examples && Fits(examples, prompt))
                    sentences.Add(new Line(period, examples, Priority: f.SeverityTier + 1, Removable: true));
            }
        }

        // -- named findings, each in the period its detector measures -------------------------------
        foreach (var finding in prompt.NamedFindings)
        {
            if (leadNamed && ReferenceEquals(finding, leadFinding))
                continue;
            if (DetectorSentences.For(finding, prompt) is { } text && Fits(text, prompt))
                sentences.Add(new Line(PeriodOfDetector(finding.Candidate.Detector), text, Priority: 1, Removable: false));
        }

        // -- what the figures cannot cover (ADR C10) ------------------------------------------------
        foreach (var dq in data.DataQuality.Where(d => d.ItemCount > 0 && FreeMonthlyDigestPrompt.BoundsAFigureCode(d.Code)))
            if (CaveatSentence(dq) is { } caveat && Fits(caveat, prompt))
            {
                var home = sentences.LastOrDefault(s => s.Text.Contains("licence", StringComparison.OrdinalIgnoreCase))?.Period ?? Period.Stock;
                sentences.Add(new Line(home, caveat, Priority: 0, Removable: false));
            }

        // -- too short to be an email: add the size of the estate, which every reader can place ------
        if (Words(sentences) < MinWords)
            foreach (var ctx in facts.Where(f => f.WindowScope == "ctx" && !f.IsHeadline && f.FactValue > 0 && !Nests(f)).OrderBy(f => f.DisplayOrder))
            {
                var text = FactSentence(ctx, emphasise: false);
                if (Fits(text, prompt))
                    sentences.Add(new Line(Period.Context, text, Priority: 5, Removable: true));
                if (Words(sentences) >= MinWords)
                    break;
            }

        /*  [OWNER, 2026-10-09] Every figure paragraph ends with its closing line (shared rules Sec.4a):
            a sentence with no number that says what the figures mean. The validator now rejects a
            paragraph that ends on a figure, and this deterministic draft used to end every one of
            its paragraphs on a figure - so it failed its own validator and the floor body shipped
            instead, naming nothing. One fixed, number-free closing line per period, counted into
            the word ceiling below so the trim accounts for it.                                   */
        foreach (var period in sentences.Select(s => s.Period).Distinct().ToList())
            sentences.Add(new Line(period, ClosingLine(period), Priority: 0, Removable: false));

        // -- the ceiling: least severe removable lines go first --------------------------------------
        var maxWords = FreeMonthlyDigestValidator.MaxWordsFor(slot);
        while (Words(sentences) > maxWords
               && sentences.Where(s => s.Removable).OrderByDescending(s => s.Priority).ThenByDescending(sentences.IndexOf).FirstOrDefault() is { } drop)
            sentences.Remove(drop);

        var paragraphs = sentences
            .GroupBy(s => s.Period)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(s => IsClosingLine(s.Text) ? 1 : 0).Select(s => s.Text).ToList())
            // A period whose figure lines were all trimmed away keeps no orphan closing line.
            .Where(g => g.Any(t => !IsClosingLine(t)))
            .Select(g => string.Join(" ", g))
            .ToList();

        var body = Greeting + "\n\n" + string.Join("\n\n", paragraphs);

        // The same period order a model draft is given - the lead paragraph stays pinned.
        return FreeMonthlyParagraphOrder.Arrange(body);
    }

    private const int MinWords = 25;

    private enum Period { Lead = 0, Past = 1, Present = 2, Stock = 3, Future = 4, Context = 5 }

    /*  Number-free, placeholder-free, no capitalised word past the first, none of the banned
        phrases - each must pass SentenceProblems on its own, because Fits() is not applied to them. */
    private static readonly IReadOnlyDictionary<Period, string> ClosingLines = new Dictionary<Period, string>
    {
        [Period.Lead] = "These are the figures to read first this month.",
        [Period.Past] = "That is what last month left behind.",
        [Period.Present] = "That is where the month stands today.",
        [Period.Stock] = "That is the standing backlog, whichever month it arose in.",
        [Period.Future] = "That is what falls due before the month ends.",
        [Period.Context] = "That is the wider picture these figures sit in.",
    };

    private static string ClosingLine(Period period) => ClosingLines[period];

    private static bool IsClosingLine(string text) => ClosingLines.Values.Contains(text, StringComparer.Ordinal);

    private sealed record Line(Period Period, string Text, int Priority, bool Removable);

    private static int Words(IEnumerable<Line> lines) =>
        lines.Sum(l => Placeholder().Replace(l.Text, "ph").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length) + 2;   // + "Good morning,"

    /// <summary>A sentence is kept only if it passes the validator's own sentence-level truth checks.</summary>
    private static bool Fits(string sentence, FreeMonthlyDigestPrompt prompt) =>
        FreeMonthlyDigestValidator.ProseProblems(sentence, prompt).Count == 0;

    private static bool Nests(MonthlyFact f) =>
        f.DisplayLabel.StartsWith("of ", StringComparison.OrdinalIgnoreCase)
        || FactLabels.For(f.FactKey, f.DisplayLabel).StartsWith("of ", StringComparison.OrdinalIgnoreCase);

    /// <summary>"{value} {label}", in the reader wording the model is given (FactLabels), with its "as at" where the proc requires one.</summary>
    private static string FactSentence(MonthlyFact f, bool emphasise)
    {
        var value = InsightCardRules.Count(f.FactValue);
        var label = Readable(FactLabels.For(f.FactKey, f.DisplayLabel));
        var asAt = f.AsAtRequired ? ", as at {{AS_AT}}" : string.Empty;
        return $"{(emphasise ? $"**{value}**" : value)} {label}{asAt}.";
    }

    /// <summary>"Examples include {{EG_1}} at {{EG_1_AT}} and {{EG_2}}." - for the pattern fact they belong to.</summary>
    private static string? ExamplesSentence(MonthlyFact f, FreeMonthlyDigestPrompt prompt)
    {
        var names = prompt.Examples
            .Where(e => string.Equals(e.Example.PatternFactKey, f.FactKey, StringComparison.Ordinal))
            .Select(e => e.AtPlaceholder is { } at ? $"{e.Placeholder} at {at}" : e.Placeholder)
            .ToList();

        return names.Count switch
        {
            0 => null,
            1 => $"An example is {names[0]}.",
            _ => $"Examples include {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}.",
        };
    }

    /// <summary>One fixed sentence per data-quality code that bounds a figure - never the proc's methodology text.</summary>
    private static string? CaveatSentence(MonthlyDataQuality dq) => dq.Code switch
    {
        "licences_without_end_date" =>
            $"{InsightCardRules.Count(dq.ItemCount)} licences have no end date recorded, so they cannot be assessed for lapse and are not counted in these licence figures.",
        _ => null,
    };

    private static Period PeriodOf(MonthlyFact f) => f.WindowScope switch
    {
        "prev" => Period.Past,
        "curr" when f.Section == "rest_of_month" || f.FactKey.StartsWith("rm_", StringComparison.Ordinal) => Period.Future,
        "curr" => Period.Present,
        "stock" => Period.Stock,
        _ => Period.Context,
    };

    private static Period PeriodOfDetector(string detector) => detector switch
    {
        "last_month_slippage" => Period.Past,
        "licence_expiring_unrenewed" => Period.Future,
        _ => Period.Stock,
    };

    /// <summary>
    /// Proc labels are written for the model; a few spell a number ("two or more"), which the
    /// validator rightly refuses in prose. The label's own value is in the closed set as a digit.
    /// </summary>
    private static string Readable(string label) =>
        label.Trim().TrimEnd('.')
            .Replace("two or more", "2 or more", StringComparison.OrdinalIgnoreCase)
            .Replace("three or more", "3 or more", StringComparison.OrdinalIgnoreCase)
            .Replace("one or more", "1 or more", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// The pre-2026-09-27 fallback, kept UNCHANGED as the floor under <see cref="Build"/>. It lists
    /// facts by their SQL labels, names nothing, and is bound already. It is never chosen - it ships
    /// only when the send-quality build fails its own validation, which the caller logs and counts.
    /// </summary>
    public static string BuildLegacy(MonthlyDigestData data)
    {
        var asAt = data.AsOf.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var month = data.Edition.CurrMonthStart.ToString("MMMM", CultureInfo.InvariantCulture);
        var title = MonthlyDigestCalendar.Title(data.Edition.Slot).ToLowerInvariant();

        var paragraphs = new List<string>
        {
            "Good morning,",
            $"Here is your {title} for {month}, as at {asAt}.",
        };

        /*  Same shape the prompts ask for (shared Rule 14): the headline's section first, then the
            rest, and sections that are only overdue/expired backlog (or context) last and short. */
        static bool BacklogOrContext(MonthlyFact f) => FreeMonthlyDigestPrompt.IsBacklogFact(f) || f.WindowScope == "ctx";

        var sections = data.Facts
            .GroupBy(f => f.Section, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => f.DisplayOrder).ToList())
            .OrderByDescending(g => g.Any(f => f.IsHeadline))
            .ThenBy(g => g.All(BacklogOrContext))
            .ThenBy(g => g[0].DisplayOrder);

        foreach (var section in sections)
        {
            /*  The first fact of a section is its base ("812 obligations fell due last month");
                the "of those ..." labels after it only read correctly beneath it, so the base is
                always kept. After that, only non-zero facts that matter (tier 1-3) - a fallback is
                a summary, not an inventory - and at most TWO backlog (stock) facts per section, so
                the overdue backlog stays a line of context, never the body of the email.        */
            var backlogShown = 0;
            var shown = new List<MonthlyFact>();
            for (var i = 0; i < section.Count; i++)
            {
                var f = section[i];
                var matters = f.FactValue > 0 && f.SeverityTier <= 3;
                var isBacklog = FreeMonthlyDigestPrompt.IsBacklogFact(f);

                // [2026-10-07] An "of those" line only directly under its own parent (see Draft).
                if (i > 0 && !f.IsHeadline && f.DisplayLabel.StartsWith("of those", StringComparison.OrdinalIgnoreCase)
                    && shown.LastOrDefault(s => !s.DisplayLabel.StartsWith("of those", StringComparison.OrdinalIgnoreCase))?.FactKey
                       != FreeMonthlyDigestPrompt.ParentOf(f, section)?.FactKey)
                    continue;

                if (i == 0 || f.IsHeadline)
                    shown.Add(f);
                else if (matters && (!isBacklog || backlogShown < 2))
                {
                    shown.Add(f);
                    if (isBacklog)
                        backlogShown++;
                }
            }

            var sentences = shown.Select(f => f.IsHeadline
                ? $"**{f.FactValue.ToString(CultureInfo.InvariantCulture)}** {f.DisplayLabel}."
                : $"{f.FactValue.ToString(CultureInfo.InvariantCulture)} {f.DisplayLabel}.");

            var paragraph = string.Join(" ", sentences);
            if (shown.Any(f => f.AsAtRequired))
                paragraph += $" These figures are as at {asAt}.";

            paragraphs.Add(paragraph);
        }

        paragraphs.Add(FreeMonthlyClosing.For(data.Edition));
        return string.Join("\n\n", paragraphs);
    }
}

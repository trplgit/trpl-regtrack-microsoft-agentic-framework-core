using System.Text.RegularExpressions;

namespace Insights.Agents;

/// <summary>
/// Cosmetic clean-up of a model draft BEFORE it is validated. Emphasis is presentation, not truth:
/// a draft whose numbers, names and claims are all correct must not be thrown away because it
/// bolded three figures in a paragraph instead of one.
///
/// [FOUND ON THE FIRST REAL PREVIEW, 2026-09-20, tenant 1285] 13 of 15 drafts were rejected, and
/// every one of those 13 failed on over-bolding alone - the figures themselves were right. Rejecting
/// meant real tokens spent and the deterministic fallback sent instead, which is exactly the
/// "validator selects against the spec" failure FreeDigestValidator already had to be corrected for
/// twice. So this normalises instead: keep the FIRST bold figure in each paragraph, unwrap the rest.
///
/// It fixes ONLY emphasis. Every check that concerns truth - invented numbers, invented names or
/// percentages, the wrong headline, banned words, length - runs afterwards, unchanged.
/// </summary>
public static partial class FreeMonthlyDraftNormalizer
{
    /*  [ROOT CAUSE, 2026-09-21] Emphasis is now applied HERE and nowhere else. The model is told to
        write plain prose and use no markers at all.

        Every presentation defect this session came from asking the model to format. A prompt is a
        request the model interprets, and each interpretation drifted into the next defect: told not
        to bold bare numbers it bolded whole sentences; told to keep emphasis to five words it
        invented noun phrases - "3 liable open items", "5 people with liability-bearing work" - to
        fill the span, which then made the sentences read badly. Three rounds, three new defects,
        same cause.

        Formatting is deterministic and belongs in code. The figure that matters in a paragraph is
        its first one, because the prompts already require the point to lead - so that is what gets
        emphasised, with the unit that follows it and nothing else. The model cannot get this wrong
        any more, because it is no longer being asked.                                            */
    /// <param name="headlineFigure">
    /// The headline fact's value as digits, if the headline is a fact. [FOUND LIVE on PROD tenant
    /// 1008, 2026-09-23] The lead paragraph read "Of the 1,915 obligations that fell due in August,
    /// 590 remained open, including 180 that carry personal criminal liability" and emphasised
    /// 1,915 - the denominator - while 180, the headline the data layer chose, sat plain. In the
    /// first paragraph the headline figure is emphasised wherever it appears; elsewhere the first
    /// figure still leads.
    /// </param>
    public static string Normalize(string body, string? headlineFigure = null)
    {
        var text = body.Replace("\r\n", "\n").Trim();

        var isFirstProse = true;
        var paragraphs = new List<string>();
        foreach (var p in text.Split("\n\n", StringSplitOptions.None)
                     .SelectMany(p => p.StartsWith("Good morning,", StringComparison.Ordinal) ? [p] : SplitByPoint(KeepTheModelsInsightSpans(p))))
        {
            if (p.StartsWith("Good morning,", StringComparison.Ordinal))
            {
                paragraphs.Add(p);
                continue;
            }

            paragraphs.Add(EmphasiseLeadFigure(p, isFirstProse ? headlineFigure : null));
            isFirstProse = false;
        }

        return string.Join("\n\n", paragraphs);
    }

    /*  [OWNER, 2026-09-24] The model marks the insight; the code guards the marking.

        The 2026-09-21 decision above stripped every marker the model wrote, because asked to
        "bold what matters" it bolded whole sentences and invented noun phrases to fill a span.
        The code then emphasised the lead figure and looked for known impact phrases - a closed
        list that could only ever find wording it already knew. Read back over the PROD 1008
        emails, that produced paragraphs whose bold was the figure and nothing else, and the
        owner's brief is that the bold should carry the insight - what the figure means, where
        it sits, the state of the work - which is exactly the judgement the model has and the
        phrase list does not.

        So the model is asked again, but the interpretation drift is closed in code rather than
        in the prompt: a span is kept only if it is short (MaxModelSpanWords), says something
        (not a bare figure, which the code marks anyway), contains no placeholder (names are
        bound bold by FreeMonthlyPlaceholderBinder under its own run rule) and does not swallow
        a sentence. Anything else is unwrapped - never rejected - and the paragraph falls back to
        exactly what the code did before. The worst case is therefore yesterday's email.       */
    /// <summary>
    /// Keeps up to <see cref="MaxModelSpans"/> well-formed insight spans the model marked in a
    /// paragraph and unwraps the rest; an unbalanced marker unwraps them all.
    /// </summary>
    private static string KeepTheModelsInsightSpans(string paragraph)
    {
        if (CountOccurrences(paragraph, "**") % 2 != 0)
            return paragraph.Replace("**", string.Empty);

        var budget = ModelSpanBudget(paragraph);
        var kept = 0;
        return ModelSpan().Replace(paragraph, m =>
        {
            var inner = m.Groups[1].Value;
            if (kept < budget && IsAnInsightSpan(inner))
            {
                kept++;
                return m.Value;
            }

            return inner;
        });
    }

    /// <summary>
    /// A span worth keeping: two to <see cref="MaxModelSpanWords"/> words, at least one word that
    /// is neither a figure nor a unit noun, no placeholder, no sentence boundary inside it.
    /// </summary>
    private static bool IsAnInsightSpan(string inner)
    {
        var trimmed = inner.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("{{", StringComparison.Ordinal) || trimmed.Contains('\n'))
            return false;

        // A span that runs past a full stop is a sentence someone shouted, not an emphasis.
        if (trimmed.TrimEnd('.', '!', '?').IndexOfAny(['.', '!', '?']) >= 0)
            return false;

        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > MaxModelSpanWords)
            return false;

        // "12 items", "41 of 46 obligations" - a bare figure; the code marks the lead figure itself.
        return words.Any(w =>
        {
            var word = w.Trim(',', '.', ';', ':', '%');
            return word.Length > 0 && !word.All(c => char.IsDigit(c) || c == ',') && !UnitNouns.Contains(word) && word != "of" && word != "all";
        });
    }

    /// <summary>
    /// How many of the model's spans a paragraph keeps: one when a name will be bound bold in it,
    /// otherwise <see cref="MaxModelSpans"/> - the same budget the code's own impact phrases use,
    /// so a paragraph never carries more emphasis than it did before the model was asked.
    /// </summary>
    private static int ModelSpanBudget(string paragraph) =>
        Placeholder().Matches(paragraph).Any(m => m.Value.StartsWith("{{NAME_", StringComparison.Ordinal) || m.Value.StartsWith("{{EG_", StringComparison.Ordinal))
            ? 1
            : MaxModelSpans;

    /// <summary>The most spans of the model's own a paragraph keeps; the same as <see cref="MaxImpactSpans"/>.</summary>
    private const int MaxModelSpans = 2;

    /// <summary>
    /// The longest span the model may mark. Eight words holds "already past their due date and
    /// still open" and "no renewal in progress at that site"; a clause longer than that is the
    /// sentence itself.
    /// </summary>
    private const int MaxModelSpanWords = 8;

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>True when a position sits inside an existing emphasised span.</summary>
    private static bool IsInsideASpan(string paragraph, int index) =>
        paragraph[..index].Count(c => c == '*') % 4 != 0;

    /// <summary>
    /// Breaks a paragraph that makes several points into one paragraph per point. Nothing is added,
    /// removed or reordered - only the blank lines between sentences change.
    ///
    /// <para>[THE RECURRING DEFECT, 2026-09-21] "One point per paragraph" is the rule the prompt
    /// could never hold. It was dropped once in a rewrite and went unnoticed; restored, it still
    /// produced a Location paragraph carrying four subjects - two sites with liability, the
    /// scope-wide total, one site's rate against the average, and the consequence. Asking is
    /// unreliable because the model is deciding, mid-paragraph, whether a new sentence is a new
    /// subject. That judgement is mechanical: <b>a sentence that introduces a figure starts a new
    /// point; a sentence without one is explaining the point before it</b>, and belongs with it.</para>
    ///
    /// <para>Splitting only happens at three or more points, so a figure with one supporting figure
    /// stays together - it is a wall of text this is breaking up, not a pair of related sentences.</para>
    /// </summary>
    private static IEnumerable<string> SplitByPoint(string paragraph)
    {
        /*  [MEASURED on tenant 1082, 2026-09-22] This was reshaping paragraphs the model had got
            RIGHT. The Users draft arrived with 5 paragraphs - within the brief - and left with 9,
            because any paragraph carrying three figures was broken into three. That is what made
            "Hanif Sumra holds 579 of the 2,852" and "Hanif Sumra is one of 5 people in this
            position" two separate paragraphs, which then read as the same point made twice.

            This exists as a LAST RESORT against a wall of text, not as a routine reshaper. The
            prompts now cap a paragraph at two figures and the model follows that, so the bar is
            raised to a paragraph that is both long and genuinely carrying several subjects.
            A short paragraph is left exactly as written, whatever it contains.               */
        if (paragraph.Length < LongParagraph)
            return [paragraph];

        var sentences = Sentences().Matches(paragraph).Select(m => m.Value).Where(s => s.Trim().Length > 0).ToList();
        if (sentences.Count < 4)
            return [paragraph];

        var blocks = new List<List<string>>();
        foreach (var sentence in sentences)
        {
            if (blocks.Count == 0 || StartsANewPoint(sentence))
                blocks.Add([]);

            blocks[^1].Add(sentence.Trim());
        }

        return blocks.Count < 4
            ? [paragraph]
            : blocks.Select(b => string.Join(" ", b));
    }

    /// <summary>Roughly three full lines in an email client - the point at which a block reads as a wall.</summary>
    private const int LongParagraph = 400;

    /// <summary>
    /// True when a sentence introduces its own figure rather than elaborating the one before it.
    /// "That is 29 of 44 items", "302 of them are older" and "Of those, 12 carry liability" all
    /// carry figures but continue the previous point, so they stay with it.
    /// </summary>
    private static bool StartsANewPoint(string sentence)
    {
        var text = sentence.TrimStart();
        if (!text.Any(char.IsDigit))
            return false;

        var opening = string.Join(' ', text.Split(' ').Take(4)).ToLowerInvariant();
        return !Continuations.Any(c => opening.StartsWith(c, StringComparison.Ordinal) || opening.Contains(" " + c, StringComparison.Ordinal));
    }

    private static readonly string[] Continuations =
        ["that is", "that leaves", "these", "they", "those", "of them", "of these", "of those", "it is", "this is", "the same"];

    /// <summary>
    /// Wraps the paragraph's first figure and the unit that follows it: "5,178 overdue obligations",
    /// "41 of 46 items", "89% of all overdue items". A paragraph with no figure gets no emphasis,
    /// which is correct - there is nothing in it to stress.
    /// </summary>
    private static string EmphasiseLeadFigure(string paragraph, string? preferredFigure = null)
    {
        /*  A placeholder carries a digit - {{NAME_1}}, {{DATE_2}} - so the first "figure" in a
            paragraph can be inside one. Emphasising there would split the braces and the draft
            would fail as a malformed placeholder. Only figures outside them count.            */
        var placeholders = Placeholder().Matches(paragraph);
        var figures = FirstFigure().Matches(paragraph)
            .Where(m => !placeholders.Any(p => m.Index >= p.Index && m.Index < p.Index + p.Length)
                        && !IsAgeBand(paragraph, m))
            .ToList();

        // The headline figure, when this is the lead paragraph and it is there; otherwise the first.
        var start = (preferredFigure is null ? null
                        : figures.FirstOrDefault(m => m.Value.TrimEnd('%').Replace(",", string.Empty) == preferredFigure))
                    ?? figures.FirstOrDefault();

        // The model's own spans survive KeepTheModelsInsightSpans; they take part of the budget.
        var modelSpans = ModelSpan().Matches(paragraph).Count;

        if (start is null)
            return paragraph;

        /*  [2026-09-24] The model may have marked the lead figure inside a span of its own
            ("**367 are already past their due date**"). That span already carries the figure,
            so the code adds nothing there - wrapping again would nest the markers.            */
        if (IsInsideASpan(paragraph, start.Index))
            return EmphasiseImpact(paragraph, 0, Math.Max(0, ImpactBudget(paragraph) - modelSpans));

        /*  Walk forward from the figure and END AT THE THING IT COUNTS.

            [ROOT CAUSE, 2026-09-21] This walk used to run until it hit a listed stopper, so every
            word the list did not know was TAKEN - and the list had to enumerate open-class English
            to be right. It never could: "41 of 46 items fell" was fixed by adding verbs, then
            "**1 licence expired during** August" appeared because "expired" and "during" were not
            among them. Each fix bought one word.

            So the test is inverted. The span ends at the first UNIT NOUN, inclusive, and if none
            appears within three words the figure is emphasised alone. What these emails count is a
            closed set - items, obligations, licences, laws, locations, people - and a closed set
            can be complete in a way a list of verbs never can. "1 licence expired during August"
            now ends at "licence"; "41 of 46 items fell" still ends at "items".                   */
        var end = start.Index + start.Length;
        var unitEnd = start.Index + start.Length;

        /*  Five words, not four. [FOUND LIVE on PROD tenant 1008, 2026-09-23] "286 of those 590
            open obligations" needed five steps to reach its unit and got "**286**" alone.       */
        for (var taken = 0; taken < 5; taken++)
        {
            var next = NextWord().Match(paragraph[end..]);
            if (!next.Success)
                break;

            end += next.Length;

            var word = next.Groups[1].Value;
            if (UnitNouns.Contains(word.TrimEnd(',', '.', ';', ':')))
            {
                unitEnd = end;   // the unit names the figure: take it and stop
                break;
            }

            // A joining word or another figure ("41 of 46") only counts once a unit follows it.
            if (!(word == "of" || word == "all" || char.IsLower(word[0]) || word.All(c => char.IsDigit(c) || c == ',')))
                break;
        }

        end = unitEnd;

        // Never leave the emphasis dangling on a joining word.
        var span = paragraph[start.Index..end];
        while (span.EndsWith(" of", StringComparison.Ordinal) || span.EndsWith(" all", StringComparison.Ordinal))
            span = span[..span.LastIndexOf(' ')];

        var emphasised = paragraph[..start.Index] + $"**{span}**" + paragraph[(start.Index + span.Length)..];

        /*  Meaning phrases are looked for AFTER the lead span, so the lead figure is always the
            paragraph's first emphasis - the one the validator checks against the headline.

            [OWNER, 2026-09-23] A budget per paragraph, so emphasis stays emphasis. Names bind bold
            after this runs (FreeMonthlyPlaceholderBinder), and a pattern sentence can carry three
            sites and three categories. With the figure, two phrases and six names, that paragraph
            was more bold than plain. So the phrases yield to the names: three or more names in the
            paragraph leaves no room for a phrase, one or two names leaves room for one, and a
            paragraph with no name gets the figure and two phrases - the names ARE the insight in
            the first case, and the phrases are in the last.                                     */
        /*  [REVISED SAME DAY] With three or more names the binder now bolds only the first, so
            one phrase fits again: the figure, the top name, and what it means.                */
        /*  [2026-09-24] The model's own kept spans come out of the same budget, so the code's
            phrase list only fills what the model left empty. A paragraph the model marked well
            gets nothing added; one it left plain reads exactly as it did before.             */
        var phraseBudget = Math.Max(0, ImpactBudget(paragraph) - modelSpans);

        return EmphasiseImpact(emphasised, start.Index + span.Length + 4, phraseBudget);
    }

    /// <summary>Impact spans a paragraph may carry besides its figure: one beside a name, else two.</summary>
    private static int ImpactBudget(string paragraph) => ModelSpanBudget(paragraph);

    /// <summary>
    /// Also emphasises WHAT KIND of exposure a paragraph describes, not only how much of it there
    /// is. "12 of the 47 obligations carry personal criminal liability" has two things worth
    /// seeing, and the count is the less important of them.
    ///
    /// <para>[2026-09-22] This replaces the canned consequence sentence that used to follow such a
    /// figure ("That can mean prosecution of the officer responsible, not only a penalty."). That
    /// sentence appeared in every email, so readers stopped seeing it. The exposure is now shown
    /// where the figure is, in the data layer's own words, and nothing is added to the email.</para>
    ///
    /// <para>The phrases are a closed set taken from the procs' own <c>DisplayLabel</c>s - this
    /// never emphasises wording the model invented, and it adds no text.</para>
    /// </summary>
    private static string EmphasiseImpact(string paragraph, int from = 0, int budget = MaxImpactSpans)
    {
        /*  [OWNER, 2026-09-23] Up to TWO meaning phrases per paragraph, not one. With only the
            figure and one phrase emphasised, a six-sentence paragraph read as a block of text
            with two dark spots in it. The second span is what the figure MEANS - "already past
            their due date", "above your organisation's average", "rest with one person" - so a
            reader scanning the bold alone gets the insight, not only the count.               */
        var spans = 0;
        foreach (var phrase in ImpactPhrases)
        {
            if (spans >= budget)
                break;

            var at = paragraph.IndexOf(phrase, Math.Min(from, paragraph.Length), StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;

            // Never emphasise inside an existing span - that would produce "**a **b** c**".
            if (paragraph[..at].Count(c => c == '*') % 4 != 0)
                continue;

            paragraph = paragraph[..at] + $"**{paragraph.Substring(at, phrase.Length)}**" + paragraph[(at + phrase.Length)..];
            spans++;
        }

        return paragraph;
    }

    private const int MaxImpactSpans = 2;

    /// <summary>
    /// What makes a figure matter, most severe first - at most <see cref="MaxImpactSpans"/> are
    /// emphasised per paragraph, in this order. Every phrase is wording the procs or the prompts
    /// themselves produce; the plain state phrases at the end are a fallback so a paragraph with
    /// no stronger meaning still shows what its figure IS.
    /// </summary>
    private static readonly string[] ImpactPhrases =
    [
        "personal criminal liability",
        "personal liability",
        "liability-bearing",
        "that liability",
        "personally liable",
        "no renewal in progress",
        "no renewal filed",
        "no action recorded",
        "rated critical",
        "nobody assigned",
        "no one else is assigned",
        "never been started",
        "no longer an active user",
        "no person assigned",
        "no reviewer assigned",
        "rest with one person",
        "rests with one person",
        "all assigned to one person",
        "already past their due date",
        "overdue for more than 90 days",
        "higher overdue rate than your organisation",
        "above your organisation",
        "above your average overdue rate",
        "no end date",
        "still open",
        "remain open",
        "currently expired",

        /*  [OWNER, 2026-09-23] Every paragraph shows its impact, whatever it is. These plain
            states are the last resort, so a paragraph with none of the stronger phrases above
            still has the word that says what its figure IS.                                */
        "overdue",
        "expired",
        "fall due",
        "past due",
    ];

    /// <summary>
    /// An age band - "more than 90 days", "31 to 60 days" - is never what a paragraph is about, so
    /// it is skipped when choosing what to emphasise.
    ///
    /// <para>[FOUND LIVE on tenant 5, 2026-09-21] A Users paragraph opened "Most overdue work has
    /// been carried for more than 90 days", so the leading figure was 90 and the email emphasised
    /// **90 days** - the one number in the sentence carrying no information, since every one of
    /// these emails mentions that threshold. The prompts always excluded age bands from emphasis;
    /// now the code does, which is the only way it stays excluded.</para>
    /// </summary>
    private static bool IsAgeBand(string paragraph, Match figure)
    {
        if (!FreeMonthlyDigestPrompt.BandLiterals.Contains(int.TryParse(figure.Value.Replace(",", string.Empty), out var v) ? v : -1))
            return false;

        // "90 days", "90-day", "90 day" - the band is always immediately qualified as a duration.
        var after = paragraph[(figure.Index + figure.Length)..];
        return after.StartsWith(" day", StringComparison.OrdinalIgnoreCase) || after.StartsWith("-day", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything these emails count. The span ends here because this is what a figure is ABOUT -
    /// "304 obligations", "1 licence", "41 of 46 items".
    ///
    /// <para>This list is closed, and that is the entire reason it replaced a list of stop-words.
    /// The facts are generated by five stored procedures whose <c>DisplayLabel</c>s count exactly
    /// these things; a new unit arrives only when a proc adds one, which is a code change that
    /// comes past here. A verb, by contrast, can arrive in any draft the model writes.</para>
    /// </summary>
    private static readonly HashSet<string> UnitNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "item", "items", "obligation", "obligations", "licence", "licences", "law", "laws",
        "location", "locations", "site", "sites", "person", "people", "user", "users",
        "schedule", "schedules", "act", "acts", "category", "categories", "department",
        "departments", "branch", "branches", "entity", "entities", "renewal", "renewals",
        "case", "cases", "licence", "licences", "obligation", "obligations",
    };

    /// <summary>The paragraph's first number, with thousands separators and an optional per cent.</summary>
    [GeneratedRegex(@"\d[\d,]*%?")]
    private static partial Regex FirstFigure();

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholder();

    /// <summary>One emphasised span, markers included; the model's or the code's.</summary>
    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex ModelSpan();

    /// <summary>One sentence, terminator included; a trailing fragment counts as one too.</summary>
    [GeneratedRegex(@"[^.!?]+(?:[.!?]+|$)")]
    private static partial Regex Sentences();

    /// <summary>One word immediately after the current position - nothing but a space between.</summary>
    [GeneratedRegex(@"^ ([A-Za-z][A-Za-z-]*|\d[\d,]*%?)")]
    private static partial Regex NextWord();

}

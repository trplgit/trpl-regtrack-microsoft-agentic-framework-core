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
    public static string Normalize(string body)
    {
        var text = body.Replace("\r\n", "\n").Trim();

        var paragraphs = text.Split("\n\n", StringSplitOptions.None)
            .SelectMany(p => p.StartsWith("Good morning,", StringComparison.Ordinal) ? [p] : SplitByPoint(StripEmphasis(p)))
            .Select(p => p.StartsWith("Good morning,", StringComparison.Ordinal) ? p : EmphasiseLeadFigure(p));

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>Removes every marker the model wrote, keeping its words untouched.</summary>
    private static string StripEmphasis(string paragraph) => paragraph.Replace("**", string.Empty);

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
    private static string EmphasiseLeadFigure(string paragraph)
    {
        /*  A placeholder carries a digit - {{NAME_1}}, {{DATE_2}} - so the first "figure" in a
            paragraph can be inside one. Emphasising there would split the braces and the draft
            would fail as a malformed placeholder. Only figures outside them count.            */
        var placeholders = Placeholder().Matches(paragraph);
        var start = FirstFigure().Matches(paragraph)
            .FirstOrDefault(m => !placeholders.Any(p => m.Index >= p.Index && m.Index < p.Index + p.Length)
                                 && !IsAgeBand(paragraph, m));

        if (start is null)
            return paragraph;

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

        for (var taken = 0; taken < 4; taken++)
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
        return EmphasiseImpact(emphasised);
    }

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
    private static string EmphasiseImpact(string paragraph)
    {
        foreach (var phrase in ImpactPhrases)
        {
            var at = paragraph.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;

            // Never emphasise inside an existing span - that would produce "**a **b** c**".
            if (paragraph[..at].Count(c => c == '*') % 4 != 0)
                continue;

            return paragraph[..at] + $"**{paragraph.Substring(at, phrase.Length)}**" + paragraph[(at + phrase.Length)..];
        }

        return paragraph;
    }

    /// <summary>
    /// What makes a figure matter, most severe first - only one is emphasised per paragraph.
    /// Every phrase is wording the procs themselves produce.
    /// </summary>
    private static readonly string[] ImpactPhrases =
    [
        "personal criminal liability",
        "no renewal in progress",
        "no renewal filed",
        "no action recorded",
        "rated critical",
        "nobody assigned",
        "no one else is assigned",
        "never been started",
        "no longer an active user",
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

    /// <summary>One sentence, terminator included; a trailing fragment counts as one too.</summary>
    [GeneratedRegex(@"[^.!?]+(?:[.!?]+|$)")]
    private static partial Regex Sentences();

    /// <summary>One word immediately after the current position - nothing but a space between.</summary>
    [GeneratedRegex(@"^ ([A-Za-z][A-Za-z-]*|\d[\d,]*%?)")]
    private static partial Regex NextWord();

}

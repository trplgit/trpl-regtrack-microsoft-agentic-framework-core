using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Replaces the placeholders in a VALIDATED monthly draft with the real names, dates and months
/// fixed by <see cref="FreeMonthlyDigestPrompt.Build"/>.
///
/// Asserts BEFORE substituting and fails closed. FreeDigestEmailRenderer.Substitute replaces an
/// unmatched token with an empty string - it fails open - so a placeholder the prompt did not supply
/// would otherwise leave a sentence with a silent hole where a name should be.
/// </summary>
public static partial class FreeMonthlyPlaceholderBinder
{
    public static string Bind(string body, IReadOnlyDictionary<string, string> bindings)
    {
        var unknown = Placeholder().Matches(body).Select(m => m.Value).Where(p => !bindings.ContainsKey(p)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Cannot bind placeholder(s) {string.Join(", ", unknown)} - not supplied for this email.");

        var empty = bindings.Where(b => string.IsNullOrWhiteSpace(b.Value)).Select(b => b.Key).ToList();
        if (empty.Count > 0)
            throw new InvalidOperationException($"Placeholder(s) {string.Join(", ", empty)} would bind to empty text.");

        /*  [2026-09-22] A NAME is emphasised, a date is not. An Act, a site or a person is the
            thing the reader scans for - "which site?" is the question the paragraph answers - so
            it gets the same weight as the figure and the exposure.

            This happens at BINDING, deliberately: the validator has already run and counts markers
            per paragraph, and the model never sees a name at all, so neither can interfere. [2026-09-24]
            The model now marks insight spans of its own; the normalizer unwraps any that contain a
            placeholder, so a name is never inside a model span when it binds here.

            {{AS_AT}}, {{PREV_MONTH}}, {{CURR_MONTH}} and {{DATE_n}} are left plain - a date is
            context for the figure, not a thing to look up.                                     */
        /*  [OWNER, 2026-09-23] Every name is emphasised - EXCEPT inside a run of names. "including
            A, B and C" bolds A and leaves B and C plain, because three bold names in a row is a
            block, not an emphasis; "A with 792, B with 358 and C with 347" bolds all three, because
            each has its own figure between it and the next and the eye can rest. The test is the
            gap: a name bound fewer than MinWordsBetweenBoldNames words after the previous name in
            the same paragraph is part of that name's run and binds plain. Decided on the
            placeholders, before any text is substituted, so the names' own lengths play no part. */
        var paragraphs = body.Split("\n\n");
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var paragraph = paragraphs[i];
            var previousNameEnd = -1;
            var boldNames = 0;

            paragraphs[i] = Placeholder().Replace(paragraph, m =>
            {
                if (!NamePlaceholder().IsMatch(m.Value))
                    return bindings[m.Value];

                // A licence's site ("{{NAME_1}} at {{NAME_1_AT}}") belongs to the name before it:
                // always bold, and never the start of a run.
                if (m.Value.EndsWith("_AT}}", StringComparison.Ordinal))
                    return $"**{bindings[m.Value]}**";

                var backToBack = previousNameEnd >= 0
                                 && WordsIn(paragraph[previousNameEnd..m.Index]) < MinWordsBetweenBoldNames;
                previousNameEnd = m.Index + m.Length;

                // Not a rule that every name is bold: a run binds plain after its first, and a
                // paragraph never carries more than MaxBoldNamesPerParagraph bold names.
                if (backToBack || boldNames >= MaxBoldNamesPerParagraph)
                    return bindings[m.Value];

                boldNames++;
                return $"**{bindings[m.Value]}**";
            });
        }

        var bound = string.Join("\n\n", paragraphs);

        // Two adjacent emphasised spans ("**A** **B**") read as one; never leave "** **" behind.
        bound = bound.Replace("** **", " ").Replace("****", string.Empty);

        if (bound.Contains("{{", StringComparison.Ordinal) || bound.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Placeholder syntax survived binding - refusing to send a body with a raw token in it.");

        return bound;
    }

    /// <summary>
    /// Fewer words than this between two names makes them a run ("A, B and C"; "your A and B
    /// sites"), and the second binds plain. "A with 358, and B" has three - its own figure sits
    /// between them - and both are bold.
    /// </summary>
    private const int MinWordsBetweenBoldNames = 3;

    /// <summary>The most bold names one paragraph carries; the rest bind plain.</summary>
    private const int MaxBoldNamesPerParagraph = 3;

    /// <summary>Words in a stretch of text, placeholders and punctuation excluded.</summary>
    private static int WordsIn(string text) =>
        Placeholder().Replace(text, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Any(char.IsLetterOrDigit));

    /// <summary>
    /// A placeholder naming a thing - an Act, site, person, category or licence - whether it is a
    /// finding ({{NAME_n}}) or an example inside a pattern sentence ({{EG_n}}).
    /// [OWNER, 2026-09-23] Examples were bound plain so they would not read as findings; the
    /// owner's call is that the proper nouns are where the insight lies for a reader scanning the
    /// email, so every name is bold. The validator still never counts an example as a finding.
    /// </summary>
    [GeneratedRegex(@"^\{\{(NAME|EG)_\d+(_AT)?\}\}$")]
    private static partial Regex NamePlaceholder();

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// The lines the SYSTEM appends after the body (shared rules: "Do not write a closing line"). Fixed
/// text, never model output - so the "next week" promise is always true to the calendar.
/// </summary>
public static class FreeMonthlyClosing
{
    /*  [2026-09-21] Both lines were a flat label ("Next week: a closer look at your locations." /
        "RegInsights Ultimate shows every location, person and law behind these figures.") and the
        email stopped dead on them. They now read as sentences and say what the reader would GET,
        which is the only honest reason to upgrade: this email names a couple of places, the paid
        product names all of them and lets you follow them over time.                            */
    /*  [2026-09-21] Promises nothing the paid tier does not do today. It previously ended "and
        tracks whether each one is improving" - trend tracking is Phase 2 and Features:Snapshots is
        false, so that was a claim we could not keep. What IS true is the difference in coverage:
        this email names a couple of things, the paid report carries all of them.                */
    public const string UpgradeLine =
        "This briefing names only what stands out. RegInsights Ultimate carries the full picture - "
        + "every location, person and law behind these numbers.";

    /*  [FIXED 2026-09-21] These said "Next Sunday". The digest is GENERATED on Sunday and SENT on
        Monday morning (ADR-0001, FreeDigest:Schedule), so the reader has it in their inbox on a
        Monday and the next one arrives a Monday later. Telling them Sunday was simply wrong.    */
    public static string For(MonthlyDigestEdition edition)
    {
        var next = MonthlyDigestCalendar.Next(edition);
        var nextLine = next.Slot switch
        {
            /*  [2026-09-21] This must not promise what the next email cannot deliver. It said "and
                what has moved since this one" - month-over-month change is not computed anywhere
                (snapshots are Phase 2), so the overview simply reports the new month. Each line
                below states the SUBJECT of the next email and nothing more.                     */
            /*  [REWRITTEN 2026-09-22] These were labels, not sentences - "Next Monday: the people
                behind this work" leaves the reader to work out that another email is coming, who
                it is about and what it will tell them. Each now says plainly what arrives, when,
                and what question it answers.                                                    */
            MonthlyDigestSlot.Overview =>
                $"Next Monday you will get the {next.CurrMonthStart.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture)} overview, covering everything in your scope.",
            MonthlyDigestSlot.Users =>
                "Next Monday's email is about the people doing this work: who is carrying the most, and where a job depends on one person only.",
            MonthlyDigestSlot.Location =>
                "Next Monday's email is about your sites: whether this backlog is spread across all of them or concentrated at a few.",
            MonthlyDigestSlot.Act =>
                "Next Monday's email is about the Acts you are registered under: which ones are being missed, and at how many of your sites.",
            MonthlyDigestSlot.Licence =>
                "Next Monday's email is about your licences: which expire soon, and which have already lapsed without a renewal being filed.",
            _ => throw new ArgumentOutOfRangeException(nameof(edition), next.Slot, null),
        };

        return $"{nextLine}\n\n{UpgradeLine}";
    }
}

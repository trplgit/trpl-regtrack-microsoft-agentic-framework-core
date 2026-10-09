using System.Globalization;
using System.Text.RegularExpressions;

namespace Insights.Agents;

/// <summary>
/// [OWNER, 2026-10-07] The reader's word is "compliance", never "obligation". RegTrack's own screens
/// and the Detailed Report call each item a compliance, and testers reconcile against those.
///
/// <para>Applied ONCE, to finished text only - the accepted email body, the fallback body and the
/// insight card - AFTER every check has run. The word reaches the reader by three routes (the slot
/// procs' fact labels, fixed C# sentences, and the model copying either), and the repair, normaliser
/// and validator match on the internal wording; renaming at the source would mean an SQL deploy and
/// re-keying those checks. Renaming at the end changes what the reader sees and nothing that decides
/// what is true.</para>
/// </summary>
public static partial class FreeTierReaderTerms
{
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // [2026-10-08] "...in the same position.**." - a full stop inside the bold and another after it.
        text = StopAroundBold().Replace(text, "$1**");
        // [2026-10-08] "are **overdue **." - a space left inside the closing bold marker.
        text = SpaceInsideBold().Replace(text, "**");

        // [2026-10-08] Card titles ("Where it sits - personal exposure") are fixed text; "exposure" is jargon.
        text = text.Replace("personal exposure", "personal liability").Replace("Personal exposure", "Personal liability");
        // [2026-10-08] Card title "Where it sits - lapsed licences": the emails say "expired".
        text = text.Replace("lapsed licences", "expired licences");

        // [2026-10-08] The procs' comparison label, copied verbatim: "15 people have an unusually large share ...".
        text = UnusualShare().Replace(text, m =>
            (m.Groups["a"].Success ? (char.IsUpper(m.Value[0]) ? "A " : "a ") : string.Empty) + "high share");

        // [2026-10-08] One measure per sentence: "...2,240 overdue compliances; 9 other locations ..." becomes two sentences.
        text = Semicolon().Replace(text, m => ". " + m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));

        // Article first: "an obligation" -> "a compliance".
        text = AnObligation().Replace(text, m => (char.IsUpper(m.Value[0]) ? "A" : "a") + " compliance");
        text = Obligation().Replace(text, m =>
        {
            var plural = m.Value.EndsWith('s') || m.Value.EndsWith('S');
            var word = plural ? "compliances" : "compliance";
            return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(word[0]) + word[1..] : word;
        });

        return WithThousandsCommas(text);
    }

    /// <summary>
    /// [OWNER, 2026-10-08] "2211 of the 14650 overdue compliances" reached testers while other emails
    /// in the same set wrote "1,179". The model is inconsistent, so the comma is added here: every
    /// figure of 4 or more digits gets one. Values are untouched - the validator already accepts both forms.
    ///
    /// <para>A 4-digit value from 1800 to 2099 is usually a YEAR - "Factories Act, 1948", "8 Oct 2026" -
    /// and is left alone unless a count word follows it ("2047 compliances"), and never straight after a
    /// month or a word such as "Act" or "Rules".</para>
    /// </summary>
    internal static string WithThousandsCommas(string text) =>
        BareNumber().Replace(text, m =>
        {
            if (!long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return m.Value;

            if (value is >= 1800 and <= 2099)
            {
                var before = text[..m.Index];
                var after = text[(m.Index + m.Length)..];
                if (YearContext().IsMatch(before) || !CountFollows().IsMatch(after))
                    return m.Value;
            }

            return value.ToString("#,0", CultureInfo.InvariantCulture);
        });

    /// <summary>
    /// [OWNER, 2026-10-08] "At Pramod Kumar, 78% of overdue compliances ..." - "At" fits a site, not a
    /// person. Placeholder form, before binding: a person's finding reads "For {{NAME_n}}, ...".
    /// </summary>
    public static string ForPeople(string text, FreeMonthlyDigestPrompt prompt)
    {
        foreach (var finding in prompt.NamedFindings)
        {
            if (finding.NamePlaceholder is not { } placeholder || finding.Candidate.EntityKind is not ("user" or "person"))
                continue;
            text = Regex.Replace(text, $@"\b([Aa])t (\*\*)?{Regex.Escape(placeholder)}",
                m => (m.Groups[1].Value == "A" ? "For " : "for ") + m.Groups[2].Value + placeholder);
        }
        return text;
    }

    /// <summary>
    /// [OWNER, 2026-10-08, PROD tenant 1008 Users email] "Vipin Mishra holds 2,211 of the 14,628 overdue
    /// compliances or 15%. 15% of the overdue work is held by Vipin Mishra." - the second sentence
    /// says the first again. A sentence whose every figure was already stated by the sentence just
    /// before it, in the same paragraph, adds nothing and is dropped. Removing a sentence can never
    /// make the email untrue, so this runs after validation. A sentence with no figure is kept.
    /// </summary>
    public static string WithoutRepeatedFigures(string body)
    {
        var paragraphs = body.Replace("\r\n", "\n").Split("\n\n");
        var allMonthsSaid = false;
        for (var p = 0; p < paragraphs.Length; p++)
        {
            // [2026-10-08, owner] "In total, from all months," opens ONE paragraph in the email - the first
            // all-months one. Later all-months paragraphs start with their figure; "overdue" says the rest.
            if (AllMonthsOpener().IsMatch(paragraphs[p]))
            {
                if (allMonthsSaid)
                    paragraphs[p] = AllMonthsOpener().Replace(paragraphs[p], m => m.Groups["bold"].Value + char.ToUpperInvariant(m.Groups["first"].Value[0]), 1);
                allMonthsSaid = true;
            }

            var sentences = SentenceEnd().Split(paragraphs[p]);
            var kept = new List<string>();
            foreach (var sentence in sentences)
            {
                var figures = Figures(sentence);
                if (kept.Count > 0 && figures.Count > 0 && figures.IsSubsetOf(Figures(kept[^1])))
                    continue;

                // [2026-10-08] "In total, from all months, ... In total, from all months, ..." - the period
                // opener is said once per paragraph; the sentences after it keep their own base figure.
                kept.Add(kept.Count > 0 && PeriodOpener().IsMatch(kept[0]) ? PeriodOpener().Replace(sentence, m => m.Groups["bold"].Value + char.ToUpperInvariant(m.Groups["first"].Value[0]), 1) : sentence);
            }
            paragraphs[p] = string.Join(" ", kept);
        }
        return string.Join("\n\n", paragraphs);
    }

    [GeneratedRegex(@"^(?:In total, from all months,|Currently,|So far in [A-Z][a-z]+,|In [A-Z][a-z]+,)\s+(?<bold>\*\*)?(?<first>\S)")]
    private static partial Regex PeriodOpener();

    [GeneratedRegex(@"^In total, from all months,\s+(?<bold>\*\*)?(?<first>\S)")]
    private static partial Regex AllMonthsOpener();

    private static HashSet<string> Figures(string sentence) =>
        FigureToken().Matches(sentence).Select(m => m.Value.Replace(",", string.Empty)).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\d[\d,]*%?")]
    private static partial Regex FigureToken();

    [GeneratedRegex(@"(?<=[.!?](?:\*\*)?)\s+(?=[A-Z0-9*{])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@";\s+(\**)(\S)")]
    private static partial Regex Semicolon();

    [GeneratedRegex(@"(?<=\S)\s+\*\*(?=[\s.,;:!?]|$)")]
    private static partial Regex SpaceInsideBold();

    [GeneratedRegex(@"([.!?])\*\*[.!?]")]
    private static partial Regex StopAroundBold();

    [GeneratedRegex(@"\b(?<a>an\s+)?unusually\s+(?:large|high)\s+share\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnusualShare();

    [GeneratedRegex(@"\b[Aa]n\s+obligation\b")]
    private static partial Regex AnObligation();

    [GeneratedRegex(@"\b[Oo]bligations?\b")]
    private static partial Regex Obligation();

    /// <summary>4+ digits standing alone - not part of 1,234, 12.5, 2026-10-08, 08/10/2026, 10:30 or a placeholder.</summary>
    [GeneratedRegex(@"(?<![\d,.\-/:_{])\b\d{4,}\b(?!,\d|\.\d|[\-/:]\d|_|\}\})")]
    private static partial Regex BareNumber();

    /// <summary>"8 Oct 2026", "October 2026" (no comma - "In September, 2047 compliances" is a count), "Act, 1948", "Rules 2016".</summary>
    [GeneratedRegex(@"(?:\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+|\b(?:Act|Acts|Rules?|Regulations?|Code|Order|Scheme|Amendment|Ordinance|Standards?|FY|year)\s*,?\s*)$", RegexOptions.IgnoreCase)]
    private static partial Regex YearContext();

    [GeneratedRegex(@"^(?:\*\*)?\s+(?:\*\*)?(?:compliances?|obligations?|licen[cs]es?|locations?|sites?|people|persons?|users?|Acts|overdue|open|still|of|are|were|have|had|remain|fell|fall|carry|due)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountFollows();
}

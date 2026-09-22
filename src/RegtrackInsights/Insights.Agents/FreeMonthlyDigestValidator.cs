using System.Globalization;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Pre-send checks for a monthly digest body, run on the model's draft BEFORE placeholders are bound.
/// Any failure discards the draft; the caller substitutes <see cref="FreeMonthlyFallbackBody"/>.
///
/// A SEPARATE class from <see cref="FreeDigestValidator"/>, deliberately. The weekly validator bans
/// "overdue", bans location/department words and whitelists 13 aggregates - every one of those is the
/// opposite of what the monthly prompts require. Parameterising one class would leave the still-live
/// weekly lane one edit away from breaking.
///
/// The number check is the strongest, as in the weekly lane: the input is a closed set, so any digit
/// run that is not in it was invented. The name check is STRONGER than the weekly one because the
/// model is promised placeholders: a capitalised word that is not sentence-initial, not a placeholder
/// and not allow-listed is a name the model wrote itself.
/// </summary>
public static partial class FreeMonthlyDigestValidator
{
    /// <summary>Words that legitimately appear capitalised mid-sentence. The impact bank itself says "in RegTrack".</summary>
    private static readonly HashSet<string> CapitalisedAllowList = new(StringComparer.Ordinal)
    {
        "RegTrack", "RegInsights", "Ultimate", "I",
    };

    /// <summary>Shared Rule 9. "due to" is omitted on purpose: "due to expire" is ordinary licence prose.</summary>
    private static readonly string[] BannedPhrases =
    [
        // Causation and inference the data layer never measured (non-negotiable 5: the narrative
        // may only assert what the data verified). "why it happened" is not in any result set.
        "because", "caused by", "driven by", "as a result of", "leading to", "resulting in",
        "therefore", "which means", "this shows", "this indicates", "suggesting", "indicating",
        "likely",

        // Manufactured urgency, and reassurance a liable officer must not be given.
        "alarming", "dangerous", "urgent", "well done", "good news", "on track",

        /*  [MEASURED 2026-09-20] Consultant filler. These are not severity adjectives - they are
            empty sentences. A draft with the strict rules removed produced "exposes the estate to
            significant risk", "contributing to ongoing operational challenges" and "it is crucial
            to address these matters swiftly" in one email: manufactured urgency carrying no fact
            the reader can act on. Each phrase here was observed in a real draft.             */
        "requires immediate attention", "immediate attention", "crucial", "pressing",
        "risk of non-compliance", "operational challenges", "further complications",
        "mitigate", "swiftly", "heightening", "serious backlog",
    ];

    /*  [MEASURED 2026-09-20] This list is anti-fabrication ONLY - claims no result set supports.
        Do NOT add style or tone preferences to it. There is no redraft, so every entry is a
        full-draft death sentence: the reader loses the written email and gets the deterministic
        fallback instead. Adding the commentary phrases of the shared rules ("indicates",
        "disproportionate", "material impact") took acceptance from 3 of 5 to 1 of 5 on one
        preview - worse emails, not better ones. "concerning", "significant", "severe" and
        "healthy" were removed for the same reason: they are voice, and the prompt asks for
        restraint without destroying an email over one adjective.                             */

    /// <summary>
    /// Shared Rule 6b's bank, as code: the fragment that identifies each approved consequence, what
    /// must exist in the input for it to be true, and how to name the gap in the failure message.
    /// Evidence is checked against the DATA, never against a detector name - a fact can license a
    /// consequence with no finding attached, and both routes are legitimate.
    /// </summary>
    private static readonly (string Fragment, Func<MonthlyDigestData, bool> HasEvidence, string Needs)[] ConsequenceEvidence =
    [
        ("can no longer act",
            d => HasDetector(d, "deactivated_owner") || HasPositiveFact(d, "inactive_owner"),
            "deactivated owner"),

        ("if that person is unavailable",
            d => HasDetector(d, "single_point_of_failure") || HasPositiveFact(d, "single_performer"),
            "single point of failure"),

        ("have not been started",
            d => HasPositiveFact(d, "never_touched"),
            "never-started work"),

        ("no one is assigned to these",
            d => HasPositiveFact(d, "no_owner"),
            "unassigned work"),

        ("no valid licence on record",
            d => d.Facts.Any(f => f.FactKey.StartsWith("lic", StringComparison.Ordinal))
                 || d.Candidates.Any(c => c.Detector.Contains("licence", StringComparison.Ordinal)),
            "licence data"),

        ("prosecution",
            d => d.Facts.Any(f => f.ImpactClass == "personal_liability" && f.FactValue > 0)
                 || d.Candidates.Any(c => c.Detector.Contains("liability", StringComparison.Ordinal)),
            "liability-bearing work"),
    ];

    private static bool HasDetector(MonthlyDigestData data, string detector) =>
        data.Candidates.Any(c => string.Equals(c.Detector, detector, StringComparison.Ordinal));

    /// <summary>A fact whose key contains <paramref name="keyPart"/> and counts more than zero.</summary>
    private static bool HasPositiveFact(MonthlyDigestData data, string keyPart) =>
        data.Facts.Any(f => f.FactKey.Contains(keyPart, StringComparison.Ordinal) && f.FactValue > 0);

    /// <summary>
    /// Everything untrue in ONE sentence, so the sentence can be removed instead of the email.
    ///
    /// <para>[DIAGNOSED 2026-09-21] Fabrication here is not reasoning - it is transcription. The
    /// model wrote "2,709" where the fact says 3709, and "2,347" the run before: 4-digit figures
    /// re-typed out of a large JSON input. A small model at low temperature will keep doing that,
    /// so the guarantee cannot rest on it copying perfectly. Deleting the one sentence that carries
    /// the bad figure keeps every true sentence, ships nothing false, and costs no rejection.</para>
    ///
    /// <para>Used by <see cref="FreeMonthlyDraftRepair"/> before the whole-body pass below, which
    /// then acts as the backstop: if anything survives this, the email still refuses to go out.</para>
    /// </summary>
    public static IReadOnlyList<string> SentenceProblems(string sentence, FreeMonthlyDigestPrompt prompt)
    {
        var problems = new List<string>();
        var stripped = Placeholder().Replace(sentence, "ph");

        foreach (Match m in NumberToken().Matches(stripped))
        {
            var normalized = m.Value.Replace(",", string.Empty);
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !prompt.AllowedNumbers.Contains(value))
                problems.Add($"the number {m.Value} is not in the data");
        }

        foreach (Match m in DecimalNumber().Matches(stripped))
            problems.Add($"the decimal {m.Value} - every input value is a whole number");

        foreach (Match m in Percentage().Matches(stripped))
            if (!prompt.AllowedPercentages.Contains(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)))
                problems.Add($"{m.Groups["n"].Value}% is not a percentage the data supplied");

        foreach (var (fragment, hasEvidence, needs) in ConsequenceEvidence)
            if (stripped.Contains(fragment, StringComparison.OrdinalIgnoreCase) && !hasEvidence(prompt.Data))
                problems.Add($"'{fragment}' has no {needs} in this email's data");

        var factValues = prompt.Data.Facts.Select(f => f.FactValue).ToHashSet();
        foreach (Match m in ScopeWideCount().Matches(stripped))
            if (!factValues.Contains(int.Parse(m.Groups["n"].Value.Replace(",", string.Empty), CultureInfo.InvariantCulture)))
                problems.Add($"{m.Groups["n"].Value} counts one entity, not the whole scope");

        var nameScan = stripped.Replace("**", string.Empty);
        foreach (Match m in CapitalisedWord().Matches(nameScan))
            if (!CapitalisedAllowList.Contains(m.Value) && !IsSentenceInitial(nameScan, m.Index))
                problems.Add($"'{m.Value}' is a name or month the model wrote itself");

        return problems;
    }

    public static int MaxWordsFor(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => 360,
        MonthlyDigestSlot.Licence => 280,
        _ => 300,
    };

    /// <summary>
    /// Reviews a draft in two tiers.
    ///
    /// <para><b>failures</b> mean the email is WRONG - an invented number or name, a consequence
    /// with no evidence, one entity's count sold as the whole scope. These reject, and the reader
    /// gets the deterministic fallback, because a wrong number in a compliance email is the one
    /// outcome worse than a dull one.</para>
    ///
    /// <para><b>advisories</b> mean the email is true but could read better - it did not lead with
    /// the headline, it named only one of two findings. [MEASURED 2026-09-21] These used to reject:
    /// across ten attempts, 18 of 19 rejections were this tier, and every one of them threw away a
    /// true email in favour of the fallback. They are now logged so prompts can be tuned against
    /// real counts, and the email ships.</para>
    /// </summary>
    public static FreeMonthlyReview Validate(string body, FreeMonthlyDigestPrompt prompt)
    {
        var failures = new List<string>();
        var advisories = new List<string>();
        var text = body.Replace("\r\n", "\n").Trim();

        if (!text.StartsWith("Good morning,", StringComparison.Ordinal))
            failures.Add("does not start with 'Good morning,'");

        // -- placeholders: only ones we supplied, each name/date at most once ------------------
        var placeholderCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Placeholder().Matches(text))
        {
            if (!prompt.Bindings.ContainsKey(m.Value))
                failures.Add($"uses the placeholder {m.Value}, which this email was not given");
            placeholderCounts[m.Value] = placeholderCounts.GetValueOrDefault(m.Value) + 1;
        }

        /*  Naming the same thing twice is repetitive, not untrue - and the binder substitutes every
            occurrence correctly either way. [FOUND LIVE 2026-09-21] As a rejection it cost two good
            emails in one run, which is the same trade this class stopped making everywhere else:
            a true email replaced by the deterministic fallback over a matter of style.          */
        foreach (var (placeholder, count) in placeholderCounts)
            if (count > 1 && (placeholder.StartsWith("{{NAME_", StringComparison.Ordinal) || placeholder.StartsWith("{{DATE_", StringComparison.Ordinal)))
                advisories.Add($"uses {placeholder} {count} times - a name reads better stated once");

        /*  [MEASURED 2026-09-20] A named finding MUST be named. The detectors are the insight -
            "Client Specific obligations are overdue at 98%, against 62% across your scope" is the
            most useful line the data layer produced for that tenant, and a draft dropped it in
            favour of "One licence, which expires this month". An email that names nothing is a
            page of counts, which is the failure mode this whole redesign exists to fix.      */
        var findingNames = prompt.Bindings.Keys
            .Where(p => p.StartsWith("{{NAME_", StringComparison.Ordinal) && !p.EndsWith("_AT}}", StringComparison.Ordinal))
            .ToList();
        var namesUsed = findingNames.Count(placeholderCounts.ContainsKey);

        /*  Naming SOMETHING specific is the difference between an insight and a page of counts, so
            an email that names nothing when findings were available is rejected. Naming one of two
            is an advisory: the email is true and specific, just less complete than it could be, and
            [MEASURED 2026-09-21] demanding all of them was the single largest source of rejections -
            9 of 19 - each one costing a true email. The model tends to describe the second finding
            in words ("one location has all its work with one person") rather than name it.       */
        if (findingNames.Count > 0 && namesUsed == 0)
            failures.Add($"names none of the {findingNames.Count} finding(s) it was given - the email points at nothing specific");
        else if (namesUsed < findingNames.Count)
            advisories.Add($"names {namesUsed} of {findingNames.Count} findings - "
                           + string.Join(", ", findingNames.Where(p => !placeholderCounts.ContainsKey(p))) + " went unused");

        // Placeholders become a lowercase neutral word: no digits, no capital, no sentence boundary.
        var stripped = Placeholder().Replace(text, "ph");

        if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal))
            failures.Add("contains a malformed placeholder");

        // -- numbers: closed set ----------------------------------------------------------------
        foreach (Match m in NumberToken().Matches(stripped))
        {
            var normalized = m.Value.Replace(",", string.Empty);
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !prompt.AllowedNumbers.Contains(value))
                failures.Add($"contains the number {m.Value}, which is not in the facts or named findings");
        }

        foreach (Match m in DecimalNumber().Matches(stripped))
            failures.Add($"contains the decimal {m.Value} - every input value is a whole number");

        /*  [FOUND LIVE 2026-09-21] A draft wrote "Nine locations hold at least one expired licence".
            This is a HOLE in the closed-set check, not a style slip: the number check only sees
            digits, so a figure spelled as a word is never compared against the facts at all - the
            model could write any quantity it liked and nothing would notice. Number words are a
            truth failure for that reason. "three" is the documented exception, allowed only where a
            label says "the 3 holding the most".                                                 */
        foreach (Match m in NumberWord().Matches(stripped))
        {
            var word = m.Value.ToLowerInvariant();
            if (word == "three" && prompt.AllowedNumbers.Contains(3))
                continue;
            failures.Add($"spells the number '{m.Value}' as a word - figures must be digits, or they cannot be checked against the data");
        }

        // -- percentages: only _pct facts and MetricPct/TenantPct -------------------------------
        foreach (Match m in Percentage().Matches(stripped))
        {
            var value = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (!prompt.AllowedPercentages.Contains(value))
                failures.Add($"states {value}% - that value is not a percentage the data supplied");
        }

        /*  -- a consequence may only be stated where its evidence exists ------------------------
            [FOUND LIVE 2026-09-20] A draft wrote "The person they are assigned to can no longer act
            on them in RegTrack" about a LATE person, on a tenant whose inactive-owner count was 0.
            Every number in it was real, so the closed-set check passed: the fabrication was in what
            the sentence CLAIMED, not in its figures. The consequence bank (shared Rule 6b) is a
            closed set, and each entry needs something in the input to be true. Matching is on a
            distinctive fragment because Rule 6b lets the model shorten the sentence.            */
        foreach (var (fragment, hasEvidence, needs) in ConsequenceEvidence)
            if (stripped.Contains(fragment, StringComparison.OrdinalIgnoreCase) && !hasEvidence(prompt.Data))
                failures.Add($"says '{fragment}' but this email's data has no {needs} - that consequence is not supported");

        /*  -- a scope-wide claim needs a scope-wide number --------------------------------------
            [FOUND LIVE 2026-09-20] "192 of the 1041 overdue items across your scope" - 1041 is the
            finding's BaseCount, which is that PERSON's total. The scope's own total was 3,830. Both
            numbers were in the allowed set, so nothing caught it. Only a FactValue describes the
            whole scope; a finding's counts describe one entity. Percentages are excluded because
            TenantPct legitimately reads "against 9% across your scope".                          */
        var factValues = prompt.Data.Facts.Select(f => f.FactValue).ToHashSet();
        foreach (Match m in ScopeWideCount().Matches(stripped))
        {
            var value = int.Parse(m.Groups["n"].Value.Replace(",", string.Empty), CultureInfo.InvariantCulture);
            if (!factValues.Contains(value))
                failures.Add($"says {m.Groups["n"].Value} {m.Groups["phrase"].Value} - that number counts one entity, not the whole scope");
        }

        // -- banned words ----------------------------------------------------------------------
        foreach (var phrase in BannedPhrases)
            if (Regex.IsMatch(stripped, $@"\b{Regex.Escape(phrase)}\b", RegexOptions.IgnoreCase))
                failures.Add($"contains the banned word '{phrase}'");

        // -- invented names: capitalised word, not sentence-initial ------------------------------
        var nameScan = stripped.Replace("**", string.Empty);
        foreach (Match m in CapitalisedWord().Matches(nameScan))
        {
            if (CapitalisedAllowList.Contains(m.Value) || IsSentenceInitial(nameScan, m.Index))
                continue;
            failures.Add($"contains '{m.Value}' mid-sentence - a name or month the model wrote itself instead of a placeholder");
        }

        // -- form ------------------------------------------------------------------------------
        if (text.Contains('!'))
            advisories.Add("contains an exclamation mark");

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var paragraph in paragraphs)
        {
            var bold = CountOccurrences(paragraph, "**");
            if (bold % 2 != 0)
                failures.Add("has an unclosed ** bold marker");
            else if (bold > 2)
                failures.Add("bolds more than one figure in a paragraph");

            foreach (var line in paragraph.Split('\n'))
                if (ListOrHeading().IsMatch(line))
                    failures.Add("contains a bullet, numbered line or heading");
        }

        var words = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var maxWords = MaxWordsFor(prompt.Data.Edition.Slot);
        if (words > maxWords)
            failures.Add($"{words} words exceeds the {maxWords}-word limit");
        if (words < 25)
            failures.Add($"{words} words is too short to be a real email");

        // -- lead with the headline (shared Rule 7) ------------------------------------------------
        // The greeting may sit on its own paragraph or share one with the lead - take the first
        // paragraph of whatever follows it either way.
        var afterGreeting = text.StartsWith("Good morning,", StringComparison.Ordinal) ? text["Good morning,".Length..].Trim() : text;
        var lead = afterGreeting.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        // Leading with the headline is editorial judgement, not truth - advisory since 2026-09-21.
        if (prompt.HeadlineMarker is { } marker && !LeadContains(lead, marker))
            advisories.Add($"the first paragraph does not state the headline ({marker})");

        /*  [FOUND ON THE FIRST UAT PREVIEW, tenant 29] The lead bolded "2 overdue items that carry
            personal criminal liability" - 2 is a real fact (od_61_90_days), so the number check
            passed, and 2230 (the headline) appeared later in the same paragraph, so the check
            above passed too. The emphasised claim was still false. When the lead bolds a figure,
            that figure must BE the headline.                                                  */
        if (prompt.HeadlineMarker is { } headline && !headline.StartsWith("{{", StringComparison.Ordinal)
            && FirstBold().Match(Placeholder().Replace(lead, "ph")) is { Success: true } leadBold
            && !NumberToken().Matches(leadBold.Groups[1].Value).Any(m => m.Value.Replace(",", string.Empty) == headline))
            advisories.Add($"the first paragraph bolds '{leadBold.Groups[1].Value}', which is not the headline figure ({headline})");

        return new FreeMonthlyReview(failures.Count == 0, failures.Distinct().ToList(), advisories.Distinct().ToList());
    }

    private static bool LeadContains(string paragraph, string marker)
    {
        if (marker.StartsWith("{{", StringComparison.Ordinal))
            return paragraph.Contains(marker, StringComparison.Ordinal);

        var stripped = Placeholder().Replace(paragraph, "ph");
        return NumberToken().Matches(stripped).Any(m => m.Value.Replace(",", string.Empty) == marker);
    }

    /// <summary>
    /// True when the word starts the text, a line, or follows sentence-ending punctuation (. ! ? :),
    /// optionally through an opening quote or bracket.
    /// </summary>
    private static bool IsSentenceInitial(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == '\t' || text[i] == '"' || text[i] == '\'' || text[i] == '('))
            i--;
        return i < 0 || text[i] is '\n' or '.' or '!' or '?' or ':';
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholder();

    /// <summary>A comma-grouped run ("2,107") is ONE number - see FreeDigestValidator's [BUG FOUND LIVE] note.</summary>
    /// <summary>
    /// A count presented as covering the whole scope: a number, then words, then "across your
    /// scope" / "in your scope" / "across the estate", with no sentence end between them. The
    /// number must not be followed by % or "per cent" - TenantPct is genuinely scope-wide.
    /// The gap excludes digits so the match anchors on the number NEAREST the phrase: in "192 of
    /// the 1041 overdue items across your scope" the claim is about 1041, not 192.
    /// </summary>
    [GeneratedRegex(@"(?<n>\d{1,3}(?:,\d{3})+|\d+)(?!\s*(%|per\s?cent|percent))[^.!?;\d]{0,80}?(?<phrase>across your scope|in your scope|across the estate|across your entire scope)", RegexOptions.IgnoreCase)]
    private static partial Regex ScopeWideCount();

    /// <summary>
    /// A quantity spelled as a word. "one" is excluded: it is overwhelmingly the pronoun ("one of
    /// 3 licences", "the only one"), and as a quantity it is the least dangerous value there is.
    /// </summary>
    [GeneratedRegex(@"\b(two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWord();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex FirstBold();

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex DecimalNumber();

    [GeneratedRegex(@"(?<n>\d+)\s*(%|per\s?cent\b|percent\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Percentage();

    [GeneratedRegex(@"\b[A-Z][A-Za-z'\-]*\b")]
    private static partial Regex CapitalisedWord();

    [GeneratedRegex(@"^\s*([-*•]\s|\d+[.)]\s|#)")]
    private static partial Regex ListOrHeading();
}

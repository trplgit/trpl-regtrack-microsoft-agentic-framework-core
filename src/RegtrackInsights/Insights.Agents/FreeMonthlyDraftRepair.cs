using System.Text.RegularExpressions;

namespace Insights.Agents;

/// <summary>
/// Deterministic clean-up of a draft before it is validated: removes sentences that comment on the
/// figures rather than stating them, and the sign-off lines the model adds unasked.
///
/// <para><b>Why repair instead of reject.</b> [MEASURED 2026-09-21] Across ten model attempts, 18 of
/// 19 rejection reasons were style, not truth - "this indicates", "which means", a stray "Please
/// review these findings". Rejecting on those threw away a TRUE email and sent the deterministic
/// fallback, which reads far worse. A hard gate on a probabilistic behaviour guarantees fallbacks:
/// the model will write "this indicates" some fraction of the time whatever the prompt says.</para>
///
/// <para><b>Why deleting a sentence is safe.</b> Removing a sentence can never introduce a claim.
/// Every number, name and consequence that survives was already in the draft and is still checked
/// by <see cref="FreeMonthlyDigestValidator"/> afterwards. This class only ever deletes - it never
/// rewrites, never merges, and never invents connecting words, because any of those could change
/// what the email asserts.</para>
///
/// <para>Truth failures are NOT repaired. An invented number, an unsupported consequence or a
/// missing finding still rejects: those mean the email is wrong, and wrong must fall back.</para>
/// </summary>
public static partial class FreeMonthlyDraftRepair
{
    /// <summary>
    /// Phrases whose sentence is removed. Every one is either an assertion about causation that no
    /// result set measured, manufactured urgency, or filler - and every one was seen in a real
    /// draft. Matched case-insensitively on a word boundary.
    /// </summary>
    /*  A trailing clause where the model adds its own inference to a sentence that was fine:
        "..., indicating a local issue", "..., which means the work is not being done". The clause
        goes; the sentence stays.

        [FOUND LIVE 2026-09-21] This distinction is the whole game. Removing the SENTENCE deleted
        "This represents 49% of overdue items at {{NAME_1}}, which is significantly higher than the
        average of 9% across your organisation" - a computed comparison, the most useful line in
        that email, and the only place the finding was named. Ban the vocabulary of explanation and
        you are left with a list of counts; that loop is what kept producing the same email.     */
    [GeneratedRegex(@",?\s*(which (means|indicates|shows|suggests|limits|can|could|may|makes|reflects)|indicating|suggesting|reflecting|highlighting|showing|demonstrating|meaning|if not addressed)\b[^.!?]*", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingInference();

    /*  Intensifiers the model assigns itself. The comparison beside them is real and stays; only
        the adverb goes, which cannot change what the sentence asserts. NOTE: "disproportionate"
        and "unusually" are NOT here - the procs' own DisplayLabels use those words, so they are
        the data's wording, not the model's.                                                    */
    [GeneratedRegex(@"\b(significantly|significant|particularly|notably|notable|considerably|markedly|especially)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Intensifier();

    private static readonly string[] RemoveTheSentence =
    [
        /*  Causation the data layer never computed. Unlike a trailing inference, a sentence built
            on one of these has nothing left once the claim is removed, so the sentence goes.   */
        "because", "caused by", "driven by", "as a result of", "leading to", "resulting in",
        "therefore", "this shows", "this indicates", "this reflects", "this suggests",

        /*  [2026-09-21] This list is deliberately SHORT. It is far more important that a real,
            computed explanation reaches the reader than that every sentence is perfectly worded -
            a slightly clumsy phrase is a cost worth paying, a deleted comparison is not. Only
            sentences whose whole purpose is commentary belong here.
            NOT "disproportionate" or "unusually": the procs' own DisplayLabels use both.       */
        "alarming", "dangerous", "urgent",

        /*  [FOUND LIVE on tenant 5, 2026-09-21] Sentences that assert a norm nobody computed
            ("a higher share than typically seen"), or speculate ("this could impact ...",
            "the issues may not be evenly distributed"), or close with nothing ("Overall, the
            standing position shows areas requiring attention"). None states a fact.          */
        "typically", "usually seen", "normally", "than expected", "than usual",
        "could impact", "may not be", "might be", "appears to", "seems to",
        "suggest", "suggests", "requiring attention", "areas requiring",
        "highlights", "underscores", "points to", "where the most challenges",

        /*  [FOUND LIVE on tenant 5] The model describing its own input to the reader: "It is the
            only licence in the recent finding with that position." The reader has no idea what a
            finding is - they have licences and sites.                                          */
        /*  [WIDENED 2026-09-22] These were "the named finding" etc, and "1 of the 2 licences in
            this position is also represented by the other named finding" walked straight through.
            The bare noun catches every determiner.                                              */
        "named finding", "recent finding", "this finding", "the finding",
        "the figures provided", "the data provided", "the input",

        /*  [FOUND LIVE on tenant 1082, 2026-09-22] "This is the only location identified with that
            pattern." Identified by whom, and what pattern? The sentence reports on the ANALYSIS
            instead of on the reader's business, and a compliance head has sites and Acts, not
            patterns. Deleting is right: strip the claim and nothing is left to keep.            */
        /*  [REVERTED 2026-09-22, SAME DAY] "this position" / "that position" were added here and
            cost two of five emails outright. They appear in the RESIDUAL line the prompts ask for -
            "it is 1 of 3 licences in that position" - which is the sentence carrying {{NAME_1}}.
            Deleting it left a draft naming none of its findings, so validation failed and the
            deterministic fallback shipped: a raw list of every fact, which is far worse than a
            slightly stiff phrase. The comment ten lines below had warned of this exactly.

            "identified with" and "pattern" stay: those describe the ANALYSIS and never carry a
            placeholder, so removing them costs nothing.                                          */
        "identified with", "this pattern", "that pattern", "the comparison",

        /*  [2026-09-21] A phrase that became a tic: it appeared in almost every email, and a
            sentence a reader learns to skip is worse than no sentence. The prompt now asks for the
            same meaning in varied words; this stops the canned form shipping if it slips back.
            Safe to delete outright - the sentence carrying it is commentary, and the figure it
            comments on is always stated in the sentence before.                                */
        "not last month's slip", "not last month’s slip", "not August's slip", "not August’s slip",

        /*  [FOUND LIVE on tenant 23] A consequence that only restates the fact before it:
            "1,017 open items have no one assigned to do them. No one is assigned to these in
            RegTrack." The second sentence carries no figure and adds no meaning. NOTE this must
            not catch the single-point-of-failure line, which says "no one ELSE is assigned to
            THAT work" and is about a different thing entirely.                                */
        "no one is assigned to these", "these have not been started",

        /*  [FOUND ACROSS TENANTS 5, 23 and 1355] "the position" used as filler - "remain in the
            forward position", "remains part of the position in September". It says nothing and
            reads as internal shorthand. NOT plain "position": "it is the only person in this
            position" is the residual line and is doing real work.                             */
        "the forward position", "in the forward position", "part of the position",
        "ongoing challenges", "need attention", "needs attention", "the situation reflects",

        /*  [REMOVED FROM THE PRODUCT 2026-09-22] The canned liability consequence. It was in the
            shared bank, so it appeared in every email, verbatim, and a sentence a reader sees five
            times a month stops carrying weight - which is the opposite of what it was for.

            Nothing is lost by deleting it: the fact's own label says "carry personal criminal
            liability for the responsible officer", and the normaliser now EMPHASISES that phrase.
            The impact is shown where the figure is, rather than restated underneath it.        */
        "prosecution of the officer", "not only a penalty",

        // Reassurance a personally liable reader must not be given.
        "well done", "good news", "on track", "healthy",

        // Instructions to the reader: this email reports, it does not task anyone.
        "please review", "we recommend", "it is recommended", "should be addressed",
        "action is required", "take action", "ensure compliance", "requires attention",
        "needs attention", "warrants attention",

        /*  [FOUND LIVE 2026-09-21] Severity the model assigned itself. The data layer ranks
            severity; the writer states the figure. "The situation at X is particularly concerning"
            adds no fact - it tells a personally liable reader how to feel. These are repaired
            rather than rejected because the rest of the sentence is usually true and useful.   */
        "concerning", "worrying", "critical situation", "the situation is",

        // A recap is not content. The last paragraph should carry its own.
        "in summary", "to summarise", "to summarize", "overall,", "in conclusion",
    ];

    /*  A figure the model spelled out. Converting it to digits does not change what the sentence
        says, and it makes the claim CHECKABLE: the closed-set number check only sees digits, so
        "Nine locations hold an expired licence" would otherwise sail past the one guarantee that
        every figure came from SQL. After this, 9 is compared against the facts like any other
        number - and still rejects if it was invented. "one" is left alone: it is nearly always the
        pronoun ("one of 3 licences"), and "three" is converted like the rest, because the labels
        that legitimately say "three" all carry a top-3 fact whose value is in the allowed set.  */
    private static readonly (string Word, string Digit)[] SpelledNumbers =
    [
        ("twenty", "20"), ("nineteen", "19"), ("eighteen", "18"), ("seventeen", "17"),
        ("sixteen", "16"), ("fifteen", "15"), ("fourteen", "14"), ("thirteen", "13"),
        ("twelve", "12"), ("eleven", "11"), ("ten", "10"), ("nine", "9"), ("eight", "8"),
        ("seven", "7"), ("six", "6"), ("five", "5"), ("four", "4"), ("three", "3"), ("two", "2"),
    ];

    public static RepairedDraft Apply(string body, FreeMonthlyDigestPrompt? prompt = null)
    {
        var removed = new List<string>();
        var paragraphs = new List<string>();

        /*  [FOUND LIVE 2026-09-21] Nothing repeats. "This is a standing position, not last month's
            slip" appeared in 8 of 10 emails, and "can mean prosecution of the officer responsible"
            appeared TWICE inside a single email. A manager reading these weekly notices immediately,
            and a repeated line is filler wearing the clothes of a finding. Two rules, both here
            rather than in the prompt, because the prompt asked and the model did it anyway:
              - a sentence whose wording repeats an earlier one is dropped;
              - the approved consequences (shared Rule 6b) are allowed ONCE per email, for the most
                severe item, so they read as a point rather than a refrain.                      */
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consequencesUsed = new HashSet<string>(StringComparer.Ordinal);
        var asAtStated = false;
        var namesStated = new Dictionary<string, int>(StringComparer.Ordinal);
        var phrasesUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var paragraph in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.None))
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
                continue;

            // The greeting is not prose and must survive untouched.
            if (trimmed.StartsWith("Good morning,", StringComparison.Ordinal))
            {
                paragraphs.Add(trimmed);
                continue;
            }

            var kept = new List<string>();
            foreach (var original in SplitSentences(trimmed))
            {
                /*  Least destructive first: trim the model's own inference off the end, then its
                    intensifiers, and only drop the whole sentence if what remains is still a claim
                    the data never made. A comparison keeps its facts and loses its editorialising. */
                /*  [FOUND LIVE on tenant 1082, 2026-09-22] The inference trim broke a sentence:
                    "47 of last month's obligations remain open, with 4 of the 11 people who had
                    work due showing an unusually large share still open" lost ", showing..." and
                    ended "...who had work due." - a clause with its predicate cut off.

                    A participle carries the verb of the clause introduced by "with" or "including".
                    Trimming it there leaves a fragment, so the sentence is left whole; the words
                    it keeps are the data's own and the reader can follow them.                  */
                var sentence = DanglingIfTrimmed(original)
                    ? original
                    : TrailingInference().Replace(original, string.Empty);
                sentence = Intensifier().Replace(sentence, string.Empty);

                foreach (var (word, digit) in SpelledNumbers)
                    sentence = Regex.Replace(sentence, $@"\b{word}\b", digit, RegexOptions.IgnoreCase);

                /*  "As at {{AS_AT}}" belongs on the FIRST as-at figure and nowhere else. [FOUND
                    LIVE on tenant 1082, 2026-09-22] The Overview opened two paragraphs with the
                    same date. The rule said "once", meaning once per paragraph, so every paragraph
                    citing such a figure repeated it - and a date the reader already has, restated,
                    is the same filler as any other refrain. Only the qualifier is removed; the
                    figure and its sentence are untouched.                                       */
                if (asAtStated && AsAtPrefix().IsMatch(sentence))
                {
                    sentence = AsAtPrefix().Replace(sentence, " ", 1);
                    removed.Add($"repeats 'as at': {original.Trim()}");
                }
                else if (AsAtPrefix().IsMatch(sentence))
                {
                    asAtStated = true;
                }

                /*  A SCOPING PHRASE IS A QUALIFIER, NOT A REFRAIN. [FOUND LIVE on tenant 1082,
                    2026-09-22] "Across your organisation" opened three paragraphs of the Location
                    email and twice inside one of them - "...across your organisation. Across your
                    organisation, 47 of the 194..." - which reads as a stammer.

                    Shared Rule 6 ("every figure says where it applies") is what produced it, and
                    it is a good rule: the first use earns its place. After two, the reader has
                    understood the frame and the phrase is noise, so it is stripped from the
                    sentence. Only the qualifier goes; the figure and the claim stay.           */
                /*  A HEAVY PHRASE, TWICE AT MOST. [FOUND LIVE on tenant 1082, 2026-09-22] "personal
                    criminal liability" appeared four times in one Overview. It is the most serious
                    thing the email says, and saying it four times in 120 words turns it into
                    wallpaper - the reader skims past the fourth exactly when it matters most.

                    Substituted, not deleted: by the third mention the reader knows what liability
                    is meant, and "that liability" reads as ordinary English while keeping the claim
                    intact. Deleting would remove a real finding, which is never the right trade. */
                foreach (var (phrase, shortForm) in HeavyPhrases)
                {
                    if (!sentence.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (phrasesUsed.GetValueOrDefault(phrase) >= MaxHeavyPhraseUses)
                    {
                        sentence = Regex.Replace(sentence, Regex.Escape(phrase), shortForm, RegexOptions.IgnoreCase);
                        removed.Add($"shortened repeated '{phrase}': {original.Trim()}");
                    }
                    else
                    {
                        phrasesUsed[phrase] = phrasesUsed.GetValueOrDefault(phrase) + 1;
                    }
                }

                foreach (var phrase in ScopingPhrases)
                {
                    if (!sentence.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        continue;

                    /*  [CORRECTED 2026-09-22] A phrase completing a COMPARISON is never stripped.
                        Removing it left "412 of its 1,170 carry personal criminal liability: 35%,
                        compared with 21%." in a customer email - 21% of what? The guard against a
                        stammer had created the vagueness it was meant to prevent. Everywhere else
                        the frame is already established and the phrase is noise; after "compared
                        with" or "against" it is the second half of the claim.                  */
                    if (ComparisonScope().IsMatch(sentence))
                        continue;

                    if (phrasesUsed.GetValueOrDefault(phrase) >= MaxScopingPhraseUses)
                    {
                        sentence = Regex.Replace(sentence, Regex.Escape(phrase) + @",?\s*", " ", RegexOptions.IgnoreCase);
                        removed.Add($"repeats '{phrase}': {original.Trim()}");
                    }
                    else
                    {
                        phrasesUsed[phrase] = phrasesUsed.GetValueOrDefault(phrase) + 1;
                    }
                }

                /*  "3 of the 3 expired licences" is arithmetic pretending to be a comparison.
                    [FOUND LIVE on tenant 1082, 2026-09-22] The emails carried "3 of the 3 expired
                    licences have no renewal", "2 of the 2 licences", "1 of its 1 expired licences".
                    A part equal to its whole is "all of them", and English has shorter words for
                    it. Both numbers came from the data, so nothing here changes what is asserted -
                    only how the same fact reads.                                                */
                /*  "A FURTHER" ADDS TWO NUMBERS THAT DO NOT ADD. [FOUND LIVE on tenant 1082,
                    2026-09-22] "Overdue obligations carrying personal criminal liability sit under
                    24 Acts... A further 22 Acts are overdue across an unusually large share of the
                    locations where they apply." Those 22 are not 22 MORE Acts - they are the same
                    Act population measured a second way, so the sentence told the reader there
                    were 46. Shared Rule 8.3 forbids exactly this and the model wrote it anyway.

                    The words are simply removed. A count stands perfectly well on its own, and
                    deleting a connective can never make a true sentence false - whereas leaving
                    it in asserts an arithmetic relationship nobody computed.                    */
                sentence = AdditiveOpener().Replace(sentence, string.Empty);

                sentence = SamePartAndWhole().Replace(sentence, m =>
                    m.Groups["n"].Value == "1" ? "the only " : m.Groups["n"].Value == "2" ? "both " : $"all {m.Groups["n"].Value} ");

                sentence = Tidy(sentence, original);

                if (!sentence.Trim().Equals(original.Trim(), StringComparison.Ordinal))
                    removed.Add($"trimmed: {original.Trim()}");

                /*  A mistyped figure is repaired, not deleted. [DIAGNOSED 2026-09-21] The model
                    wrote "2,709" where the fact says 3709, and "2,347" the run before: single-digit
                    slips while re-typing 4-digit figures out of a large JSON input. Deleting those
                    sentences would throw away the most substantial line in the email over one
                    character, which defeats the point of sending it.                            */
                if (prompt is not null)
                    sentence = CorrectTranscriptionSlips(sentence, prompt, removed);

                /*  Untrue beats unpolished: a sentence carrying a figure, name or consequence the
                    data does not support is removed outright. This is what makes fabrication
                    impossible WITHOUT costing a rejection - the other true sentences still ship. */
                var untrue = prompt is null ? [] : FreeMonthlyDigestValidator.SentenceProblems(sentence, prompt);

                /*  [FOUND LIVE on tenant 5, 2026-09-21] Before deleting, see whether the untruth is
                    confined to a trailing clause. The model wrote "Of those, 302 have been overdue
                    for more than 90 days, so nearly all of the backlog has been carried for over
                    three months" - true up to the comma, and "three" is a figure no fact carries,
                    so the whole sentence went and the paragraph was left as the bare stub "304
                    obligations are overdue today." Trimming the clause keeps the grounded half and
                    still removes the invented number, which is the same least-destructive-first
                    principle TrailingInference already applies one step above.                   */
                if (untrue.Count > 0 && prompt is not null && TrimUnsupportedTail(sentence, prompt, original) is { } grounded)
                {
                    removed.Add($"trimmed unsupported tail: {sentence.Trim()}");
                    sentence = grounded;
                    untrue = FreeMonthlyDigestValidator.SentenceProblems(sentence, prompt);
                }

                if (untrue.Count > 0)
                {
                    removed.Add($"UNSUPPORTED ({string.Join("; ", untrue)}): {sentence.Trim()}");
                    continue;
                }

                /*  \b only works where the phrase ends in a word character. "overall," never
                    matched, so "Overall, the standing position shows areas requiring attention"
                    reached a real email - the boundary is applied per end instead.            */
                var offender = RemoveTheSentence.FirstOrDefault(p => Regex.IsMatch(sentence, Bounded(p), RegexOptions.IgnoreCase));
                if (offender is not null)
                {
                    removed.Add($"'{offender}': {sentence.Trim()}");
                    continue;
                }

                if (!HasWords(sentence))
                    continue;

                /*  The same point, said twice. Wording alone identifies it - the figures differ.

                    [CONSIDERED AND REJECTED 2026-09-21] Adding the figures to this key was proposed
                    on the reasoning that "302 of 304" and "3,709 of 3,830" are different subjects.
                    They are not, in practice: RemovesASentenceThatRepeatsAnEarlierPoint documents
                    that exact pair arriving in one live draft as padding, the second sentence
                    restating the first about a subset. Keep the key on wording. The tenant-5
                    hollow paragraph it was meant to fix had a different cause entirely - an
                    unsupported figure in a trailing clause, handled by TrimUnsupportedTail.     */
                var shape = Shape(sentence);
                if (shape.Length > 0 && !seen.Add(shape))
                {
                    removed.Add($"repeats an earlier sentence: {sentence.Trim()}");
                    continue;
                }

                /*  EACH consequence once - not one consequence in total. [FOUND LIVE 2026-09-21]
                    Counting them together deleted a licence's "no valid licence on record" because
                    a liability sentence had already used the single allowance, and on one email the
                    explanation phrase "standing position" consumed it before either. An email may
                    legitimately carry a liability point AND a licence point; what it may not do is
                    make the SAME point twice.                                                     */
                /*  A NAME IS STATED AT MOST TWICE. [FOUND LIVE on tenant 1082, 2026-09-22] The
                    Licence email named one licence in three paragraphs and twice inside the first
                    one - "Motor Vehicle Pollution under Control has expired... Motor Vehicle
                    Pollution under Control at Khavda remains expired..." - which reads as three
                    problems when there is one. Naming is the email's scarcest currency: it gets
                    at most two named things, and repeating one crowds out the other.

                    The third sentence to carry a name goes. By then the reader has been told
                    which licence twice; a third mention is restatement, not information.       */
                var repeatedName = Placeholders().Matches(sentence)
                    .Select(m => m.Value)
                    .Where(p => p.StartsWith("{{NAME_", StringComparison.Ordinal))
                    .FirstOrDefault(p => namesStated.TryGetValue(p, out var seen) && seen >= MaxNameMentions);

                if (repeatedName is not null)
                {
                    removed.Add($"names {repeatedName} a third time: {sentence.Trim()}");
                    continue;
                }

                foreach (var placeholder in Placeholders().Matches(sentence).Select(m => m.Value).Distinct())
                    if (placeholder.StartsWith("{{NAME_", StringComparison.Ordinal))
                        namesStated[placeholder] = namesStated.GetValueOrDefault(placeholder) + 1;

                var consequence = ApprovedConsequences.FirstOrDefault(c => sentence.Contains(c, StringComparison.OrdinalIgnoreCase));
                if (consequence is not null && !consequencesUsed.Add(consequence))
                {
                    removed.Add($"repeats '{consequence}': {sentence.Trim()}");
                    continue;
                }

                kept.Add(sentence);
            }

            if (kept.Count > 0)
                paragraphs.Add(string.Join(" ", kept.Select(s => s.Trim())));
        }

        return new RepairedDraft(string.Join("\n\n", paragraphs), removed);
    }

    /// <summary>
    /// Repairs a number the model mistyped, and ONLY that.
    ///
    /// <para>A figure qualifies as a slip when it is not in the data, but exactly one permitted
    /// value has the same number of digits and differs in exactly one position. "2709" against a
    /// data set holding 3709 is that case. The correction is then certain: there is one candidate,
    /// it is the same magnitude, and the surrounding prose came from that same fact.</para>
    ///
    /// <para>Anything else is left alone for the caller to delete - two candidates means guessing
    /// which the model meant, and a different digit-length means it is not a typing slip at all.
    /// The bar is deliberately narrow: this is the only place in the pipeline where a number is
    /// written by code rather than copied from SQL, so it must never be a judgement call.</para>
    /// </summary>
    private static string CorrectTranscriptionSlips(string sentence, FreeMonthlyDigestPrompt prompt, List<string> log)
    {
        var stripped = Placeholders().Replace(sentence, "ph");

        foreach (Match m in NumberToken().Matches(stripped))
        {
            var written = m.Value.Replace(",", string.Empty);
            if (!int.TryParse(written, out var value) || prompt.AllowedNumbers.Contains(value))
                continue;

            var candidates = prompt.AllowedNumbers
                .Select(a => a.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Where(a => a.Length == written.Length && a.Zip(written).Count(p => p.First != p.Second) == 1)
                .ToList();

            if (candidates.Count != 1)
                continue;

            log.Add($"corrected mistyped figure {m.Value} -> {candidates[0]}");
            sentence = Regex.Replace(sentence, $@"\b{Regex.Escape(m.Value)}\b", candidates[0]);
        }

        return sentence;
    }

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+")]
    private static partial Regex NumberToken();

    /// <summary>
    /// Closes up after a cut: collapses doubled spaces and spaces before punctuation, and restores
    /// the sentence's terminator if the cut took it. Never adds a word.
    /// </summary>
    private static string Tidy(string sentence, string original)
    {
        /*  A cut can leave punctuation stranded at either end. [FOUND LIVE on tenant 5] a sentence
            reached a real email as ", Adinath Kothare holds 147 of these overdue items."       */
        /*  [FOUND LIVE on tenant 1082, 2026-09-22] "...carry personal criminal liability,." reached
            a finished email. A cut that removes a trailing clause takes the words but leaves the
            comma that introduced it, and the checks below only tidy a sentence that does NOT
            already end in a terminator - so a stranded comma immediately before one survived.   */
        var text = Whitespace().Replace(sentence, " ").Replace(" ,", ",").Replace(" .", ".").TrimEnd();
        text = StrandedComma().Replace(text, "$1");
        text = text.TrimStart().TrimStart(',', ';', ':', '-', ' ');
        if (text.Length > 0 && char.IsLower(text[0]) && sentence.TrimStart() is { Length: > 0 } s && !char.IsLower(s[0]))
            text = char.ToUpperInvariant(text[0]) + text[1..];

        if (!HasWords(text))
            return text;

        if (text.Length > 0 && text[^1] is not ('.' or '!' or '?'))
        {
            var terminator = original.TrimEnd() is { Length: > 0 } t && t[^1] is '.' or '!' or '?' ? t[^1] : '.';
            text = text.TrimEnd(',', ';', ':', ' ') + terminator;
        }

        return text.StartsWith(' ') ? text : " " + text.TrimStart();
    }

    private static bool HasWords(string text) => text.Any(char.IsLetterOrDigit);

    /// <summary>
    /// True when the inference trim would cut the verb out of a "with ..." / "including ..." clause
    /// and leave a fragment behind. The sentence is then kept whole rather than broken.
    /// </summary>
    private static bool DanglingIfTrimmed(string sentence) =>
        TrailingInference().Match(sentence) is { Success: true } m
        && ClauseNeedingItsParticiple().IsMatch(sentence[..m.Index]);

    /// <summary>A trailing "with N of the M people who..." - a subject still waiting for its verb.</summary>
    [GeneratedRegex(@",\s*(with|including)\b[^,]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseNeedingItsParticiple();

    /// <summary>
    /// Drops trailing comma-separated clauses, one at a time, and returns the first result that the
    /// data fully supports - or <c>null</c> when no prefix of the sentence is clean, in which case
    /// the caller deletes it as before.
    ///
    /// <para>Two guards keep this from turning a deletion into a worse fragment: the surviving text
    /// must still carry a figure (a clause-less "Of those" says nothing), and the first clause is
    /// never dropped, because a sentence whose OPENING claim is untrue is not salvageable by
    /// trimming - that is a fabrication, and it must still be deleted outright.</para>
    /// </summary>
    private static string? TrimUnsupportedTail(string sentence, FreeMonthlyDigestPrompt prompt, string original)
    {
        /*  Split on clause commas ONLY. A thousands separator is a comma too, so a bare Split(',')
            cuts "3,709 items are overdue" into "3" and "709 items are overdue" - and "3" passes a
            digit guard, so a prefix carrying a number that was never in the data could ship. A
            clause comma is always followed by a space; a separator inside a figure never is.    */
        var clauses = ClauseComma().Split(sentence);
        if (clauses.Length < 2)
            return null;

        for (var keep = clauses.Length - 1; keep >= 1; keep--)
        {
            var candidate = Tidy(string.Join(", ", clauses.Take(keep)), original);
            if (!candidate.Any(char.IsDigit))
                continue;

            if (FreeMonthlyDigestValidator.SentenceProblems(candidate, prompt).Count == 0)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The distinctive fragments of the approved consequence sentences (shared Rule 6b). Each may be
    /// stated once per email; a second use is the model reaching for a phrase instead of a fact.
    /// </summary>
    /// <summary>How often one name may be stated before a further mention is restatement.</summary>
    private const int MaxNameMentions = 2;

    /// <summary>
    /// Stock phrases that place a figure in the whole scope. Useful once or twice, a stammer after.
    /// </summary>
    private static readonly string[] ScopingPhrases =
    [
        "across your organisation", "across your scope", "in your scope", "across the organisation",
    ];

    private const int MaxScopingPhraseUses = 2;

    /// <summary>
    /// Phrases that carry real weight and lose it by repetition. The short form is what a person
    /// would say on the third mention - it refers back rather than restating.
    /// </summary>
    private static readonly (string Phrase, string ShortForm)[] HeavyPhrases =
    [
        ("personal criminal liability", "that liability"),
        ("no renewal in progress", "no renewal"),
        ("no renewal filed", "no renewal"),

        /*  The procs' own comparison wording. It is accurate and it is also the only phrase they
            give for "worse than the rest", so it arrives on every detector that compares a rate -
            and the model repeats it verbatim. Three of these in one email reads as one observation
            made three times, which is the opposite of what three separate findings deserve.     */
        /*  The article is INSIDE the phrase, and only one form of each is listed. Both matter:
            "an unusually large share" -> "an larger share..." is broken English, and listing the
            bare form as well would count the same words twice and shorten on the second mention
            rather than the third.                                                               */
        ("an unusually large share", "a larger share than most"),
        ("an unusually high share", "a higher share than most"),
    ];

    private const int MaxHeavyPhraseUses = 2;

    private static readonly string[] ApprovedConsequences =
    [
        "no valid licence on record",
        "no one is assigned to these",
        "has been recorded against",
        "can no longer act",
        "if that person is unavailable",
        "standing position",
        "not last month",
    ];

    /// <summary>
    /// A sentence's wording with its figures and names stripped out, so two sentences that make the
    /// same point about different numbers collapse to the same shape. Placeholders and digits go;
    /// everything else lowercases. "3,709 of 3,830 have been overdue for more than 90 days" and
    /// "302 of 304 have been overdue for more than 90 days" share a shape, and the second is filler.
    /// </summary>
    private static string Shape(string sentence)
    {
        var text = Placeholders().Replace(sentence, " ");
        text = Digits().Replace(text, " ");
        text = NonWord().Replace(text, " ");
        return Whitespace().Replace(text, " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"[\d,]+")]
    private static partial Regex Digits();

    /// <summary>
    /// "3 of the 3", "2 of its 2", "1 of the 1" - the same figure as part and as whole.
    /// </summary>
    /// <summary>"A further", "Another", "In addition" opening a sentence - an addition nobody computed.</summary>
    [GeneratedRegex(@"(?<=^|[.!?]\s)(a further|another|in addition,|additionally,|a total of)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AdditiveOpener();

    /// <summary>A scoping phrase that completes a comparison - "compared with 21% across your scope".</summary>
    [GeneratedRegex(@"\b(compared with|against|versus|vs\.?)\b[^.!?]*\b(across|in) (your|the) (organisation|scope)", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonScope();

    /*  The determiner is OPTIONAL: "1 of 1 licences" reached a customer email because the pattern
        required "of the 1". Both forms say the same useless thing.                              */
    [GeneratedRegex(@"\b(?<n>\d+) of (the |its |their |your )?\k<n>\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SamePartAndWhole();

    /// <summary>
    /// An "as at &lt;date&gt;" qualifier, before or after binding, with its trailing comma.
    /// </summary>
    [GeneratedRegex(@"\s*\bas at (\{\{AS_AT\}\}|\d{1,2} [A-Za-z]{3} \d{4})\s*,?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AsAtPrefix();

    /// <summary>A comma or semicolon left immediately before a sentence terminator by a cut.</summary>
    [GeneratedRegex(@"\s*[,;:]+\s*([.!?])")]
    private static partial Regex StrandedComma();

    /// <summary>A comma that ends a clause - one followed by whitespace, never a thousands separator.</summary>
    [GeneratedRegex(@",(?=\s)")]
    private static partial Regex ClauseComma();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWord();

    /// <summary>
    /// Word boundaries only where the phrase actually has a word edge. <c>\b</c> before a leading
    /// non-word character, or after a trailing one, can never match - which is how "overall," and
    /// "in summary," slipped through into a sent email.
    /// </summary>
    private static string Bounded(string phrase)
    {
        var escaped = Regex.Escape(phrase);
        var start = char.IsLetterOrDigit(phrase[0]) ? @"\b" : string.Empty;
        var end = char.IsLetterOrDigit(phrase[^1]) ? @"\b" : string.Empty;
        return start + escaped + end;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Splits on sentence end followed by whitespace. Keeps the terminator with its sentence, and
    /// does not split on a decimal point because there are none - every input value is whole.
    /// </summary>
    private static IEnumerable<string> SplitSentences(string paragraph)
    {
        var start = 0;
        foreach (Match m in SentenceEnd().Matches(paragraph))
        {
            yield return paragraph[start..(m.Index + 1)];
            start = m.Index + m.Length;
        }

        if (start < paragraph.Length)
            yield return paragraph[start..];
    }

    [GeneratedRegex(@"[.!?]\s+")]
    private static partial Regex SentenceEnd();
}

/// <param name="Body">The draft with the offending sentences removed.</param>
/// <param name="Removed">What was taken out and why - logged so prompts can be tuned against it.</param>
public sealed record RepairedDraft(string Body, IReadOnlyList<string> Removed);

/// <summary>
/// A reviewed draft. <paramref name="FailedChecks"/> mean it is wrong and must not be sent;
/// <paramref name="Advisories"/> mean it is true but imperfect, and are logged for prompt tuning.
/// </summary>
public sealed record FreeMonthlyReview(bool IsValid, IReadOnlyList<string> FailedChecks, IReadOnlyList<string> Advisories);

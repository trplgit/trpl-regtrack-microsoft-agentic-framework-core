using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The two lines of text on an insight card, after validation and placeholder binding.
/// <see cref="Source"/> is <c>llm</c> or <c>fallback</c>; <see cref="Reason"/> says why a draft
/// was not used. <see cref="NumbersVerified"/> is true only when the text that ships passed the
/// closed-set check - since 2026-09-27 the fallback is checked too, rather than asserted.
/// </summary>
public sealed record InsightCardText(
    string Headline, string Narrative, string Source, string? Reason, bool NumbersVerified,
    int InputTokens, int OutputTokens, string RawDraft);

/// <summary>
/// One card draft from one model call, validated, still in PLACEHOLDER form.
/// <see cref="FailureReason"/> is null when the draft passed every card check.
/// </summary>
public sealed record InsightCardDraft(
    string? Headline, string? Narrative, string? FailureReason, int InputTokens, int OutputTokens, string RawText)
{
    public bool IsValid => FailureReason is null;
}

/// <summary>
/// Writes the insight card's headline and narrative (ADR-0004) from the JSON lane's own closed
/// input - <see cref="InsightCardInput.UserMessage"/> - with its own short prompt
/// (07b_insight_card.md). A rejected or skipped draft falls back to a deterministic build from the
/// headline fact, never throws.
///
/// <para>Validated with <see cref="FreeMonthlyDigestValidator.ProseProblems"/> against the data
/// layer's closed sets (<see cref="InsightCardInput.Guardrails"/>), NOT <see cref="FreeDigestValidator"/>:
/// the old weekly validator bans the words this lane must use (spec 8.1).</para>
///
/// <para>Split into <see cref="DraftAsync"/> (one call, validated, placeholder form),
/// <see cref="Finish"/> (bind) and <see cref="Fallback"/>; <see cref="WriteAsync"/> is those three
/// in sequence.</para>
/// </summary>
public sealed partial class InsightCardWriter(IClaudeClient client, IPromptLoader promptLoader)
{
    public const string PromptFileName = "07b_insight_card.md";

    private const double CharsPerToken = 4.0;
    private const int MinCompletionTokens = 150;
    private const int MaxCompletionTokens = 600;

    /// <summary>
    /// The narrative is two or three sentences by contract; a fourth is a rejected draft, not a
    /// trimmed one. [RAISED 2026-09-25] Two sentences forced the model to drop the period and cram a
    /// second finding into a clause ("..., while obligations are not configured at Finance"); the
    /// third sentence gives each its own complete statement.
    /// </summary>
    public const int MinNarrativeSentences = 2;
    public const int MaxNarrativeSentences = 3;

    public async Task<InsightCardText> WriteAsync(InsightCardInput input, int tokenCap, CancellationToken cancellationToken = default)
    {
        var draft = await DraftAsync(input, input.UserMessage, tokenCap, cancellationToken);
        return draft.IsValid
            ? Finish(input, draft.Headline!, draft.Narrative!, draft.InputTokens, draft.OutputTokens, draft.RawText)
            : Fallback(input, draft.FailureReason!, draft.InputTokens, draft.OutputTokens, draft.RawText);
    }

    /// <summary>
    /// One capped model call and the card checks. <paramref name="userMessage"/> is the card input.
    /// </summary>
    public async Task<InsightCardDraft> DraftAsync(InsightCardInput input, string userMessage, int tokenCap, CancellationToken cancellationToken = default)
    {
        var prompt = input.Guardrails;
        var systemPrompt = await promptLoader.LoadAsync(PromptFileName, cancellationToken);
        var completionBudget = Math.Clamp(
            tokenCap - (int)Math.Ceiling((systemPrompt.Length + userMessage.Length) / CharsPerToken),
            MinCompletionTokens, MaxCompletionTokens);

        var result = await client.CompleteAsync(systemPrompt, userMessage, completionBudget, cancellationToken);
        var total = result.InputTokens + result.OutputTokens;

        InsightCardDraft Fail(string reason) => new(null, null, reason, result.InputTokens, result.OutputTokens, result.Text);

        if (result.WasTruncated)
            return Fail("response truncated at the token cap");
        if (total > tokenCap)
            return Fail($"token usage {total} exceeded cap {tokenCap}");

        var parsed = Parse(result.Text);
        if (parsed is null)
            return Fail("response was not a JSON object with headline and narrative");

        // Plain text only: the hub renders these as-is, so the model's own emphasis markers are removed, never rejected.
        var headline = Clean(parsed.Value.Headline);
        var narrative = Clean(parsed.Value.Narrative);

        var problems = CardProblems(headline, narrative, prompt);
        if (problems.Count > 0)
            return Fail("validator rejected the draft: " + string.Join("; ", problems));

        return new InsightCardDraft(headline, narrative, null, result.InputTokens, result.OutputTokens, result.Text);
    }

    /// <summary>A validated placeholder-form card, bound and made plain for the hub.</summary>
    public static InsightCardText Finish(InsightCardInput input, string headline, string narrative, int inputTokens, int outputTokens, string rawDraft) =>
        // The binder emphasises names for the email ("**A**"); the hub renders plain text.
        new(PlainText(FreeMonthlyPlaceholderBinder.Bind(FreeTierReaderTerms.ForPeople(headline.Trim(), input.Guardrails), input.Guardrails.Bindings)),
            PlainText(FreeMonthlyPlaceholderBinder.Bind(FreeTierReaderTerms.ForPeople(narrative.Trim(), input.Guardrails), input.Guardrails.Bindings)),
            "llm", null, NumbersVerified: true, inputTokens, outputTokens, rawDraft);

    /// <summary>The deterministic card text, with the given reason. Never throws.</summary>
    public static InsightCardText Fallback(InsightCardInput input, string reason, int inputTokens, int outputTokens, string rawDraft)
    {
        var fallback = InsightCardFallback.BuildChecked(input.Guardrails, input.NamedFindings);
        var why = fallback.UsedFloor ? $"{reason}; send-quality fallback refused ({string.Join("; ", fallback.FloorReasons)}), legacy floor used" : reason;
        return new InsightCardText(fallback.Headline, fallback.Narrative, "fallback", why, NumbersVerified: !fallback.UsedFloor, inputTokens, outputTokens, rawDraft);
    }

    /// <summary>
    /// Every card rule: the closed-set prose checks on both fields, the headline figure or name in
    /// the headline, nothing empty, and a 2-3 sentence narrative. Shared by the model's drafts and
    /// the deterministic fallback, so both meet the same bar.
    /// </summary>
    internal static List<string> CardProblems(string headline, string narrative, FreeMonthlyDigestPrompt prompt)
    {
        var problems = new List<string>();
        problems.AddRange(FreeMonthlyDigestValidator.ProseProblems(headline, prompt).Select(p => "headline " + p));
        problems.AddRange(FreeMonthlyDigestValidator.ProseProblems(narrative, prompt).Select(p => "narrative " + p));

        if (prompt.HeadlineMarker is { } marker && !ContainsMarker(headline, marker))
            problems.Add($"headline does not state the headline figure ({marker})");

        if (headline.Trim().Length == 0 || narrative.Trim().Length == 0)
            problems.Add("headline or narrative is empty");

        problems.AddRange(FreeMonthlyDigestValidator.LicenceOverdueProblems(headline + " " + narrative).Select(p => "card " + p));
        problems.AddRange(FreeMonthlyDigestValidator.FragmentProblems(headline + " " + narrative).Select(p => "card " + p));

        var sentences = SentenceCount(narrative);
        if (sentences is < MinNarrativeSentences or > MaxNarrativeSentences)
            problems.Add($"narrative has {sentences} sentences, not {MinNarrativeSentences} to {MaxNarrativeSentences}");

        return problems;
    }

    private static string Clean(string text) =>
        text.Replace("**", string.Empty).Replace("per cent", "%", StringComparison.OrdinalIgnoreCase).Trim();

    internal static string PlainText(string text) => text.Replace("**", string.Empty).Trim();

    private static bool ContainsMarker(string headline, string marker)
    {
        if (marker.StartsWith("{{", StringComparison.Ordinal))
            return headline.Contains(marker, StringComparison.Ordinal);

        return NumberToken().Matches(headline).Any(m => m.Value.Replace(",", string.Empty) == marker);
    }

    /// <summary>Tolerates a code fence or prose around the object - the object itself must parse.</summary>
    internal static (string Headline, string Narrative)? Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? headline = null, narrative = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (property.Name.Equals("headline", StringComparison.OrdinalIgnoreCase))
                    headline = property.Value.GetString();
                else if (property.Name.Equals("narrative", StringComparison.OrdinalIgnoreCase))
                    narrative = property.Value.GetString();
            }

            return headline is null || narrative is null ? null : (headline, narrative);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sentences end at . ! or ? followed by whitespace and a capital or placeholder - "1,234." mid-number never splits.</summary>
    internal static int SentenceCount(string text) => SplitSentences(text).Count;

    /// <summary>The narrative's sentences, by the same rule <see cref="SentenceCount"/> counts with.</summary>
    internal static IReadOnlyList<string> SplitSentences(string text) =>
        SentenceBreak().Split(text.Trim()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z{])")]
    private static partial Regex SentenceBreak();
}

/// <summary>The card's deterministic text, and whether the send-quality build gave way to the legacy floor.</summary>
public sealed record InsightCardFallbackText(string Headline, string Narrative, bool UsedFloor, IReadOnlyList<string> FloorReasons);

/// <summary>
/// The deterministic card text.
///
/// <para><b>[REBUILT 2026-09-27, ADR 2026-09-27-free-digest-reflection-design Sec.4.13]</b> The old
/// build broke the card's own rules (ADR C12): it repeated the headline as the narrative when no
/// support fact existed, wrote a one-sentence narrative when one did, printed a raw detector name,
/// and asserted <c>NumbersVerified</c> without checking. Now:</para>
/// <list type="bullet">
/// <item>the headline is the lead finding's <see cref="DetectorSentences"/> sentence, or the headline
/// fact from its own label;</item>
/// <item>the narrative is always 2-3 sentences - other named findings, then the most material
/// facts, each opening with a capital so the card's sentence rule counts it - and never the headline
/// again;</item>
/// <item>the result is checked with <see cref="InsightCardWriter.CardProblems"/>, the model's own bar,
/// before it is bound; if it fails, <see cref="BuildLegacy"/> ships as the floor and the caller is
/// told.</item>
/// </list>
/// </summary>
public static class InsightCardFallback
{
    /// <summary>Bound text only - for callers that need a well-formed card and nothing else.</summary>
    public static (string Headline, string Narrative) Build(FreeMonthlyDigestPrompt prompt)
    {
        var text = BuildChecked(prompt);
        return (text.Headline, text.Narrative);
    }

    /// <param name="named">The card's own named findings (InsightCardInput.NamedFindings drops those that read the same on a card); the guardrails' list when null.</param>
    public static InsightCardFallbackText BuildChecked(FreeMonthlyDigestPrompt prompt, IReadOnlyList<MonthlyNamedFinding>? named = null)
    {
        named ??= prompt.NamedFindings;
        List<string> problems;
        try
        {
            var (headline, narrative) = Draft(prompt, named);

            problems = headline is null || narrative is null
                ? new List<string> { "no headline sentence could be stated" }
                : InsightCardWriter.CardProblems(headline, narrative, prompt);

            if (problems.Count == 0)
                return new InsightCardFallbackText(
                    InsightCardWriter.PlainText(FreeMonthlyPlaceholderBinder.Bind(headline!, prompt.Bindings)),
                    InsightCardWriter.PlainText(FreeMonthlyPlaceholderBinder.Bind(narrative!, prompt.Bindings)),
                    false, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Runs after billed calls; an escaping exception would re-bill them on the activity retry.
            problems = [$"{ex.GetType().Name}: {ex.Message}"];
        }

        var (legacyHeadline, legacyNarrative) = BuildLegacy(prompt);
        return new InsightCardFallbackText(InsightCardWriter.PlainText(legacyHeadline), InsightCardWriter.PlainText(legacyNarrative), true, problems);
    }

    /// <summary>Placeholder form, before validation and binding.</summary>
    internal static (string? Headline, string? Narrative) Draft(FreeMonthlyDigestPrompt prompt, IReadOnlyList<MonthlyNamedFinding> named)
    {
        var data = prompt.Data;
        var facts = data.Facts.OrderBy(f => f.DisplayOrder).ToList();
        var headlineFact = facts.FirstOrDefault(f => f.IsHeadline);
        var candidateLeads = string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal);
        var leadFinding = candidateLeads ? named.FirstOrDefault(n => n.Slot == 1) : null;

        bool Fits(string sentence) => FreeMonthlyDigestValidator.ProseProblems(sentence, prompt).Count == 0;

        string? headline = null;
        if (leadFinding is not null && DetectorSentences.For(leadFinding, prompt) is { } findingHeadline && Fits(findingHeadline))
            headline = findingHeadline;
        else if (headlineFact is not null && FactSentence(headlineFact, facts, data.Edition.Slot) is var factHeadline && Fits(factHeadline))
            headline = factHeadline;

        if (headline is null)
            return (null, null);

        var narrative = new List<string>();
        void Add(string? sentence)
        {
            if (sentence is null || narrative.Count >= InsightCardWriter.MaxNarrativeSentences || !Fits(sentence)
                || string.Equals(sentence, headline, StringComparison.Ordinal) || narrative.Contains(sentence, StringComparer.Ordinal)
                || InsightCardWriter.SentenceCount(sentence) != 1)
                return;
            narrative.Add(sentence);
        }

        foreach (var finding in named.Where(n => !ReferenceEquals(n, leadFinding)))
            Add(DetectorSentences.For(finding, prompt));

        foreach (var f in facts
                     .Where(f => !f.IsHeadline && f.FactValue > 0 && f.SeverityTier <= 3 && f.ImpactClass != "volume" && f.WindowScope != "ctx")
                     .Where(f => prompt.AllowedNumbers.Contains(f.FactValue))
                     .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder))
            Add(PeriodSentence(f, facts, data.Edition.Slot));

        // Never below the card's two-sentence floor: the date the figures stand at is always true.
        if (narrative.Count < InsightCardWriter.MinNarrativeSentences)
            Add("These figures are as of {{AS_AT}}.");
        if (narrative.Count < InsightCardWriter.MinNarrativeSentences)
            Add("Your monthly email for {{CURR_MONTH}} carries the full picture behind this card.");

        return (headline, string.Join(" ", narrative));
    }

    /// <summary>
    /// A support fact that opens with a capital, so the card's sentence rule counts it: "Of the N
    /// ..., M ..." when it nests, otherwise the fact prefixed with the period it covers.
    /// </summary>
    private static string PeriodSentence(MonthlyFact fact, IReadOnlyList<MonthlyFact> facts, MonthlyDigestSlot slot)
    {
        var sentence = FactSentence(fact, facts, slot);
        if (sentence.Length > 0 && char.IsUpper(sentence[0]))
            return sentence;

        var lead = fact.WindowScope switch
        {
            // [2026-10-08] The email's own section phrases, so card and email read alike.
            "prev" => "In {{PREV_MONTH}}, ",
            "curr" when fact.Section == "rest_of_month" || fact.FactKey.StartsWith("rm_", StringComparison.Ordinal) => "Before the end of {{CURR_MONTH}}, ",
            "curr" => "So far in {{CURR_MONTH}}, ",
            _ when fact.FactKey.StartsWith("lic", StringComparison.Ordinal) => "Currently, ",
            _ => "In total, from all months, ",
        };
        return lead + sentence;
    }

    private static string FactSentence(MonthlyFact fact, IReadOnlyList<MonthlyFact> facts, MonthlyDigestSlot slot)
    {
        var label = Plain(fact.DisplayLabel);
        var value = InsightCardRules.Count(fact.FactValue);

        if (label.StartsWith("of those", StringComparison.OrdinalIgnoreCase))
        {
            // [2026-10-07] The fact's real parent, not the first base in its section (t_rm_liability was read under t_lm_due).
            var sectionBase = FreeMonthlyDigestPrompt.ParentOf(fact, facts);
            var rest = label["of those".Length..].Trim();
            return sectionBase is not null
                ? $"Of the {InsightCardRules.Count(sectionBase.FactValue)} {Plain(sectionBase.DisplayLabel)}, {value} {rest}."
                : $"{value} {InsightCardRules.UnitForFact(fact.FactKey, slot)} {rest}.";
        }

        if (label.StartsWith("of ", StringComparison.OrdinalIgnoreCase))
            return $"{value} {InsightCardRules.UnitForFact(fact.FactKey, slot)} {label}.";

        return $"{value} {label}.";
    }

    /// <summary>
    /// The pre-2026-09-27 card fallback, kept UNCHANGED as the floor under <see cref="BuildChecked"/>
    /// (bound text). It ships only when the send-quality build fails its own checks.
    /// </summary>
    public static (string Headline, string Narrative) BuildLegacy(FreeMonthlyDigestPrompt prompt)
    {
        var data = prompt.Data;
        var facts = data.Facts.OrderBy(f => f.DisplayOrder).ToList();
        var headlineFact = facts.FirstOrDefault(f => f.IsHeadline);
        var headlineFinding = prompt.NamedFindings.FirstOrDefault(n => n.Slot == 1);

        string headline;
        if (string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal) && headlineFinding is not null)
            headline = LegacyFindingSentence(headlineFinding, prompt);
        else if (headlineFact is not null)
            headline = FactSentence(headlineFact, facts, data.Edition.Slot);
        else
            headline = "No material change to report this week.";

        var support = facts
            .Where(f => !f.IsHeadline && f.FactValue > 0 && f.SeverityTier <= 3 && f.ImpactClass != "volume")
            .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder)
            .Take(2)
            .Select(f => FactSentence(f, facts, data.Edition.Slot))
            .ToList();

        var narrative = support.Count > 0
            ? string.Join(" ", support)
            : headline;

        return (headline, narrative);
    }

    private static string LegacyFindingSentence(MonthlyNamedFinding finding, FreeMonthlyDigestPrompt prompt)
    {
        var c = finding.Candidate;
        var name = finding.NamePlaceholder is { } p && prompt.Bindings.TryGetValue(p, out var bound) ? bound : $"One {c.EntityKind}";
        var site = finding.AtPlaceholder is { } at && prompt.Bindings.TryGetValue(at, out var boundAt) ? $" at your {boundAt} site" : string.Empty;
        var date = finding.DatePlaceholder is { } d && prompt.Bindings.TryGetValue(d, out var boundDate) ? $" on {boundDate}" : string.Empty;
        var unit = InsightCardRules.UnitForDetector(c.Detector);

        return c.Detector switch
        {
            "overdue_concentration" when c.ItemCount is { } item && c.BaseCount is { } whole =>
                $"{name} holds {InsightCardRules.Count(item)} of the {InsightCardRules.Count(whole)} {unit} in the standing backlog across your organisation.",
            "last_month_slippage" when c.ItemCount is { } item && c.BaseCount is { } whole =>
                $"{name} still has {InsightCardRules.Count(item)} of the {InsightCardRules.Count(whole)} obligations that fell due there in {prompt.Bindings["{{PREV_MONTH}}"]} open as at {prompt.Bindings["{{AS_AT}}"]}.",
            "liability_share" when c.ItemCount is { } item && c.BaseCount is { } whole =>
                $"At {name}, {InsightCardRules.Count(item)} of its {InsightCardRules.Count(whole)} overdue obligations carry personal criminal liability for the responsible officer.",
            "licence_expiring_unrenewed" =>
                $"{name} expires{date}{site} with no renewal filed.",
            "licence_lapsed_recent_unrenewed" =>
                $"{name} expired{date}{site} and still has no renewal in progress.",
            "single_point_of_failure" when c.ItemCount is { } item =>
                $"At {name}, all {InsightCardRules.Count(item)} open obligations rest on one person.",
            "deactivated_owner" when c.ItemCount is { } item =>
                $"{InsightCardRules.Count(item)} open obligations are held by {name}, who is no longer an active user of RegTrack.",
            _ when c.ItemCount is { } item =>
                $"{name}: {InsightCardRules.Count(item)} {unit} {InsightCardRules.ShortNounForDetector(c.Detector)}.".Replace(" .", "."),
            _ => $"{name}: {c.Detector.Replace('_', ' ')}.",
        };
    }

    /// <summary>
    /// The proc labels are definitions, written for the model; the fallback prints them to a
    /// manager, so the backlog wording is put the way the card prompt (07b) asks the model to put it.
    /// </summary>
    internal static string Plain(string label) =>
        InsightCardInput.Plain(label)
            .Replace("make up the standing backlog - overdue now, whatever date each was originally due", "are overdue across your organisation", StringComparison.OrdinalIgnoreCase)
            .Replace("of that standing backlog", "in the overdue backlog", StringComparison.OrdinalIgnoreCase)
            .Replace("the standing backlog", "the overdue backlog", StringComparison.OrdinalIgnoreCase);
}

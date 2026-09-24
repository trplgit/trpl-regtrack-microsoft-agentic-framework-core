using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The two lines of text on an insight card, after validation and placeholder binding.
/// <see cref="Source"/> is <c>llm</c> or <c>fallback</c>; <see cref="Reason"/> says why a draft
/// was not used. <see cref="NumbersVerified"/> is true only when the text that ships passed the
/// closed-set check - a fallback is built from proc values and passes by construction.
/// </summary>
public sealed record InsightCardText(
    string Headline, string Narrative, string Source, string? Reason, bool NumbersVerified,
    int InputTokens, int OutputTokens, string RawDraft);

/// <summary>
/// Writes the insight card's headline and narrative (ADR-0004) from the JSON lane's own closed
/// input - <see cref="InsightCardInput.UserMessage"/> - with its own short prompt
/// (07b_insight_card.md). One capped, non-reflective call; a rejected or skipped draft falls back
/// to a deterministic build from the headline fact, never throws.
///
/// <para>Validated with <see cref="FreeMonthlyDigestValidator.ProseProblems"/> against the data
/// layer's closed sets (<see cref="InsightCardInput.Guardrails"/>), NOT <see cref="FreeDigestValidator"/>:
/// the old weekly validator bans the words this lane must use (spec 8.1).</para>
/// </summary>
public sealed partial class InsightCardWriter(IClaudeClient client, IPromptLoader promptLoader)
{
    public const string PromptFileName = "07b_insight_card.md";

    private const double CharsPerToken = 4.0;
    private const int MinCompletionTokens = 150;
    private const int MaxCompletionTokens = 600;

    /// <summary>The narrative is two sentences by contract; a third is a rejected draft, not a trimmed one.</summary>
    public const int NarrativeSentences = 2;

    public async Task<InsightCardText> WriteAsync(InsightCardInput input, int tokenCap, CancellationToken cancellationToken = default)
    {
        var prompt = input.Guardrails;
        var systemPrompt = await promptLoader.LoadAsync(PromptFileName, cancellationToken);
        var completionBudget = Math.Clamp(
            tokenCap - (int)Math.Ceiling((systemPrompt.Length + input.UserMessage.Length) / CharsPerToken),
            MinCompletionTokens, MaxCompletionTokens);

        var result = await client.CompleteAsync(systemPrompt, input.UserMessage, completionBudget, cancellationToken);
        var total = result.InputTokens + result.OutputTokens;

        if (result.WasTruncated)
            return Fallback(prompt, "response truncated at the token cap", result);
        if (total > tokenCap)
            return Fallback(prompt, $"token usage {total} exceeded cap {tokenCap}", result);

        var parsed = Parse(result.Text);
        if (parsed is null)
            return Fallback(prompt, "response was not a JSON object with headline and narrative", result);

        // Plain text only: the hub renders these as-is, so the model's own emphasis markers are removed, never rejected.
        var headline = parsed.Value.Headline.Replace("**", string.Empty).Replace("per cent", "%", StringComparison.OrdinalIgnoreCase).Trim();
        var narrative = parsed.Value.Narrative.Replace("**", string.Empty).Replace("per cent", "%", StringComparison.OrdinalIgnoreCase).Trim();
        var problems = new List<string>();
        problems.AddRange(FreeMonthlyDigestValidator.ProseProblems(headline, prompt).Select(p => "headline " + p));
        problems.AddRange(FreeMonthlyDigestValidator.ProseProblems(narrative, prompt).Select(p => "narrative " + p));

        if (prompt.HeadlineMarker is { } marker && !ContainsMarker(headline, marker))
            problems.Add($"headline does not state the headline figure ({marker})");

        if (headline.Trim().Length == 0 || narrative.Trim().Length == 0)
            problems.Add("headline or narrative is empty");

        var sentences = SentenceCount(narrative);
        if (sentences != NarrativeSentences)
            problems.Add($"narrative has {sentences} sentences, not {NarrativeSentences}");

        if (problems.Count > 0)
            return Fallback(prompt, "validator rejected the draft: " + string.Join("; ", problems), result);

        // The binder emphasises names for the email ("**A**"); the hub renders plain text.
        return new InsightCardText(
            PlainText(FreeMonthlyPlaceholderBinder.Bind(headline.Trim(), prompt.Bindings)),
            PlainText(FreeMonthlyPlaceholderBinder.Bind(narrative.Trim(), prompt.Bindings)),
            "llm", null, NumbersVerified: true, result.InputTokens, result.OutputTokens, result.Text);
    }

    private static InsightCardText Fallback(FreeMonthlyDigestPrompt prompt, string reason, ClaudeCompletionResult result)
    {
        var (headline, narrative) = InsightCardFallback.Build(prompt);
        return new InsightCardText(PlainText(headline), PlainText(narrative), "fallback", reason, NumbersVerified: true, result.InputTokens, result.OutputTokens, result.Text);
    }

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
    internal static int SentenceCount(string text) =>
        SentenceBreak().Split(text.Trim()).Count(s => s.Trim().Length > 0);

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z{])")]
    private static partial Regex SentenceBreak();
}

/// <summary>
/// The deterministic card text: the headline fact or finding stated from its own label, and a
/// narrative from the next most material fact. Every number is a proc value, every name is bound
/// from the same bindings the model would have used, so it passes the same checks by construction.
/// </summary>
public static class InsightCardFallback
{
    public static (string Headline, string Narrative) Build(FreeMonthlyDigestPrompt prompt)
    {
        var data = prompt.Data;
        var facts = data.Facts.OrderBy(f => f.DisplayOrder).ToList();
        var headlineFact = facts.FirstOrDefault(f => f.IsHeadline);
        var headlineFinding = prompt.NamedFindings.FirstOrDefault(n => n.Slot == 1);

        string headline;
        if (string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal) && headlineFinding is not null)
            headline = FindingSentence(headlineFinding, prompt);
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

    private static string FactSentence(MonthlyFact fact, IReadOnlyList<MonthlyFact> facts, MonthlyDigestSlot slot)
    {
        var label = fact.DisplayLabel.Replace("items", "obligations", StringComparison.OrdinalIgnoreCase).Replace("item", "obligation", StringComparison.OrdinalIgnoreCase).Trim();
        var value = InsightCardRules.Count(fact.FactValue);

        if (label.StartsWith("of those", StringComparison.OrdinalIgnoreCase))
        {
            var sectionBase = facts.FirstOrDefault(o => o.Section == fact.Section && o.DisplayOrder < fact.DisplayOrder
                                                        && !o.DisplayLabel.StartsWith("of", StringComparison.OrdinalIgnoreCase));
            var rest = label["of those".Length..].Trim();
            return sectionBase is not null
                ? $"Of the {InsightCardRules.Count(sectionBase.FactValue)} {sectionBase.DisplayLabel.Replace("items", "obligations", StringComparison.OrdinalIgnoreCase)}, {value} {rest}."
                : $"{value} {InsightCardRules.UnitForFact(fact.FactKey, slot)} {rest}.";
        }

        if (label.StartsWith("of ", StringComparison.OrdinalIgnoreCase))
            return $"{value} {InsightCardRules.UnitForFact(fact.FactKey, slot)} {label}.";

        return $"{value} {label}.";
    }

    private static string FindingSentence(MonthlyNamedFinding finding, FreeMonthlyDigestPrompt prompt)
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
            _ => $"{name}: {DetectorPlain(c.Detector)}.",
        };
    }

    private static string DetectorPlain(string detector) => detector.Replace('_', ' ');
}

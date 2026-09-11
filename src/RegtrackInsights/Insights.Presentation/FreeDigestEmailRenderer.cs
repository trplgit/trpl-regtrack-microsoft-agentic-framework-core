using System.Net;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Renders the free digest fallback body and the final HTML shell from
/// templates/digest_fallback.txt and templates/digest.html (Email:TemplatePath).
/// Templates are files, never string literals - same rule as prompts (docs/CONFIGURATION.md).
/// </summary>
public sealed partial class FreeDigestEmailRenderer(string templateDirectory)
{
    /// <summary>
    /// The placeholder written into a Sunday-generated artifact wherever the real, per-recipient
    /// unsubscribe link will go. Recipients sharing a scope group share one artifact, but the
    /// unsubscribe link is signed per (customer, user) - it cannot be baked in until the actual
    /// recipient is known, which only happens at Monday send time.
    /// </summary>
    public const string UnsubscribeSentinel = "__RTI_UNSUBSCRIBE_PENDING__";

    /// <summary>
    /// GENERATE-phase render: the full email shell with the unsubscribe link left as
    /// <see cref="UnsubscribeSentinel"/>. Asserts the sentinel appears EXACTLY ONCE before
    /// returning - fail closed (CLAUDE.md non-negotiable 2) rather than silently storing an
    /// artifact with no unsubscribe link (digest.html's Substitute replaces an unmatched token
    /// with an empty string, which would otherwise fail open).
    /// </summary>
    public async Task<string> RenderHtmlForArtifactAsync(
        string body, string tenantName, DateTime weekEnding, string upgradeUrl, CancellationToken cancellationToken = default)
    {
        var html = await RenderHtmlAsync(body, tenantName, weekEnding, upgradeUrl, UnsubscribeSentinel, cancellationToken);

        var occurrences = CountOccurrences(html, UnsubscribeSentinel);
        if (occurrences != 1)
            throw new InvalidOperationException(
                $"Digest artifact render produced {occurrences} occurrence(s) of the unsubscribe sentinel, expected exactly 1 - refusing to store an artifact with no (or an ambiguous) unsubscribe link.");

        return html;
    }

    /// <summary>
    /// SEND-phase transform: swaps the sentinel for one recipient's real, signed unsubscribe URL.
    /// Asserts the sentinel is present exactly once beforehand - anything else means the stored
    /// artifact was altered or corrupted since generation, and this recipient must not be mailed
    /// (ADR-0001 D9).
    /// </summary>
    public static string SubstituteUnsubscribeUrl(string artifactHtml, string realUnsubscribeUrl)
    {
        var occurrences = CountOccurrences(artifactHtml, UnsubscribeSentinel);
        if (occurrences != 1)
            throw new InvalidOperationException(
                $"Digest artifact has {occurrences} occurrence(s) of the unsubscribe sentinel, expected exactly 1 - refusing to send a possibly-altered artifact.");

        return artifactHtml.Replace(UnsubscribeSentinel, realUnsubscribeUrl, StringComparison.Ordinal);
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

    public async Task<string> RenderFallbackBodyAsync(FreeDigestAggregates aggregates, string? recipientName, DateTime weekEnding, CancellationToken cancellationToken = default)
    {
        var template = await ReadTemplateAsync("digest_fallback.txt", cancellationToken);
        return Substitute(template, TokensFor(aggregates, recipientName, weekEnding));
    }

    public async Task<string> RenderHtmlAsync(string body, string tenantName, DateTime weekEnding, string upgradeUrl, string unsubscribeUrl, CancellationToken cancellationToken = default)
    {
        var template = await ReadTemplateAsync("digest.html", cancellationToken);
        var tokens = new Dictionary<string, string>
        {
            ["Body"] = FormatBody(body),
            ["TenantName"] = tenantName,
            ["WeekEnding"] = weekEnding.ToString("d MMM yyyy"),
            ["UpgradeUrl"] = upgradeUrl,
            ["UnsubscribeUrl"] = unsubscribeUrl,
        };
        return Substitute(template, tokens);
    }

    /// <summary>
    /// The body is LLM output (or the deterministic fallback) inserted straight into an HTML
    /// email - it was never HTML-encoded, so any literal "&lt;"/"&amp;" in a composed sentence
    /// (or a prompt-injected tag) would have rendered as live markup. Encode first, then turn the
    /// prompt's **bold** markdown into &lt;strong&gt; - safe to do in that order because encoding
    /// leaves the ASCII asterisks untouched.
    /// </summary>
    private static string FormatBody(string body) =>
        BoldMarkdown().Replace(WebUtility.HtmlEncode(body), "<strong>$1</strong>");

    private Task<string> ReadTemplateAsync(string fileName, CancellationToken cancellationToken)
    {
        var root = Path.IsPathRooted(templateDirectory) ? templateDirectory : Path.Combine(AppContext.BaseDirectory, templateDirectory);
        return File.ReadAllTextAsync(Path.Combine(root, fileName), cancellationToken);
    }

    private static Dictionary<string, string> TokensFor(FreeDigestAggregates a, string? recipientName, DateTime weekEnding) => new()
    {
        ["RecipientName"] = recipientName ?? string.Empty,
        ["WeekEnding"] = weekEnding.ToString("d MMM yyyy"),
        ["DueNext7"] = a.DueNext7.ToString(),
        ["CriticalDueNext7"] = a.CriticalDueNext7.ToString(),
        ["ImprisonmentDueNext7"] = a.ImprisonmentDueNext7.ToString(),
        ["DueNext30"] = a.DueNext30.ToString(),
        ["ImprisonmentDueNext30"] = a.ImprisonmentDueNext30.ToString(),
        ["LicencesLapsingNext30"] = a.LicencesLapsingNext30.ToString(),
        ["CompletedLast7"] = a.CompletedLast7.ToString(),
    };

    /// <summary>
    /// Minimal mustache subset: {{Token}} substitution plus {{#Token}}...{{/Token}}
    /// sections that render their contents only when the token is non-empty (used for
    /// the optional "{{#RecipientName}} {{RecipientName}}{{/RecipientName}}" greeting).
    /// The template vocabulary is small and fixed, so a full templating library is not
    /// warranted.
    /// </summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var withSections = SectionToken().Replace(template, match =>
        {
            var key = match.Groups["key"].Value;
            var hasValue = tokens.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value);
            return hasValue ? match.Groups["inner"].Value : string.Empty;
        });

        return PlainToken().Replace(withSections, match =>
            tokens.TryGetValue(match.Groups["key"].Value, out var value) ? value : string.Empty);
    }

    [GeneratedRegex(@"\{\{#(?<key>\w+)\}\}(?<inner>.*?)\{\{/\k<key>\}\}", RegexOptions.Singleline)]
    private static partial Regex SectionToken();

    [GeneratedRegex(@"\{\{(?<key>\w+)\}\}")]
    private static partial Regex PlainToken();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldMarkdown();
}

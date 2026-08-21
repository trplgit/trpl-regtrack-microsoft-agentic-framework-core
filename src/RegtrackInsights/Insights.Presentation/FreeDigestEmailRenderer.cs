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
            ["Body"] = body,
            ["TenantName"] = tenantName,
            ["WeekEnding"] = weekEnding.ToString("d MMM yyyy"),
            ["UpgradeUrl"] = upgradeUrl,
            ["UnsubscribeUrl"] = unsubscribeUrl,
        };
        return Substitute(template, tokens);
    }

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
}

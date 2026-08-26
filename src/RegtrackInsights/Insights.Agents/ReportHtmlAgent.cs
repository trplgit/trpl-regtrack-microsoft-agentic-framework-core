using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface IReportHtmlAgent
{
    /// <summary>
    /// Renders the approved plan and narrative into one self-contained HTML document
    /// (prompts/05_report_html.md - "Way 1", MVP only). The output is raw HTML, not JSON - this
    /// agent must be built with <see cref="MafAgentFactory.CreateTextAgent"/>, never
    /// CreateJsonAgent. The 7 output constraints (inline CSS/JS only, zero external references,
    /// no runtime network calls, inline SVG, system fonts only) are NOT enforced here - that is
    /// <see cref="ReportEmitNormalizer"/>'s job, deterministically, after this call returns.
    /// </summary>
    Task<AgentCallResult<string>> RenderAsync(
        CompositionPlan plan,
        NarrativeResult narrative,
        string tenantName,
        string reportType,
        DateTime generatedAt,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReportHtmlAgent"/>
public sealed partial class MafReportHtmlAgent(AIAgent agent) : IReportHtmlAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<AgentCallResult<string>> RenderAsync(
        CompositionPlan plan,
        NarrativeResult narrative,
        string tenantName,
        string reportType,
        DateTime generatedAt,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                composition_plan = plan,
                narrative,
                tenant_name = tenantName,
                report_type = reportType,
                generated_at = generatedAt,
            },
            JsonOptions);

        var message = "Render this approved report as a single self-contained HTML document:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var html = response.Text;
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("Report HTML agent returned no text.");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<string>(StripMarkdownFence(html), totalTokens);
    }

    /// <summary>
    /// [TRAP] Confirmed via a live run (2026-08-20): GPT-5.2 wrapped a fully correct HTML
    /// document in a markdown code fence (```html ... ```), even though the prompt asks for a
    /// raw document and never mentions fencing. ReportEmitNormalizer's checks all search WITHIN
    /// the string, so they still passed - the DOCTYPE and closing tag both existed, just with
    /// stray backtick lines wrapped around them, which a real browser would render as visible
    /// garbage text before/after the report. This is a mechanical formatting habit, not a
    /// content problem - same "don't fail over something this predictable" reasoning as the free
    /// digest's fallback path - so it is stripped defensively here, not treated as a refusal.
    /// The normalizer's rule 1 was separately tightened to verify the document actually starts/
    /// ends correctly, as a second net for anything this stripping does not catch.
    /// </summary>
    internal static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        var match = MarkdownFenceToken().Match(trimmed);
        return match.Success ? match.Groups["body"].Value.Trim() : trimmed;
    }

    [GeneratedRegex(@"\A```(?:html)?\s*\r?\n(?<body>.*?)\r?\n?```\z", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceToken();
}

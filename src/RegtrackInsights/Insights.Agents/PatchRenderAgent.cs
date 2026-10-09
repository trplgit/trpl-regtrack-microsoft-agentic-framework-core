using System.Text.Json;
using Insights.Presentation;
using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-10-07] Design spec Section 4 - a NEW, narrow render call distinct from
/// IReportHtmlAgent: inputs are the current full HTML plus a list of named real problems
/// (InteractiveTileChecker's TileFinding records), not a CompositionPlan/NarrativeResult. Output
/// contract is "the same document, only the named region(s) changed" - see
/// prompts/10_patch_render_defect.md for the exact, binding instruction. Built with
/// MafAgentFactory.CreateTextAgent, same reasoning as IReportHtmlAgent (raw HTML out, not JSON).
/// </summary>
public interface IPatchRenderAgent
{
    Task<AgentCallResult<string>> PatchAsync(string html, IReadOnlyList<TileFinding> findings, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPatchRenderAgent"/>
public sealed class MafPatchRenderAgent(AIAgent agent) : IPatchRenderAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<AgentCallResult<string>> PatchAsync(string html, IReadOnlyList<TileFinding> findings, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                html,
                findings = findings.Select(f => new
                {
                    card_selector = f.CardSelector,
                    card_title = f.CardTitle,
                    interaction = f.Interaction,
                    technical_description = f.TechnicalDescription,
                }),
            },
            JsonOptions);

        var response = await agent.RunAsync("Fix only the named problem(s) in this document:\n" + payload, cancellationToken: cancellationToken);
        var patched = response.Text;
        if (string.IsNullOrWhiteSpace(patched))
            throw new InvalidOperationException("Patch render agent returned no text.");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<string>(patched.Trim(), totalTokens);
    }
}

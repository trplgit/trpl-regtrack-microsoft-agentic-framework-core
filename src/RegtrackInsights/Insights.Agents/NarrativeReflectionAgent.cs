using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface INarrativeReflectionAgent
{
    /// <summary>
    /// Catches what the deterministic claim-checker structurally cannot (prompts/
    /// 04_narrative_reflection.md): a number that maps to a real assertion but tells a false
    /// story - inverted direction, an orphaned caveat, an invented severity word, a buried lede.
    /// This is judgement work, layered ON TOP OF PublishGate, never a substitute for it.
    /// </summary>
    Task<AgentCallResult<NarrativeReflectionResult>> ReflectAsync(
        NarrativeResult narrative,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INarrativeReflectionAgent"/>
public sealed class MafNarrativeReflectionAgent(AIAgent agent) : INarrativeReflectionAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentCallResult<NarrativeReflectionResult>> ReflectAsync(
        NarrativeResult narrative,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { narrative, assertions, findings }, JsonOptions);

        // [TRAP] Same Responses-API constraint as every other JSON-mode agent here: 400s unless
        // the input message itself contains the literal word "json" - confirmed live (2026-08-20).
        var message = "Here is the narrative to critique, with its assertion and finding pools, as JSON:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Narrative reflection agent returned no text.");

        var result = JsonSerializer.Deserialize<NarrativeReflectionResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Narrative reflection agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<NarrativeReflectionResult>(result, totalTokens);
    }
}

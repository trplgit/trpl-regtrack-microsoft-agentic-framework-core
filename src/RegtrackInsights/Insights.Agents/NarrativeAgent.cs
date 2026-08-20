using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface INarrativeAgent
{
    /// <summary>
    /// Writes prose for every block in <paramref name="plan"/>, strictly from
    /// <paramref name="assertions"/>/<paramref name="findings"/> (prompts/03_narrative.md - the
    /// D7 contract, README.md "you may only assert what the data layer has already verified").
    /// The result's <c>AssertionIdsUsed</c> per block is what PublishGate's claim-checker runs
    /// against - this agent's whole safety property rests on it declaring sources honestly, and
    /// the gate is what catches it if it doesn't.
    /// </summary>
    Task<NarrativeResult> NarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INarrativeAgent"/>
public sealed class MafNarrativeAgent(AIAgent agent) : INarrativeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<NarrativeResult> NarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new { composition_plan = plan, assertions, findings },
            JsonOptions);

        // [TRAP] Same Responses-API constraint as every other JSON-mode agent here: 400s unless
        // the input message itself contains the literal word "json" - confirmed live (2026-08-20).
        var message = "Here is the approved composition plan and the assertion/finding pools, as JSON:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Narrative agent returned no text.");

        return JsonSerializer.Deserialize<NarrativeResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Narrative agent returned unparsable JSON: {text}");
    }
}

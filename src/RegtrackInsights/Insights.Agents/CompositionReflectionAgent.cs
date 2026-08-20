using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface ICompositionReflectionAgent
{
    /// <summary>
    /// <paramref name="assertions"/>/<paramref name="findings"/> are the same pools the
    /// composition agent had - the critic needs them to check claims like "is the highest-
    /// severity finding the hero" (rule 1) and "does direction invert the reading" (rule 2).
    /// </summary>
    Task<CompositionReflectionResult> ReflectAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string tenantShape,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps a MAF <see cref="AIAgent"/> configured with prompts/02_composition_reflection.md.
/// Agentic judgement, layered ON TOP OF the deterministic PublishGate, never in place of it
/// (design doc 3.5) - this catches wrong emphasis or an inverted reading; it cannot catch a
/// hallucinated number or an out-of-scope row, and must never be trusted to.
/// </summary>
public sealed class MafCompositionReflectionAgent(AIAgent agent) : ICompositionReflectionAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CompositionReflectionResult> ReflectAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string tenantShape,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new { composition_plan = plan, assertions, findings, tenant_shape = tenantShape },
            JsonOptions);

        // [TRAP] Same Responses-API constraint as the composition agent: ResponseFormat=Json
        // 400s unless the input message itself contains the literal word "json" - confirmed
        // live, not documentation (2026-08-20). Data payloads never naturally contain it.
        var message = "Here is the composition plan to critique, as JSON:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Composition reflection agent returned no text.");

        return JsonSerializer.Deserialize<CompositionReflectionResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Composition reflection agent returned unparsable JSON: {text}");
    }
}

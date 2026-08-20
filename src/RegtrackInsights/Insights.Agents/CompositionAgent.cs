using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface ICompositionAgent
{
    /// <summary>
    /// <paramref name="dimensionResults"/> is keyed by dimension name ("Location", "Risk", ...);
    /// each value is whichever DimensionResult&lt;TControlTotals,TRow&gt; that dimension produced.
    /// Serialized straight to JSON, no shared umbrella type across the nine heterogeneous shapes
    /// (deliberate - see chat 2026-08-20: a translation layer here is another thing to keep in
    /// sync as dimension shapes evolve, for no benefit the agent itself needs).
    ///
    /// <paramref name="revision"/> is null on the first attempt. On a retry after composition
    /// reflection returns Revise, the caller (the bounded loop) passes the previous plan and the
    /// critic's issues, and this call is expected to produce a genuinely revised plan addressing
    /// them - not silently ignore them and repeat the same output.
    /// </summary>
    Task<CompositionPlan> ComposeAsync(
        IReadOnlyDictionary<string, object> dimensionResults,
        string tenantShape,
        string reportType,
        (CompositionPlan PreviousPlan, IReadOnlyList<CompositionReflectionIssue> Issues)? revision = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps a MAF <see cref="AIAgent"/> configured with prompts/01_composition.md as its
/// instructions (see MafAgentFactory). This class owns only the input/output shape -
/// serialise dimension data in, parse the JSON plan out. It does not judge, order, or emphasise
/// anything itself; that is entirely the prompt's job, and composition reflection
/// (02_composition_reflection.md) is what critiques it - not this class.
/// </summary>
public sealed class MafCompositionAgent(AIAgent agent) : ICompositionAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CompositionPlan> ComposeAsync(
        IReadOnlyDictionary<string, object> dimensionResults,
        string tenantShape,
        string reportType,
        (CompositionPlan PreviousPlan, IReadOnlyList<CompositionReflectionIssue> Issues)? revision = null,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                dimensions = dimensionResults,
                tenant_shape = tenantShape,
                report_type = reportType,
                previous_plan = revision?.PreviousPlan,
                reflection_issues = revision?.Issues,
            },
            JsonOptions);

        // [TRAP] The Responses API 400s on ResponseFormat=Json unless the literal word "json"
        // appears in the INPUT message itself - instructions containing it is not enough, and a
        // pure data payload naturally never contains that English word. Confirmed via a live
        // call, not documentation (2026-08-20). This prefix is the fix, not decoration.
        var message = (revision is not null
            ? "Composition reflection returned these issues on your previous plan - produce a revised plan that genuinely addresses them, as JSON:\n"
            : "Here is the dimension data as JSON:\n") + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Composition agent returned no text.");

        return JsonSerializer.Deserialize<CompositionPlan>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Composition agent returned unparsable JSON: {text}");
    }
}

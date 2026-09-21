using System.Text.Json;
using System.Text.Json.Serialization;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// [FOUND LIVE 2026-09-20] The model sometimes emits row_refs_used[].member_key as a raw JSON
/// number (e.g. DepartmentID) instead of the real string name field the prompt's worked example
/// uses - confirmed live against tenant 29's real Departments data. Accepting either and
/// coercing to string here is a defensive fix on top of the prompt clarification also made the
/// same day; a model output contract should not hard-fail on a plausible, if wrong, token type.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a string or number for member_key, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// [ADDED 2026-09-20] prompts/v2/03_narrative_analyst.md - replaces INarrativeAgent +
/// INarrativeReflectionAgent for the 5 freehand dimensions only (see the design doc:
/// docs/superpowers/specs/2026-09-20-narrative-analyst-agent-design.md). One call does both jobs:
/// writes prose AND performs its own self-reflection pass before returning, instead of a second
/// round-trip to a separate reflection agent.
///
/// LAB/EXPERIMENTAL - not yet wired into PaidReportAgentsRegistration or the orchestrator. See
/// tests/Insights.IntegrationTests/NarrativeAnalystLabTests.cs for the real comparison harness.
/// </summary>
public interface IAnalystNarrativeAgent
{
    /// <summary>
    /// Writes prose for every block in <paramref name="plan"/>, from
    /// <paramref name="assertions"/>/<paramref name="findings"/> PLUS the raw per-member
    /// <paramref name="dimensionRowsJson"/> for this dimension - the same real, already-reconciled
    /// data FetchDimensionsActivity already retrieved, just now threaded to this agent so it can
    /// trace a number back to a specific member (performer, department, branch) instead of only
    /// restating it. <paramref name="dimensionControlTotalsJson"/> is optional - not every
    /// dimension's tenant-level aggregates are relevant to every trace.
    ///
    /// Unlike INarrativeAgent, there is no separate revision parameter fed back from an external
    /// reflection agent - self-critique happens inside this one call (prompts/v2's own
    /// self_reflection block). <paramref name="revision"/> still exists for the SAME class of
    /// retry PublishGate or an external caller might request (e.g. a claim-checker rejection),
    /// not for a narrative-reflection round-trip that no longer exists.
    /// </summary>
    Task<AgentCallResult<NarrativeResult>> AnalyzeAndNarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionName,
        string dimensionRowsJson,
        string? dimensionControlTotalsJson,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnalystNarrativeAgent"/>
public sealed class MafAnalystNarrativeAgent(AIAgent agent) : IAnalystNarrativeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringConverter() },
    };

    public async Task<AgentCallResult<NarrativeResult>> AnalyzeAndNarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionName,
        string dimensionRowsJson,
        string? dimensionControlTotalsJson,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        CancellationToken cancellationToken = default)
    {
        // dimension_rows/dimension_control_totals are already-serialized JSON strings (same shape
        // InsightsReportOrchestrator already builds for RenderHtmlInput - see the design doc
        // Sec.4) - embed as raw JSON, not as a re-escaped string, so the agent sees real objects.
        var payload =
            $$"""
            {
              "composition_plan": {{JsonSerializer.Serialize(plan, JsonOptions)}},
              "assertions": {{JsonSerializer.Serialize(assertions, JsonOptions)}},
              "findings": {{JsonSerializer.Serialize(findings, JsonOptions)}},
              "dimension_name": {{JsonSerializer.Serialize(dimensionName)}},
              "dimension_rows": {{dimensionRowsJson}},
              "dimension_control_totals": {{dimensionControlTotalsJson ?? "null"}},
              "previous_narrative": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.PreviousNarrative, JsonOptions))}},
              "reflection_issues": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.Issues, JsonOptions))}}
            }
            """;

        // [TRAP] Same Responses-API constraint every other JSON-mode agent here already documents:
        // 400s unless the input message itself contains the literal word "json".
        var message = (revision is not null
            ? "A prior attempt needs revision against these issues, as JSON:\n"
            : "Here is the approved composition plan, the assertion/finding pools, and the raw dimension data for root-cause tracing, as JSON:\n") + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Analyst narrative agent returned no text.");

        var narrative = JsonSerializer.Deserialize<NarrativeResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Analyst narrative agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<NarrativeResult>(narrative, totalTokens, ReasoningSummaryExtractor.Extract(response.Messages));
    }
}

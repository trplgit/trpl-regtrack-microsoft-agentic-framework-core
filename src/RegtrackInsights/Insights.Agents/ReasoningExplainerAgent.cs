using System.Text.Json;
using System.Text.Json.Serialization;
using Insights.Data;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-26] Real, deterministic trace bundle for ONE dimension's ONE real run - every
/// piece of already-produced data ReasoningExplainerAgent needs to explain the report, assembled
/// with no new LLM call and no new claim: the composition plan, the typed assertions/findings, the
/// raw dimension rows/control totals, the real data_quality notes, and (when present) the real
/// vendor reasoning-summary text per stage and any live SQL the narrate agent actually ran.
/// </summary>
public sealed record ReasoningTraceBundle(
    string DimensionName,
    string? RunId,
    CompositionPlan Plan,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<Finding> Findings,
    string DimensionRowsJson,
    string? DimensionControlTotalsJson,
    IReadOnlyList<DataQualityNote> DataQuality,
    IReadOnlyList<AgentReasoningLogEntry> ReasoningLog,
    IReadOnlyList<ToolInvocationLogEntry> ToolInvocations,
    // [ADDED 2026-09-27] What the reader actually sees, and every number on it (code-extracted) -
    // the explainer must cover each one. Null when the finished report was not available.
    string? ReportText = null,
    IReadOnlyList<string>? NumbersOnReport = null,
    // [ADDED 2026-10-01] fixed_holistic (Entity) spans up to 15 dimensions, not one - no single
    // CompositionPlan-for-one-dimension shape exists for it. When present, this REPLACES
    // DimensionRowsJson/DimensionControlTotalsJson in the payload the explainer agent is given
    // (dimension name -> its own {Rows, ControlTotals, DataQuality} JSON, straight from
    // FetchDimensionsOutput.DimensionResults - no new data, no new claim, same as every other
    // field on this bundle). Null for the freehand single-dimension path, unchanged.
    IReadOnlyDictionary<string, string>? AllDimensionDataJson = null);
    // [REMOVED 2026-09-28] DatabaseChecksJson - testers work from formulas, not SQL (user decision).

/// <summary>
/// [ADDED 2026-09-26] Turns a <see cref="ReasoningTraceBundle"/> into a plain-Markdown "how was
/// this report built" document for internal testers - never a second report, never a new claim.
/// Deliberately a SMALL model (gpt-4o-mini, same endpoint/key as the other real agents - see
/// PaidReportAgentsRegistration.cs) - this is an explain/cite task over already-verified data, not
/// a judgement call, so it does not need the reasoning-heavy deployment compose/narrate/render use.
/// </summary>
public interface IReasoningExplainerAgent
{
    Task<AgentCallResult<string>> ExplainAsync(ReasoningTraceBundle bundle, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReasoningExplainerAgent"/>
public sealed class MafReasoningExplainerAgent(AIAgent agent) : IReasoningExplainerAgent
{
    // [FIX - FOUND LIVE 2026-09-26] Without a string converter, Assertion.Direction (an enum)
    // serializes as its raw ordinal (0/1) - confirmed live: a real explainer output showed
    // "Direction: 0" verbatim, unreadable to the tester this whole document exists for. The
    // explainer faithfully reported exactly what the JSON gave it - the bug was in this payload,
    // not in the model's reading of it.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<AgentCallResult<string>> ExplainAsync(ReasoningTraceBundle bundle, CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(bundle);
        var message = "Here is the real trace bundle for one report, as JSON - write the explainer document:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Reasoning explainer agent returned no text.");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<string>(text.Trim(), totalTokens, ReasoningSummaryExtractor.Extract(response.Messages));
    }

    /// <summary>
    /// [ADDED 2026-09-28] What a tool call did, never its SQL text: the model is told to describe
    /// extra look-ups in plain words, and without the text it has no SQL to copy into the file.
    /// </summary>
    internal static IReadOnlyList<object> ToolCallsWithoutSql(IReadOnlyList<ToolInvocationLogEntry> calls) =>
        calls.Select(c => (object)new { c.Stage, c.ToolName, c.Success, c.ResultLength }).ToList();

    /// <summary>
    /// [ADDED 2026-10-01] Extracted so a unit test can assert the real JSON shape without a fake
    /// AIAgent. fixed_holistic's multi-dimension shape (<see cref="ReasoningTraceBundle.AllDimensionDataJson"/>)
    /// replaces the two singular dimension_* keys with one dimension_data object keyed by dimension
    /// name; the freehand single-dimension path (the only shape that existed before today) is
    /// completely unchanged.
    /// </summary>
    internal static string BuildPayload(ReasoningTraceBundle bundle)
    {
        var dimensionDataBlock = bundle.AllDimensionDataJson is null
            ? $$"""
              "dimension_rows": {{bundle.DimensionRowsJson}},
              "dimension_control_totals": {{bundle.DimensionControlTotalsJson ?? "null"}},
              """
            : $$"""
              "dimension_data": { {{string.Join(",", bundle.AllDimensionDataJson.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {kv.Value}"))}} },
              """;

        return $$"""
            {
              "dimension_name": {{JsonSerializer.Serialize(bundle.DimensionName)}},
              "run_id": {{JsonSerializer.Serialize(bundle.RunId)}},
              "composition_plan": {{JsonSerializer.Serialize(bundle.Plan, JsonOptions)}},
              "assertions": {{JsonSerializer.Serialize(bundle.Assertions, JsonOptions)}},
              "findings": {{JsonSerializer.Serialize(bundle.Findings, JsonOptions)}},
              {{dimensionDataBlock}}
              "data_quality": {{JsonSerializer.Serialize(bundle.DataQuality, JsonOptions)}},
              "reasoning_log": {{JsonSerializer.Serialize(bundle.ReasoningLog, JsonOptions)}},
              "tool_invocations": {{JsonSerializer.Serialize(ToolCallsWithoutSql(bundle.ToolInvocations), JsonOptions)}},
              "report_text": {{JsonSerializer.Serialize(bundle.ReportText)}},
              "numbers_on_report": {{JsonSerializer.Serialize(bundle.NumbersOnReport ?? [])}}
            }
            """;
    }
}

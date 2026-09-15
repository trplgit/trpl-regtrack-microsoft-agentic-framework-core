using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-14] Real LLM composition for exactly the four dimensions in
/// <see cref="FreehandDimensions"/> - which sections, in what order, which one leads as hero and
/// why (decided per-tenant from that tenant's own real numbers, never fixed), what each section
/// emphasizes. A genuinely different agent per dimension (Act/BacklogAging/Departments/Licence
/// each get their own prompt file, since each dimension's real fields and traps differ), so this
/// is a dictionary registration, same pattern as IReportHtmlAgent's per-dimension render agents.
/// Every other dimension (Location, Users, Entity) keeps the existing deterministic
/// DimensionSelectionComposition.Build/FixedHolisticComposition.Build path, unchanged.
/// </summary>
public interface IFreehandDimensionCompositionAgent
{
    /// <summary>
    /// <paramref name="dimensionRowsJson"/>/<paramref name="dimensionControlTotalsJson"/>/
    /// <paramref name="dataQualityJson"/> are the dimension's own real, already-reconciled SQL
    /// output - raw JSON extracted from FetchDimensionsOutput.DimensionResults, same source
    /// IReportHtmlAgent.RenderAsync's dimensionRowsJson/dimensionControlTotalsJson parameters
    /// already read. Nothing here is curated or capped the way <paramref name="assertions"/>/
    /// <paramref name="findings"/> are.
    /// </summary>
    Task<AgentCallResult<CompositionPlan>> ComposeAsync(
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionRowsJson,
        string dimensionControlTotalsJson,
        string dataQualityJson,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IFreehandDimensionCompositionAgent"/>
public sealed class MafFreehandDimensionCompositionAgent(AIAgent agent) : IFreehandDimensionCompositionAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentCallResult<CompositionPlan>> ComposeAsync(
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionRowsJson,
        string dimensionControlTotalsJson,
        string dataQualityJson,
        CancellationToken cancellationToken = default)
    {
        // dimension_rows/dimension_control_totals/data_quality are already-serialized real JSON -
        // parsed back into JsonElement so they nest as real JSON in the payload, not as an escaped
        // string the agent would have to un-escape itself (same reasoning as
        // MafReportHtmlAgent.RenderAsync's own dimensionRows/dimensionControlTotals handling).
        var payload = JsonSerializer.Serialize(
            new
            {
                assertions,
                findings,
                dimension_rows = JsonSerializer.Deserialize<JsonElement>(dimensionRowsJson),
                dimension_control_totals = JsonSerializer.Deserialize<JsonElement>(dimensionControlTotalsJson),
                data_quality = JsonSerializer.Deserialize<JsonElement>(dataQualityJson),
            },
            JsonOptions);

        // [TRAP] Same Responses-API constraint as every other JSON-mode agent here: 400s unless
        // the input message itself contains the literal word "json" - confirmed live (2026-08-20).
        var message = "Here is this tenant's real data for this dimension, as JSON:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Freehand dimension composition agent returned no text.");

        var plan = JsonSerializer.Deserialize<CompositionPlan>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Freehand dimension composition agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<CompositionPlan>(plan, totalTokens);
    }
}

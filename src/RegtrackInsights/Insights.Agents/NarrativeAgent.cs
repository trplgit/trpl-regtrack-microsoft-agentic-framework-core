using System.Text.Json;
using Insights.Data;
using Insights.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    ///
    /// <paramref name="revision"/> is null on the first attempt. On a retry after narrative
    /// reflection returns Revise, the caller passes the previous narrative and the critic's
    /// issues (each with its offending quote), expecting a genuinely revised narrative.
    ///
    /// <paramref name="dimensionNames"/>/<paramref name="tenantId"/> [ADDED 2026-09-22] - optional,
    /// trailing. Unlike v2's AnalyzeAndNarrateAsync (always single-dimension), a v1 call can cover
    /// SEVERAL dimensions at once (fixed_holistic, multi-select dimension_selection) - every
    /// dimension actually present in <paramref name="plan"/>'s blocks should be named here, so
    /// tenant-memory reads/writes can be scoped correctly. Only meaningful when this instance was
    /// built with real tenant-memory dependencies (see MafNarrativeAgent's own doc comment).
    /// </summary>
    Task<AgentCallResult<NarrativeResult>> NarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        IReadOnlyList<string>? dimensionNames = null,
        int? tenantId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// [EXTENDED 2026-09-22] Tenant memory - see
/// docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md. All four memory params null (the
/// default) is a complete no-op, same pattern as MafAnalystNarrativeAgent's own extension. When set
/// (and NarrateAsync also receives real dimensionNames/tenantId), this call reads every named
/// dimension's history section before narrating (joined into a labelled tenant_history field) and
/// gets a real write_tenant_memory tool, scoped to exactly those dimensions - the FIRST time v1 has
/// had any tool-calling capability at all.
/// </summary>
public sealed class MafNarrativeAgent(
    AIAgent agent,
    IReportEncryptor? memoryEncryptor = null,
    IReportDecryptor? memoryDecryptor = null,
    string? memoryBlobConnectionString = null,
    string? memoryContainerName = null) : INarrativeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentCallResult<NarrativeResult>> NarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        IReadOnlyList<string>? dimensionNames = null,
        int? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        TenantMemoryTool? memoryTool = null;
        IReadOnlyDictionary<string, string> tenantHistory = new Dictionary<string, string>();
        if (memoryEncryptor is not null && memoryDecryptor is not null && memoryBlobConnectionString is not null
            && memoryContainerName is not null && tenantId is not null && dimensionNames is { Count: > 0 })
        {
            memoryTool = new TenantMemoryTool(
                memoryEncryptor, memoryDecryptor, memoryBlobConnectionString, memoryContainerName,
                tenantId.Value, dimensionNames);
            tenantHistory = await memoryTool.ReadSectionsAsync(cancellationToken);
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                composition_plan = plan,
                assertions,
                findings,
                tenant_history = tenantHistory,
                previous_narrative = revision?.PreviousNarrative,
                reflection_issues = revision?.Issues,
            },
            JsonOptions);

        // [TRAP] Same Responses-API constraint as every other JSON-mode agent here: 400s unless
        // the input message itself contains the literal word "json" - confirmed live (2026-08-20).
        var message = (revision is not null
            ? "Narrative reflection returned these issues on your previous narrative, each with the offending quote - produce a revised narrative that genuinely addresses them, as JSON:\n"
            : "Here is the approved composition plan and the assertion/finding pools, as JSON:\n") + payload;

        var runOptions = memoryTool is not null
            ? new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = [AIFunctionFactory.Create(memoryTool.WriteTenantMemoryAsync, name: "write_tenant_memory")],
                },
            }
            : null;

        var response = runOptions is not null
            ? await agent.RunAsync(message, session: null, options: runOptions, cancellationToken: cancellationToken)
            : await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Narrative agent returned no text.");

        var narrative = JsonSerializer.Deserialize<NarrativeResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Narrative agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<NarrativeResult>(narrative, totalTokens, ReasoningSummaryExtractor.Extract(response.Messages));
    }
}

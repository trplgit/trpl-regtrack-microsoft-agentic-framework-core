using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-14] Real vision-model review of one or more real screenshots of a rendered
/// report - the ONLY agent in this codebase that sees an image rather than text/JSON. Deliberately
/// narrow: looks ONLY for mechanical layout defects (elements overlapping, content escaping its
/// container, obviously broken/collapsed structure) - never content accuracy (PublishGate's job),
/// never colour/design taste. See prompts/06_vision_qa.md for the exact, binding scope.
/// </summary>
public interface IVisionQaAgent
{
    /// <summary>
    /// <paramref name="screenshots"/> - one real PNG per tab/state the report exposes (see
    /// PlaywrightReportQa's own multi-tab capture) - reviewed together in one call so the model
    /// can reason about the whole document, not just one state in isolation.
    /// </summary>
    Task<AgentCallResult<VisionQaResult>> ReviewAsync(IReadOnlyList<byte[]> screenshots, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVisionQaAgent"/>
public sealed class MafVisionQaAgent(AIAgent agent) : IVisionQaAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentCallResult<VisionQaResult>> ReviewAsync(IReadOnlyList<byte[]> screenshots, CancellationToken cancellationToken = default)
    {
        if (screenshots.Count == 0)
            throw new ArgumentException("At least one screenshot is required.", nameof(screenshots));

        // [TRAP] Same Responses-API constraint as every other JSON-mode agent here: 400s unless
        // the input message itself contains the literal word "json" - confirmed live (2026-08-20).
        var contents = new List<AIContent>
        {
            new TextContent($"Here {(screenshots.Count == 1 ? "is a real screenshot" : $"are {screenshots.Count} real screenshots, one per tab/state")} of a rendered report. Respond as JSON."),
        };
        contents.AddRange(screenshots.Select(s => new DataContent(s, "image/png")));

        var message = new ChatMessage(ChatRole.User, contents);

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Vision QA agent returned no text.");

        var result = JsonSerializer.Deserialize<VisionQaResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Vision QA agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<VisionQaResult>(result, totalTokens);
    }
}

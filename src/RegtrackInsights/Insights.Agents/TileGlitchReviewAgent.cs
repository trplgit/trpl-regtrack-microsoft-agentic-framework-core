using System.Text.Json;
using Insights.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-10-07] Stage 2 of InteractiveTileChecker's cross-tile glitch detection (design spec
/// Section 3). Deliberately NOT IVisionQaAgent - that agent's prompt/framing (06_vision_qa.md) is
/// scoped to "here is a real screenshot of a rendered report," a whole-page review; this agent
/// reviews exactly two small crops (before/after the SAME region) and answers a narrower, different
/// question (did this specific region change in a way that looks broken). Same underlying
/// Llm:VisionQa:Endpoint/Model/ApiKey deployment as IVisionQaAgent - see
/// PaidReportAgentsRegistration.cs - just a different prompt and a different, two-image call shape.
/// </summary>
public interface ITileGlitchReviewAgent
{
    Task<AgentCallResult<TileGlitchReviewResult>> ReviewAsync(byte[] beforeCrop, byte[] afterCrop, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITileGlitchReviewAgent"/>
public sealed class MafTileGlitchReviewAgent(AIAgent agent) : ITileGlitchReviewAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentCallResult<TileGlitchReviewResult>> ReviewAsync(byte[] beforeCrop, byte[] afterCrop, CancellationToken cancellationToken = default)
    {
        var contents = new List<AIContent>
        {
            new TextContent("Here are the \"before\" and \"after\" crops of one region. Respond as JSON."),
            new DataContent(beforeCrop, "image/png"),
            new DataContent(afterCrop, "image/png"),
        };
        var message = new ChatMessage(ChatRole.User, contents);

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Tile glitch review agent returned no text.");

        var result = JsonSerializer.Deserialize<TileGlitchReviewResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Tile glitch review agent returned unparsable JSON: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<TileGlitchReviewResult>(result, totalTokens);
    }
}

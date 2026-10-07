using Insights.Domain;

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

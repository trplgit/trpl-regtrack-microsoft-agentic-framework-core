namespace Insights.Worker.Orchestration;

/// <summary>
/// The 7 stage names API_CONTRACTS.md 4 locks for GET /api/insights/runs/{runId}/stream. Serialized
/// via the lowercase names below (matching the contract's JSON exactly), not the C# member names.
/// </summary>
public enum InsightsRunStage
{
    Gathering,
    Validating,
    Composing,
    Narrating,
    Verifying,
    Rendering,
    Complete,
}

public static class InsightsRunStageExtensions
{
    public static string ToContractName(this InsightsRunStage stage) => stage switch
    {
        InsightsRunStage.Gathering => "gathering",
        InsightsRunStage.Validating => "validating",
        InsightsRunStage.Composing => "composing",
        InsightsRunStage.Narrating => "narrating",
        InsightsRunStage.Verifying => "verifying",
        InsightsRunStage.Rendering => "rendering",
        InsightsRunStage.Complete => "complete",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };
}

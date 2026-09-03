namespace Insights.Domain;

/// <summary>
/// Verdict of the deterministic FixedHolisticStructureGate (Insights.Presentation) - the score-
/// hero-card-count and blocked-tab-badge invariants. Same shape/posture as ReportEmitResult: any
/// violation refuses the whole document, no partial-pass.
/// </summary>
public sealed record FixedHolisticStructureResult(bool Approved, IReadOnlyList<string> Violations)
{
    public static readonly FixedHolisticStructureResult Approve = new(true, []);
    public static FixedHolisticStructureResult Refuse(IReadOnlyList<string> violations) => new(false, violations);
}

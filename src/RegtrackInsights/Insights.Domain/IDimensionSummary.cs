namespace Insights.Domain;

/// <summary>
/// The dimension-agnostic half of a dimension result - everything except control_totals and rows.
///
/// Those four result sets are identical across all nine dimensions, which is what lets the
/// publish gate, the composition agent and the narrative agent take any dimension without
/// knowing which one it is. DimensionResult&lt;TControlTotals, TRow&gt; implements this.
/// </summary>
public interface IDimensionSummary
{
    string Dimension { get; }
    IReadOnlyList<DetectorPolicy> Detectors { get; }
    IReadOnlyList<Assertion> Assertions { get; }
    IReadOnlyList<Finding> Findings { get; }
    IReadOnlyList<DataQualityNote> DataQuality { get; }
}

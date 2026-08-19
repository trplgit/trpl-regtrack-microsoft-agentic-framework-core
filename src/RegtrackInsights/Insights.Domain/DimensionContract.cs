namespace Insights.Domain;

/// <summary>
/// How a detector's findings are emitted, decided in SQL by the shared emission policy:
/// nothing flagged -> None; a member set of five or fewer -> Individual (aggregating below
/// the top-5 cap cannot reduce output and only loses the member names); more than 20% of the
/// eligible set flagged -> Aggregate (a finding firing on 84% of a tenant's locations is not a
/// finding, it is a description of how that tenant operates); otherwise Individual.
/// </summary>
public enum DetectorEmitMode
{
    None,
    Individual,
    Aggregate,
}

/// <summary>Which side of its comparator an assertion falls on. Computed in SQL, never phrased there.</summary>
public enum AssertionDirection
{
    Worse,
    Better,
}

/// <summary>Finding severity as emitted by the dimension procs.</summary>
public enum FindingSeverity
{
    Info,
    Medium,
    High,
}

/// <summary>
/// One detector's emission decision. <see cref="Eligible"/> and <see cref="Flagged"/> MUST come
/// from the same population - drawing them from different ones is what produced "191 of 109
/// locations (175.2%)" during design. <see cref="DimensionResult{TControlTotals,TRow}.Validate"/>
/// enforces that here, because no SQL THROW covers it.
/// </summary>
public sealed record DetectorPolicy(
    string Detector,
    int Eligible,
    int Flagged,
    decimal FlaggedPct,
    DetectorEmitMode EmitMode);

/// <summary>
/// A typed, pre-computed claim. The narrative agent may assert ONLY what appears here - this is
/// the defence against "right numbers, lying narrative". Comparatives are computed
/// (<see cref="ComparatorValue"/>, <see cref="VsComparatorPP"/>, <see cref="Direction"/>) rather
/// than left for a model to infer, and <see cref="Caveat"/> travels WITH the value: the prompt
/// contract forbids citing a value without its caveat, so never surface one without the other.
/// </summary>
public sealed record Assertion(
    string AssertionId,
    string Metric,
    string ScopeLabel,
    decimal Value,
    int? Rank,
    int? OfN,
    decimal? ComparatorValue,
    decimal? VsComparatorPP,
    AssertionDirection? Direction,
    string? Caveat)
{
    /// <summary>True when this assertion is a tenant-level aggregate rather than a named member.</summary>
    public bool IsTenantScoped => string.Equals(ScopeLabel, "tenant", StringComparison.Ordinal);
}

/// <summary>
/// A headline plus the assertions that back it. <see cref="NarrativeGuard"/> is an INSTRUCTION
/// that binds the narrator, not a note for a human reader - e.g. dormancy findings must be put
/// as a question, and "disengaged but current" must never be presented as good performance.
/// Pass it through to the prompt verbatim; never summarise or drop it.
/// </summary>
public sealed record Finding(
    string FindingId,
    FindingSeverity Severity,
    string Headline,
    IReadOnlyList<string> AssertionIds,
    string? NarrativeGuard);

/// <summary>
/// A declared limitation of this run. Never optional decoration: several are mandatory (the
/// uncategorised-nature gap, the engagement/quality confound), and a report that omits them
/// misrepresents how much of the estate the numbers actually cover.
/// </summary>
public sealed record DataQualityNote(string Issue, string Detail);

/// <summary>
/// The six-result-set contract every dimension proc emits, in order: control_totals, rows,
/// detector_policy, assertions, findings, data_quality. Read positionally by QueryMultiple.
/// Only the first two vary by dimension; the other four are identical everywhere, which is what
/// lets one repository serve all nine.
///
/// Holding one of these means the proc's own THROWs passed - scope resolved, and per-member sums
/// reconciled to the scoped total. See <see cref="DimensionReconciliationException"/> for the
/// failure path.
/// </summary>
public sealed class DimensionResult<TControlTotals, TRow>(
    string dimension,
    TControlTotals controlTotals,
    IReadOnlyList<TRow> rows,
    IReadOnlyList<DetectorPolicy> detectors,
    IReadOnlyList<Assertion> assertions,
    IReadOnlyList<Finding> findings,
    IReadOnlyList<DataQualityNote> dataQuality)
{
    public string Dimension { get; } = dimension;
    public TControlTotals ControlTotals { get; } = controlTotals;
    public IReadOnlyList<TRow> Rows { get; } = rows;
    public IReadOnlyList<DetectorPolicy> Detectors { get; } = detectors;
    public IReadOnlyList<Assertion> Assertions { get; } = assertions;
    public IReadOnlyList<Finding> Findings { get; } = findings;
    public IReadOnlyList<DataQualityNote> DataQuality { get; } = dataQuality;

    /// <summary>
    /// Enforces the two contract rules that have no SQL THROW behind them.
    ///
    /// Reconciliation is self-enforcing - the procs refuse to publish and the batch stops. These
    /// two are not, so a violation would reach a customer silently. That is exactly the failure
    /// mode this project exists to prevent, so they are checked here and fail closed.
    ///
    ///   1. Flagged &lt;= Eligible on every detector. A rate above 100% means the two counts were
    ///      drawn from different populations.
    ///   2. No Aggregate emission on a member set of five or fewer. Aggregating below the top-5
    ///      cap cannot reduce output and only discards the member names.
    ///
    /// Called automatically by the repository - callers do not need to invoke it.
    /// </summary>
    public void Validate()
    {
        foreach (var d in Detectors)
        {
            if (d.Flagged > d.Eligible)
            {
                throw new DimensionContractViolationException(
                    Dimension,
                    $"detector '{d.Detector}' flagged {d.Flagged} of {d.Eligible} eligible ({d.FlaggedPct}%). " +
                    "Flagged and Eligible must come from the same population.");
            }

            if (d.EmitMode == DetectorEmitMode.Aggregate && d.Eligible <= 5)
            {
                throw new DimensionContractViolationException(
                    Dimension,
                    $"detector '{d.Detector}' emitted as aggregate over only {d.Eligible} eligible member(s). " +
                    "Below the top-5 cap, aggregating loses the member names and gains nothing.");
            }
        }
    }
}

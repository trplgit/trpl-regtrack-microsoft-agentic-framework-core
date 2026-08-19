namespace Insights.Domain;

/// <summary>
/// One row from <c>dbo.usp_Insights_GoldenInvariants</c> (G-1 .. G-9).
/// </summary>
public sealed record GoldenInvariantResult(string TestId, string TestName, string Result, string Detail)
{
    public bool Passed => Result == "PASS";
}

/// <summary>
/// The full outcome of one golden regression run against one tenant.
/// </summary>
public sealed class GoldenRegressionRun(int customerId, IReadOnlyList<GoldenInvariantResult> results)
{
    public int CustomerId { get; } = customerId;
    public IReadOnlyList<GoldenInvariantResult> Results { get; } = results;

    public bool AllPassed => Results.Count > 0 && Results.All(r => r.Passed);

    public IReadOnlyList<GoldenInvariantResult> Failures => Results.Where(r => !r.Passed).ToArray();

    /// <summary>
    /// Human-readable failure summary for test output / logs - "TestId: Detail" per
    /// failing row, so a failure is diagnosable from the message alone.
    /// </summary>
    public string DescribeFailures() =>
        Failures.Count == 0
            ? "all invariants passed"
            : string.Join(Environment.NewLine, Failures.Select(f => $"{f.TestId} ({f.TestName}): {f.Detail}"));
}

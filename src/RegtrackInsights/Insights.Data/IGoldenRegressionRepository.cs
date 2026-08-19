using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps <c>dbo.usp_Insights_GoldenInvariants</c> (sql/02) - the drift-proof
/// production-invariant suite (spec Section 6.6). Run before any dimension
/// proc is written or edited; CLAUDE.md Section 9.
/// </summary>
public interface IGoldenRegressionRepository
{
    /// <summary>
    /// Runs G-1 .. G-9 for one tenant. Never throws on a failing invariant - the
    /// underlying THROW (error 51002) is caught and reflected in the returned
    /// run's <see cref="GoldenRegressionRun.AllPassed"/> / <see cref="GoldenRegressionRun.Failures"/>,
    /// so callers always get the full row-level detail, not just an exception.
    /// Any OTHER SQL error (bad connection, dictionary gap, etc.) still throws.
    /// </summary>
    Task<GoldenRegressionRun> RunAsync(int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);
}

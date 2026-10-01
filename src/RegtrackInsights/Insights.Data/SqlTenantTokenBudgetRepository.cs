using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Insights.Data;

/// <inheritdoc cref="ITenantTokenBudgetRepository"/>
public sealed class SqlTenantTokenBudgetRepository(string connectionString, ILogger<SqlTenantTokenBudgetRepository> logger) : ITenantTokenBudgetRepository
{
    // SQL Server's real error number for a UNIQUE INDEX violation (2627 is PK/UNIQUE CONSTRAINT -
    // sql/20's guard is a UNIQUE NONCLUSTERED INDEX, so it raises 2601, not 2627).
    private const int UniqueIndexViolation = 2601;
    public async Task<long> GetTokensSinceAsync(int customerId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT ISNULL(SUM(TotalTokens), 0)
            FROM dbo.InsightsTenantTokenUsage
            WHERE CustomerID = @CustomerId AND RecordedAtUtc >= @SinceUtc;
            """;

        return await connection.QuerySingleAsync<long>(
            new CommandDefinition(sql, new { CustomerId = customerId, SinceUtc = sinceUtc }, cancellationToken: cancellationToken));
    }

    public async Task RecordUsageAsync(int customerId, string runId, long totalTokens, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        /*  [CHANGED 2026-09-18] IF NOT EXISTS-then-INSERT is two statements, not one - two
            concurrent connections recording the SAME RunId (a redelivered
            RecordTenantTokenUsageActivity, the same DTFx at-least-once risk PersistActivity has)
            can both see "not exists" before either commits, and both insert - a real, live
            double-count risk with 4 replicas that never had a chance to fire at 1. Kept as a fast
            path (avoids hitting the unique index in the common, non-racing case) but sql/20 now
            backs this with a real UNIQUE NONCLUSTERED INDEX on RunId - the actual correctness
            guarantee is the constraint, not this check.                                          */
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.InsightsTenantTokenUsage WHERE RunId = @RunId)
                INSERT INTO dbo.InsightsTenantTokenUsage (CustomerID, RunId, TotalTokens)
                VALUES (@CustomerId, @RunId, @TotalTokens);
            """;

        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(sql, new { CustomerId = customerId, RunId = runId, TotalTokens = totalTokens }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number == UniqueIndexViolation)
        {
            // Lost the race: another connection's redelivered attempt for this SAME RunId won
            // between our own NOT EXISTS check and our INSERT. Their row already carries this
            // run's real spend - the interface contract (ITenantTokenBudgetRepository) is explicit
            // that a duplicate RunId must never throw, so this is success, not an error.
            logger.LogInformation(
                "RecordUsageAsync: lost a concurrent race for RunId {RunId} - another connection already recorded it.", runId);
        }
    }
}

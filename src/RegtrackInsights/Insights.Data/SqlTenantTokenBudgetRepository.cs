using Dapper;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="ITenantTokenBudgetRepository"/>
public sealed class SqlTenantTokenBudgetRepository(string connectionString) : ITenantTokenBudgetRepository
{
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

        /*  IDEMPOTENT on RunId, not via a unique constraint - a NOT EXISTS guard keeps the DDL
            in sql/20 simple (no unique index to maintain) while still meaning a DTFx replay of
            RecordTenantTokenUsageActivity for the SAME run never double-appends. Whole-run RunId
            is unique by construction (InsightsRunId.For), so this correctly scopes to "has THIS
            run already been recorded", not "has this tenant ever hit this token total before".  */
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.InsightsTenantTokenUsage WHERE RunId = @RunId)
                INSERT INTO dbo.InsightsTenantTokenUsage (CustomerID, RunId, TotalTokens)
                VALUES (@CustomerId, @RunId, @TotalTokens);
            """;

        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { CustomerId = customerId, RunId = runId, TotalTokens = totalTokens }, cancellationToken: cancellationToken));
    }
}

using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IInsightJsonRepository"/>
public sealed class SqlInsightJsonRepository(string connectionString) : IInsightJsonRepository
{
    public async Task<bool> TryClaimPostAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "dbo.usp_Insights_InsightJsonClaimPost",
                new { CustomerID = customerId, UserID = userId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue) },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task RecordOutcomeAsync(int customerId, long userId, DateOnly weekEnding, string outcome, string? detail = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_InsightJsonRecordOutcome",
                new { CustomerID = customerId, UserID = userId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue), Outcome = outcome, Detail = detail },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task ReleaseClaimAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_InsightJsonReleaseClaim",
                new { CustomerID = customerId, UserID = userId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue) },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }
}

using Dapper;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IAgentReasoningRecorder"/>
public sealed class SqlAgentReasoningRecorder(string connectionString) : IAgentReasoningRecorder
{
    public async Task RecordAsync(string runId, string stage, string? reasoningSummary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reasoningSummary))
            return;

        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            INSERT INTO dbo.InsightsAgentReasoningLog (RunId, Stage, ReasoningSummary)
            VALUES (@RunId, @Stage, @ReasoningSummary);
            """;

        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { RunId = runId, Stage = stage, ReasoningSummary = reasoningSummary }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AgentReasoningLogEntry>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT Stage, ReasoningSummary, RecordedAtUtc
            FROM dbo.InsightsAgentReasoningLog
            WHERE RunId = @RunId
            ORDER BY Id;
            """;

        var rows = await connection.QueryAsync<AgentReasoningLogEntry>(
            new CommandDefinition(sql, new { RunId = runId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}

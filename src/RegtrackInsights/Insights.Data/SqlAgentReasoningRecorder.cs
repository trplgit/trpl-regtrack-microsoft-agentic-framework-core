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
}

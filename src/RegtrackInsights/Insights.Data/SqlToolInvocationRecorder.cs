using Dapper;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IToolInvocationRecorder"/>
public sealed class SqlToolInvocationRecorder(string connectionString) : IToolInvocationRecorder
{
    public async Task RecordAsync(
        string? runId, string stage, string toolName, string detail, bool success, int? resultLength,
        CancellationToken cancellationToken = default)
    {
        // Same "no runId, nothing to key this row on" stance IAgentReasoningRecorder takes on an
        // empty reasoningSummary - a lab/manual call with no real DTFx run behind it has nothing
        // for a future "did run X call the tool" query to find this row by.
        if (string.IsNullOrEmpty(runId))
            return;

        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            INSERT INTO dbo.InsightsToolInvocationLog (RunId, Stage, ToolName, Detail, Success, ResultLength)
            VALUES (@RunId, @Stage, @ToolName, @Detail, @Success, @ResultLength);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { RunId = runId, Stage = stage, ToolName = toolName, Detail = detail, Success = success, ResultLength = resultLength },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ToolInvocationLogEntry>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT Stage, ToolName, Detail, Success, ResultLength, RecordedAtUtc
            FROM dbo.InsightsToolInvocationLog
            WHERE RunId = @RunId
            ORDER BY Id;
            """;

        var rows = await connection.QueryAsync<ToolInvocationLogEntry>(
            new CommandDefinition(sql, new { RunId = runId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}

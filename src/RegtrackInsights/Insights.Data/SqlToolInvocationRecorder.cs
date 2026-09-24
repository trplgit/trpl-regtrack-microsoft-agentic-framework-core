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
}

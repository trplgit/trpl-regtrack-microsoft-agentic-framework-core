using Microsoft.Data.SqlClient;

namespace DrainCheck;

/// <summary>
/// Counts in-flight (Pending or Running) instances of a given orchestrator version in a real
/// Durable Task SQL provider hub - dt.Instances.RuntimeStatus is the real column name (confirmed
/// against the schema directly, NOT "Status" - see the design spec's section 2 and CLAUDE.md
/// section 13, "believe the data, not the column name").
/// </summary>
public static class DrainCheckQuery
{
    public static async Task<int> CountInFlightAsync(string connectionString, string version, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dt.Instances WHERE Version = @version AND RuntimeStatus IN ('Pending', 'Running')";
        command.Parameters.AddWithValue("@version", version);

        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }
}

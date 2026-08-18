using System.Data;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IGoldenRegressionRepository"/>
public sealed class SqlGoldenRegressionRepository(string connectionString) : IGoldenRegressionRepository
{
    /// <summary>Error raised by usp_Insights_GoldenInvariants when any G-1..G-9 row fails.</summary>
    private const int GoldenRegressionFailedErrorNumber = 51002;

    public async Task<GoldenRegressionRun> RunAsync(int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<GoldenInvariantResult>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_Insights_GoldenInvariants";
        command.Parameters.Add(new SqlParameter("@CustomerID", SqlDbType.Int) { Value = customerId });
        command.Parameters.Add(new SqlParameter("@AsOf", SqlDbType.DateTime) { Value = (object?)asOf ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // [TRAP] The proc SELECTs the full result grid, THEN THROWs (51002) if any row
        // failed - fail loud, by design (CLAUDE.md #2). That THROW surfaces here as a
        // SqlException on the read that would otherwise signal "no more rows", i.e.
        // AFTER every row has already been added to `rows`. Dapper's buffered
        // QueryAsync<T> would lose all of them at that point, because the exception
        // propagates out of Dapper's own internal loop before it ever returns the list
        // it built. Reading with a raw SqlDataReader and catching ONLY this specific,
        // expected error keeps the row-level detail intact for the caller either way -
        // that detail is the whole point of calling this proc.
        try
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new GoldenInvariantResult(
                    TestId: reader.GetString(0),
                    TestName: reader.GetString(1),
                    Result: reader.GetString(2),
                    Detail: reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            }
        }
        catch (SqlException ex) when (ex.Number == GoldenRegressionFailedErrorNumber)
        {
            // Expected outcome when one or more invariants fail - `rows` already holds
            // the complete, ordered result grid the proc printed before it threw.
        }

        return new GoldenRegressionRun(customerId, rows);
    }
}

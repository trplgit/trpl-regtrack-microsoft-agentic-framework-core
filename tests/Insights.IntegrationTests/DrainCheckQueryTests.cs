using Microsoft.Data.SqlClient;
using Xunit;

namespace Insights.IntegrationTests;

public class DrainCheckQueryTests : IAsyncLifetime
{
    private readonly string _databaseName = $"InsightsDrainCheckTest_{Guid.NewGuid():N}";
    private string _masterConnectionString = "";
    private string _testDbConnectionString = "";

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _databaseName;
        _testDbConnectionString = builder.ConnectionString;

        await using (var master = new SqlConnection(_masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = new SqlConnection(_testDbConnectionString);
        await db.OpenAsync();

        // CREATE SCHEMA must be the only statement in its batch - SqlCommand sends CommandText as
        // one batch (no GO support), so this has to be two separate commands, not one string.
        await using (var createSchema = db.CreateCommand())
        {
            createSchema.CommandText = "CREATE SCHEMA dt;";
            await createSchema.ExecuteNonQueryAsync();
        }

        await using var createTable = db.CreateCommand();
        createTable.CommandText = @"
CREATE TABLE dt.Instances (
    InstanceID VARCHAR(100) NOT NULL PRIMARY KEY,
    Version VARCHAR(100) NULL,
    RuntimeStatus VARCHAR(50) NOT NULL
);";
        await createTable.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }

    private async Task InsertRow(string instanceId, string version, string runtimeStatus)
    {
        await using var db = new SqlConnection(_testDbConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO dt.Instances (InstanceID, Version, RuntimeStatus) VALUES (@id, @v, @s)";
        cmd.Parameters.AddWithValue("@id", instanceId);
        cmd.Parameters.AddWithValue("@v", version);
        cmd.Parameters.AddWithValue("@s", runtimeStatus);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CountInFlightAsync_CountsOnlyMatchingVersionAndInFlightStatus()
    {
        await InsertRow("i1", "4.4", "Pending");
        await InsertRow("i2", "4.4", "Running");
        await InsertRow("i3", "4.4", "Completed");   // wrong status - must not count
        await InsertRow("i4", "4.4.0", "Running");   // near-miss version string - must not count
        await InsertRow("i5", "4.3", "Running");     // wrong version - must not count

        var count = await DrainCheck.DrainCheckQuery.CountInFlightAsync(_testDbConnectionString, "4.4");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountInFlightAsync_ReturnsZero_WhenNothingMatches()
    {
        await InsertRow("i1", "4.5", "Running");

        var count = await DrainCheck.DrainCheckQuery.CountInFlightAsync(_testDbConnectionString, "4.4");

        Assert.Equal(0, count);
    }
}

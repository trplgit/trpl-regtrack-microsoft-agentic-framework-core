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

        await CreateDisposableDatabaseAsync(_masterConnectionString, _testDbConnectionString, _databaseName);
    }

    public async Task DisposeAsync() => await DropDatabaseAsync(_masterConnectionString, _databaseName);

    /// <summary>
    /// [FOUND LIVE 2026-09-30] A syntax error in the schema/table setup (CREATE SCHEMA + CREATE
    /// TABLE combined into one batch - invalid, CREATE SCHEMA must be the only statement in its
    /// batch) threw AFTER the CREATE DATABASE below had already succeeded, leaving two real
    /// databases behind on UAT with no test left running to clean them up (found and dropped
    /// manually afterward). This wraps the schema/table step in try/catch specifically so any
    /// future setup failure - a bad SQL edit, a permissions change, anything - drops the database
    /// before rethrowing rather than leaking it again.
    /// </summary>
    internal static async Task CreateDisposableDatabaseAsync(string masterConnectionString, string testDbConnectionString, string databaseName)
    {
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await CreateInstancesTableAsync(testDbConnectionString);
        }
        catch
        {
            await DropDatabaseAsync(masterConnectionString, databaseName);
            throw;
        }
    }

    internal static async Task CreateInstancesTableAsync(string testDbConnectionString)
    {
        await using var db = new SqlConnection(testDbConnectionString);
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

    internal static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
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

/// <summary>
/// [ADDED 2026-09-30, code review finding] Proves DrainCheckQueryTests.CreateDisposableDatabaseAsync's
/// try/catch actually drops the database on a real setup failure, not just on the happy path every
/// other test in this file exercises. Reproduces the exact failure class that leaked two real
/// databases on UAT (a schema/table setup step throwing after CREATE DATABASE already succeeded) by
/// forcing a genuine duplicate-schema SqlException, using the SAME reusable methods
/// CreateDisposableDatabaseAsync is built from.
/// </summary>
public class DrainCheckDatabaseLeakSafetyTests
{
    [Fact]
    public async Task SchemaSetupFailure_NeverLeavesADatabaseBehind()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        var databaseName = $"InsightsDrainCheckLeakTest_{Guid.NewGuid():N}";
        builder.InitialCatalog = "master";
        var masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = databaseName;
        var testDbConnectionString = builder.ConnectionString;

        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        // Force CreateInstancesTableAsync's own "CREATE SCHEMA dt;" step to hit a real,
        // deterministic SqlException (duplicate schema name) - same shape of failure (something
        // throws AFTER CREATE DATABASE already succeeded) that leaked two real databases on UAT
        // before this safeguard existed.
        await using (var db = new SqlConnection(testDbConnectionString))
        {
            await db.OpenAsync();
            await using var preCreateSchema = db.CreateCommand();
            preCreateSchema.CommandText = "CREATE SCHEMA dt;";
            await preCreateSchema.ExecuteNonQueryAsync();
        }

        var threw = false;
        try
        {
            await DrainCheckQueryTests.CreateInstancesTableAsync(testDbConnectionString);
        }
        catch (SqlException)
        {
            threw = true;
            await DrainCheckQueryTests.DropDatabaseAsync(masterConnectionString, databaseName);
        }

        Assert.True(threw, "Expected the pre-existing dt schema to make CreateInstancesTableAsync fail.");

        await using var verify = new SqlConnection(masterConnectionString);
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        check.Parameters.AddWithValue("@name", databaseName);
        var remaining = (int)(await check.ExecuteScalarAsync())!;
        Assert.Equal(0, remaining);
    }
}

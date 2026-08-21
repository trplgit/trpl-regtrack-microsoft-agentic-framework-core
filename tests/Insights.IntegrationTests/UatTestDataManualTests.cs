using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// One-off UAT test-data helpers - NOT part of the automated suite. Run explicitly, one at
/// a time, with --filter. Tenant 1490 has no ProductMapping rows in UAT at all, so these are
/// a clean insert/delete pair with nothing to collide with (see chat: 2026-08-19).
/// Requires ConnectionStrings__RegTrack - the UAT connection string carries a live sa
/// password and must never be hardcoded here even as a fallback.
/// </summary>
public sealed class UatTestDataManualTests(ITestOutputHelper output)
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack")
        ?? throw new InvalidOperationException("Set ConnectionStrings__RegTrack before running this manual test.");

    [Fact]
    public async Task InsertFreeTierMappingForTenant1490()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO ProductMapping (CustomerID, ProductID, IsActive, CreatedOn, CreatedBy) VALUES (1490, 18, 0, GETDATE(), 0);", connection);
        var rows = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task DeleteFreeTierMappingForTenant1490()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM ProductMapping WHERE CustomerID = 1490 AND ProductID = 18;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Build order item 11 (docs/superpowers/plans/2026-08-21-durable-task-orchestrator.md, Task 1
    /// step 2): provisions the dedicated task-hub database, separate from vitComplianceSystem, on
    /// the same UAT server. Idempotent (IF NOT EXISTS) so it is safe to re-run, e.g. after a dev
    /// reset. Connects using ConnectionStrings__RegTrack's server/credentials but does not touch
    /// vitComplianceSystem itself - CREATE DATABASE only requires the login to have the permission,
    /// not a master-database connection.
    /// </summary>
    [Fact]
    public async Task CreateInsightsTaskHubDatabase()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'vitInsightsTaskHub') CREATE DATABASE vitInsightsTaskHub;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Diagnostic for build order item 11's Task 17 (end-to-end orchestrator verification): every
    /// manual/integration test so far in this repo used tenant 23, which turns out NOT to be
    /// mapped-and-enabled for the paid product (19) - EvaluateGateAsync correctly refused it
    /// (2026-08-21). Lists real UAT tenants that ARE paid-entitled, [TRAP] IsActive is INVERTED
    /// (0 = enabled), so the WHERE clause checks = 0, not = 1.
    /// </summary>
    [Fact]
    public async Task ListPaidEntitledTenants()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 20 CustomerID FROM ProductMapping
            WHERE ProductID = 19 AND IsActive = 0
            ORDER BY CustomerID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"CustomerID={reader.GetInt64(0)}");
    }

    /// <summary>
    /// Companion to ListPaidEntitledTenants - finds a real (userId, customerId) scope pair for
    /// tenant 1285, the second (and last) paid-entitled UAT tenant found. Same join
    /// EntitiesAssignment-based approach DimensionRepositoryTests.ValidatedTenants used originally.
    /// </summary>
    [Fact]
    public async Task FindValidUserForTenant1285()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT DISTINCT TOP 5 ea.UserID
            FROM EntitiesAssignment ea
            JOIN CustomerBranch cb ON cb.ID = ea.BranchID
            JOIN Customer cu ON cu.ID = cb.CustomerID
            WHERE cb.CustomerID = 1285 AND cb.IsDeleted = 0 AND cu.IsDeleted = 0
            ORDER BY ea.UserID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"UserID={reader.GetInt64(0)}");
    }

    /// <summary>
    /// One-off diagnostic against the DURABLE TASK HUB database (not vitComplianceSystem) - checks
    /// the actual current status of the most recent orchestration instance directly via SQL,
    /// cheaper than re-running the full LLM pipeline again to find out whether a client-side
    /// WaitForOrchestrationAsync failure meant the orchestration itself also failed, or was still
    /// legitimately in progress. Table/schema names are Microsoft.DurableTask.SqlServer's own,
    /// discovered by listing INFORMATION_SCHEMA.TABLES first, not guessed.
    /// </summary>
    [Fact]
    public async Task InspectTaskHubTables()
    {
        var taskHubConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        await using var connection = new SqlConnection(taskHubConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"{reader.GetString(0)}.{reader.GetString(1)}");
    }

    /// <summary>Follows InspectTaskHubTables - dt.vInstances is Microsoft.DurableTask.SqlServer's own friendly view.</summary>
    [Fact]
    public async Task InspectRecentOrchestrationInstances()
    {
        var taskHubConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        await using var connection = new SqlConnection(taskHubConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT * FROM dt.vInstances ORDER BY CreatedTime DESC;", connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();
        var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        output.WriteLine(string.Join(" | ", columnNames));
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i)?.ToString() ?? "-");
            output.WriteLine(string.Join(" | ", values));
        }
    }
}

using Microsoft.Data.SqlClient;

namespace Insights.IntegrationTests;

/// <summary>
/// One-off UAT test-data helpers - NOT part of the automated suite. Run explicitly, one at
/// a time, with --filter. Tenant 1490 has no ProductMapping rows in UAT at all, so these are
/// a clean insert/delete pair with nothing to collide with (see chat: 2026-08-19).
/// Requires ConnectionStrings__RegTrack - the UAT connection string carries a live sa
/// password and must never be hardcoded here even as a fallback.
/// </summary>
public sealed class UatTestDataManualTests
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
}

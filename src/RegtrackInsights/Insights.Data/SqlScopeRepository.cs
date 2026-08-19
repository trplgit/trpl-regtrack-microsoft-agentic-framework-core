using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IScopeRepository"/>
public sealed class SqlScopeRepository(string connectionString) : IScopeRepository
{
    /// <summary>Error raised by usp_Insights_AuditScope when any out-of-scope row is found.</summary>
    private const int ScopeAuditFailedErrorNumber = 51010;

    public async Task<IReadOnlyList<ScopePair>> GetScopePairsAsync(int userId, int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<ScopePair>(
            new CommandDefinition(
                "SELECT BranchID, CategoryId FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID)",
                new { UserID = userId, CustomerID = customerId },
                cancellationToken: cancellationToken));

        return rows.AsList();
    }

    public async Task<ScopeClassification> ClassifyScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var row = await connection.QuerySingleAsync<ClassifyScopeRow>(
            new CommandDefinition(
                "dbo.usp_Insights_ClassifyScope",
                new { UserID = userId, CustomerID = customerId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return new ScopeClassification(
            row.UserID, row.CustomerID, row.ScopeBranches, row.TenantBranches,
            row.ScopeCategories, row.TenantCategories, ParseScopeClass(row.ScopeClass), row.IsDenied);
    }

    public async Task<ScopeAuditResult> AuditScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        try
        {
            var row = await connection.QuerySingleAsync<AuditScopeRow>(
                new CommandDefinition(
                    "dbo.usp_Insights_AuditScope",
                    new { UserID = userId, CustomerID = customerId },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: cancellationToken));

            return new ScopeAuditResult(row.BranchViolations, row.CategoryViolations, row.PairViolations);
        }
        catch (SqlException ex) when (ex.Number == ScopeAuditFailedErrorNumber)
        {
            // Unlike usp_Insights_GoldenInvariants, this proc THROWs BEFORE selecting
            // anything on failure - there is no result set to lose, so a plain Dapper
            // call (unlike the golden-regression wrapper) is safe here. Re-thrown as a
            // typed exception so callers can catch it specifically rather than parsing
            // SqlException.Number themselves.
            throw new ScopeAuditFailedException(userId, customerId, ex);
        }
    }

    public async Task<IReadOnlyList<ScopelessUser>> FindScopelessUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<ScopelessUser>(
            new CommandDefinition(
                "dbo.usp_Insights_FindScopelessUsers",
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return rows.AsList();
    }

    /// <summary>
    /// Fail closed on an unrecognised value, matching the dictionary's own coverage
    /// check (sql/01) - a ScopeClass this wrapper does not know about must never be
    /// silently treated as any particular access level.
    /// </summary>
    private static ScopeClass ParseScopeClass(string value) => value switch
    {
        "DENY" => ScopeClass.Deny,
        "tenant_wide" => ScopeClass.TenantWide,
        "functional" => ScopeClass.Functional,
        "entity_scoped" => ScopeClass.EntityScoped,
        _ => throw new InvalidOperationException($"Unknown ScopeClass '{value}' from usp_Insights_ClassifyScope."),
    };

    private sealed record ClassifyScopeRow(
        int UserID, int CustomerID, int ScopeBranches, int TenantBranches,
        int ScopeCategories, int TenantCategories, string ScopeClass, bool IsDenied);

    private sealed record AuditScopeRow(int BranchViolations, int CategoryViolations, int PairViolations, bool ScopeAuditPassed);
}

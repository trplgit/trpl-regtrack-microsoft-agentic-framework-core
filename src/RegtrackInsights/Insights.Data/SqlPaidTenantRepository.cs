using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IPaidTenantRepository"/>
public sealed class SqlPaidTenantRepository(string connectionString) : IPaidTenantRepository
{
    /// <summary>Product 19 = RegInsights Pro, the paid tier (spec Section 5.3).</summary>
    private const int PaidProductId = 19;

    public async Task<IReadOnlyList<PaidKeepWarmTenant>> GetEntitledTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        /*  [TRAP] ProductMapping.IsActive is INVERTED - 0 means ENABLED. Same predicate
            SqlFreeDigestRepository.GetEntitledTenantsAsync uses for product 18 - mirrored here,
            not shared, because the two lists have no other logic in common.                    */
        const string sql = """
            SELECT DISTINCT c.ID AS CustomerId, c.Name AS TenantName
            FROM ProductMapping pm
            JOIN Customer c ON c.ID = pm.CustomerID
            WHERE pm.ProductID = @PaidProductId
              AND pm.IsActive = 0
              AND c.IsDeleted = 0
            ORDER BY c.ID;
            """;

        var rows = await connection.QueryAsync<PaidKeepWarmTenant>(
            new CommandDefinition(sql, new { PaidProductId }, cancellationToken: cancellationToken));

        return rows.AsList();
    }
}

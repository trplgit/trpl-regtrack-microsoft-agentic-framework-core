using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="ITenantDirectoryRepository"/>
public sealed class SqlTenantDirectoryRepository(string connectionString) : ITenantDirectoryRepository
{
    public async Task<IReadOnlyList<EligibleTenant>> GetEligibleTenantsAsync(
        int userId, CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(userId, customerId: null, cancellationToken);
        return rows;
    }

    public async Task<EligibleTenant?> IsEligibleAsync(
        int userId, int customerId, CancellationToken cancellationToken = default)
    {
        /*  Pushes the filter into the proc rather than fetching every tenant and scanning in C#.
            Not an optimisation: a user with one eligible tenant and a forged id for another must
            cost the same as one asking about their own, and must exercise the SAME predicate.
            Two code paths deciding eligibility is how the two drift apart.                      */
        var rows = await QueryAsync(userId, customerId, cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<IReadOnlyList<EligibleTenant>> QueryAsync(
        int userId, int? customerId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<EligibleTenantRow>(
            new CommandDefinition(
                "dbo.usp_Insights_EligibleTenants",
                new { UserID = userId, CustomerID = customerId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return rows.Select(r => new EligibleTenant(
                r.TenantId, r.Name, ParseTier(r.Tier), ScopeClassNames.Parse(r.ScopeClass)))
            .ToList();
    }

    /// <summary>
    /// Fail closed on an unrecognised tier, exactly as ParseScopeClass does. Defaulting an
    /// unknown value to 'basic' would silently withhold a paid feature from a paying tenant;
    /// defaulting it to 'pro' would hand paid features to a free one.
    /// </summary>
    private static EntitlementTier ParseTier(string value) => value switch
    {
        "basic" => EntitlementTier.Free,
        "pro" => EntitlementTier.Paid,
        _ => throw new InvalidOperationException($"Unknown Tier '{value}' from usp_Insights_EligibleTenants."),
    };

    private sealed record EligibleTenantRow(int TenantId, string Name, string Tier, string ScopeClass);
}

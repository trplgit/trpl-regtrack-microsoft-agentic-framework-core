using Dapper;
using Microsoft.Data.SqlClient;

namespace Insights.Data.Email;

/// <summary>Resolves which provider a tenant's digest is sent through. See <see cref="EmailGatewayRules"/>.</summary>
public interface IEmailGatewayResolver
{
    Task<EmailGatewayResolution> ResolveAsync(int tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// READ-ONLY lookup of the platform's own routing tables (owned by RegTrack, not this project -
/// nothing here writes to them). Inline SQL rather than a stored procedure: two small reads, and
/// keeping it in C# means no database deployment is needed to ship per-tenant routing.
///
/// The tenant comes from the orchestration input, never from recipient data - the platform's own
/// lookup derives CustomerID from the recipient's email, which is the IDOR shape this codebase
/// forbids.
/// </summary>
public sealed class SqlEmailGatewayResolver(string connectionString, EmailGateway defaultGateway) : IEmailGatewayResolver
{
    public async Task<EmailGatewayResolution> ResolveAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT ID, CustomerID, EmailGateWayType, EmailFailoverCondition, EmailFailoverSenderID, UpdatedOn
            FROM dbo.EmailDeliveryGatewayCustomization
            WHERE CustomerID = @CustomerID
              AND IsActive = 1;

            SELECT ID, IsActive
            FROM dbo.EmailGatewayMaster;
            """;

        using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, new { CustomerID = (long)tenantId }, cancellationToken: cancellationToken));

        var rows = (await grid.ReadAsync<EmailGatewayCustomizationRow>()).AsList();
        var masters = (await grid.ReadAsync<EmailGatewayMasterRow>()).AsList();

        return EmailGatewayRules.Resolve(tenantId, rows, masters, defaultGateway);
    }
}

using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IFreeDigestRepository"/>
public sealed class SqlFreeDigestRepository(string connectionString) : IFreeDigestRepository
{
    /// <summary>Raised by usp_Insights_FreeDigestAggregates when Critical RiskType is unmapped.</summary>
    private const int FreeDigestDictionaryGapErrorNumber = 51040;

    /// <summary>Product 18 = RegInsights Basic, the free tier (spec Section 5.3).</summary>
    private const int FreeProductId = 18;

    public async Task<FreeDigestGateResult> EvaluateGateAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var row = await connection.QuerySingleAsync<GateRow>(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestGate",
                new { CustomerID = customerId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return new FreeDigestGateResult(
            row.CustomerID, ParseDecision(row.Decision), row.RecipientCount, row.Reason, row.ShouldProceed);
    }

    public async Task<FreeDigestAggregates> GetAggregatesAsync(
        int customerId, int? userId = null, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        try
        {
            using var multi = await connection.QueryMultipleAsync(
                new CommandDefinition(
                    "dbo.usp_Insights_FreeDigestAggregates",
                    new { CustomerID = customerId, UserID = userId, AsOf = asOf },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: cancellationToken));

            /*  [TRAP] A NESTED EXEC ADDS A RESULT SET.
                This proc opens with
                    EXEC dbo.usp_Insights_AssertStatusCoverage;
                and that proc ends in SELECT CAST(1 AS BIT) AS StatusCoverageComplete. Its row
                arrives FIRST, so a plain QuerySingle binds the coverage flag instead of the
                aggregates and fails with a materialisation error naming StatusCoverageComplete.
                Skip that grid, then read the fifteen numbers from the second.                */
            await multi.ReadAsync();

            return await multi.ReadSingleAsync<FreeDigestAggregates>();
        }
        catch (SqlException ex) when (ex.Number == FreeDigestDictionaryGapErrorNumber)
        {
            // The 51040 THROW lands after the coverage grid but before the aggregates,
            // so the only result set in hand is the one we already discarded.
            throw new FreeDigestDictionaryGapException(customerId, ex);
        }
    }

    /// <summary>
    /// Shares the vocabulary of usp_Insights_EvaluateGate deliberately - the two gates are the
    /// same decision expressed for different callers, and must not drift apart. Fails closed on
    /// an unrecognised value rather than assuming the permissive one.
    /// </summary>
    private static EntitlementDecision ParseDecision(string value) => value switch
    {
        "PROCEED" => EntitlementDecision.Proceed,
        "EXIT_ZERO_COST" => EntitlementDecision.ExitZeroCost,
        "EXIT_SUPERSEDED" => EntitlementDecision.ExitSuperseded,
        "EXIT_NO_RECIPIENTS" => EntitlementDecision.ExitNoRecipients,
        _ => throw new InvalidOperationException($"Unknown Decision '{value}' from usp_Insights_FreeDigestGate."),
    };

    private sealed record GateRow(int CustomerID, string Decision, int RecipientCount, string Reason, bool ShouldProceed);

    public async Task<IReadOnlyList<FreeDigestTenant>> GetEntitledTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        /*  [TRAP] ProductMapping.IsActive is INVERTED - 0 means ENABLED. Confirmed in the
            dictionary: the core Compliance product has ~1,865 customers mapped with only 7 at
            IsActive = 1, i.e. 7 DISABLED. Never "correct" this to = 1.                       */
        const string sql = """
            SELECT DISTINCT c.ID AS CustomerId, c.Name AS TenantName
            FROM ProductMapping pm
            JOIN Customer c ON c.ID = pm.CustomerID
            WHERE pm.ProductID = @FreeProductId
              AND pm.IsActive = 0
              AND c.IsDeleted = 0
            ORDER BY c.ID;
            """;

        var rows = await connection.QueryAsync<FreeDigestTenant>(
            new CommandDefinition(sql, new { FreeProductId = FreeProductId }, cancellationToken: cancellationToken));

        return rows.AsList();
    }

    public async Task<IReadOnlyList<FreeDigestRecipient>> GetRecipientsAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        /*  This predicate MIRRORS usp_Insights_FreeDigestGate's recipient count exactly -
            ProductID 18, UserCustomerMapping.IsActive = 0 (INVERTED), User.IsDeleted = 0.
            If the two drift apart the gate's EXIT_NO_RECIPIENTS stops meaning anything.

            Users with a null or blank email are excluded HERE rather than left to fail at the
            provider: a send failure looks like an outage, a missing address is a data gap.    */
        const string sql = """
            SELECT DISTINCT
                u.ID AS UserId,
                u.Email,
                LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) AS Name
            FROM UserCustomerMapping ucm
            JOIN [User] u ON u.ID = ucm.UserID
            WHERE ucm.CustomerID = @CustomerID
              AND ucm.ProductID = @FreeProductId
              AND ucm.IsActive = 0
              AND u.IsDeleted = 0
              AND NULLIF(LTRIM(RTRIM(u.Email)), N'') IS NOT NULL
            ORDER BY u.ID;
            """;

        var rows = await connection.QueryAsync<FreeDigestRecipient>(
            new CommandDefinition(sql, new { CustomerID = customerId, FreeProductId = FreeProductId }, cancellationToken: cancellationToken));

        return rows.AsList();
    }
}


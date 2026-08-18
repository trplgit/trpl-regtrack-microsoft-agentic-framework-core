using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IEntitlementRepository"/>
public sealed class SqlEntitlementRepository(string connectionString) : IEntitlementRepository
{
    public async Task<EntitlementGateResult> EvaluateGateAsync(int customerId, EntitlementTier tier, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var row = await connection.QuerySingleAsync<GateRow>(
            new CommandDefinition(
                "dbo.usp_Insights_EvaluateGate",
                new { CustomerID = customerId, Tier = ToSqlTier(tier) },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        // @Tier is an echo of the input, not re-derived - use the typed value we
        // already have rather than re-parsing the proc's own echoed string.
        return new EntitlementGateResult(row.CustomerID, tier, ParseDecision(row.Decision), row.RecipientCount, row.Reason, row.ShouldProceed);
    }

    private static string ToSqlTier(EntitlementTier tier) => tier switch
    {
        EntitlementTier.Free => "free",
        EntitlementTier.Paid => "paid",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    private static EntitlementDecision ParseDecision(string value) => value switch
    {
        "PROCEED" => EntitlementDecision.Proceed,
        "EXIT_ZERO_COST" => EntitlementDecision.ExitZeroCost,
        "EXIT_SUPERSEDED" => EntitlementDecision.ExitSuperseded,
        "EXIT_NO_RECIPIENTS" => EntitlementDecision.ExitNoRecipients,
        _ => throw new InvalidOperationException($"Unknown Decision '{value}' from usp_Insights_EvaluateGate."),
    };

    private sealed record GateRow(int CustomerID, string Tier, string Decision, int RecipientCount, string Reason, bool ShouldProceed);
}

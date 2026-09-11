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

            /*  [TRAP - THE CONTRACT MOVED UNDER THIS CODE] There is NO coverage grid to skip.

                usp_Insights_AssertStatusCoverage USED to end in
                SELECT CAST(1 AS BIT) AS StatusCoverageComplete, so its row arrived as result set
                #1 and this read had to step over it. The proc was corrected to return nothing at
                all ("success is silence; failure is a THROW") - verified: zero occurrences of
                StatusCoverageComplete in sql/01 today.

                Skipping a grid that no longer exists consumes the AGGREGATES, and the read below
                then finds an exhausted reader: "The reader has been disposed; this can happen
                after all data has been consumed". The aggregates are result set #1. Read them
                directly.

                The same stale skip existed in SqlDimensionRepository and was fixed there; this
                copy was reverted by a merge and had to be fixed twice. If a proc ever regains a
                pre-flight SELECT, this fails loudly as a Dapper materialisation error naming the
                columns it could not bind - not as silently wrong numbers.                       */

            return await multi.ReadSingleAsync<FreeDigestAggregates>();
        }
        catch (SqlException ex) when (ex.Number == FreeDigestDictionaryGapErrorNumber)
        {
            // 51040 is the dictionary-gap THROW raised by the proc before it selects anything,
            // so there is no result set to salvage - only the typed refusal to surface.
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

        /*  [CORRECTED 2026-09-10] Recipients come from dbo.tvfInsightsManagementUsers, NOT
            UserCustomerMapping - that table cannot answer this question in production. All 65
            production rows carry ProductID = NULL and IsActive = 1 (see sql/01), so the old
            predicate (ProductID = 18, IsActive = 0) matched nothing, ever. The gate
            (usp_Insights_FreeDigestGate) was corrected to the TVF on 2026-09-08; this method
            was missed until confirmed live against tenant 1300 (gate: 13 recipients via the
            TVF, this query: 0 via UserCustomerMapping).

            DISTINCT on UserID happens in a DERIVED TABLE, before the join to [User] - not as
            SELECT DISTINCT over the final columns. The TVF returns one row per (user, branch,
            category) - 236K+ rows for a large tenant against a few hundred actual users.
            Deduplicating on the key before the join makes one-row-per-user STRUCTURAL, not an
            accident of Email/Name happening to be functionally dependent on the id today.

            No ProductID filter: ComplianceCategoryMgmtUser (which the TVF reads) has no
            ProductID column. Product entitlement is checked upstream by both callers
            (ResolveDigestRecipientsActivity, ResolveDigestDispatchActivity) before this method
            runs - this list intentionally answers "who", not "is this tenant entitled".

            Users with a null or blank email are excluded HERE rather than left to fail at the
            provider: a send failure looks like an outage, a missing address is a data gap.

            DURABLE OPT-OUTS are filtered HERE, not only in the gate, even though the gate (as
            of 2026-09-10) now also subtracts them from its count: the gate exits
            EXIT_NO_RECIPIENTS only when EVERY recipient has opted out. When only SOME have, the
            gate proceeds, and without this clause the list below would still contain them - an
            unsubscribed user on a multi-recipient tenant would keep receiving the digest. Keyed
            (CustomerID, UserID), so opting out of one tenant leaves a conglomerate user
            subscribed to the others.

            This query and the gate's can still disagree in count - the gate's is an upper-bound
            cost pre-filter (no email check), this list is authoritative. That is safe: an empty
            list here still exits before any LLM spend (ResolveDigestRecipientsActivity).       */
        const string sql = """
            SELECT u.ID AS UserId,
                   u.Email,
                   LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))) AS Name
            FROM (SELECT DISTINCT m.UserID FROM dbo.tvfInsightsManagementUsers(@CustomerID) m) mu
            JOIN [User] u ON u.ID = mu.UserID
            WHERE u.IsDeleted = 0
              AND NULLIF(LTRIM(RTRIM(u.Email)), N'') IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.InsightsDigestSuppression s
                              WHERE s.CustomerID = @CustomerID
                                AND s.UserID     = u.ID)
            ORDER BY u.ID;
            """;

        var rows = await connection.QueryAsync<FreeDigestRecipient>(
            new CommandDefinition(sql, new { CustomerID = customerId }, cancellationToken: cancellationToken));

        return rows.AsList();
    }

    /*  [TRAP] DAPPER CANNOT BIND DateOnly.
        Dapper 2.1.66 throws "The member WeekEnding of type System.DateOnly cannot be used as a
        parameter value" - it predates the type. So DateOnly stays in the public API, where it is
        the correct type (it makes a time-of-day component impossible, and the whole point of the
        claim key is that two runs on different days of the same week collide), and is converted
        to DateTime at midnight only here, at the Dapper boundary. The column is DATE, so the
        time component is discarded on the way in.

        The alternative is a global SqlMapper.AddTypeHandler<DateOnly>, which would fix every
        future call site too - worth doing if DateOnly spreads, but a global behaviour change for
        three call sites is the bigger surprise.                                                */
    public async Task<IReadOnlyList<long>> GetClaimedUserIdsAsync(
        int customerId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        /*  A plain read of the claim table, deliberately NOT a call to usp_Insights_FreeDigestClaimSend -
            that proc CLAIMS, and claiming here would consume every recipient before a single email
            was composed. This only asks who already holds a claim.

            Any row for the week counts, whatever its Outcome: a row with Outcome NULL is a run
            still in flight or one that died mid-send, and in both cases this recipient must not be
            composed for again. Filtering to Outcome = sent would re-compose exactly the recipients
            whose previous attempt is unresolved.                                                  */
        const string sql = """
            SELECT UserID
            FROM dbo.InsightsFreeDigestLog
            WHERE CustomerID = @CustomerID
              AND WeekEnding = @WeekEnding;
            """;

        // [TRAP] Dapper 2.1.66 cannot bind DateOnly - converted at this boundary, as everywhere
        // else in this class. The column is DATE, so the midnight component is discarded by SQL.
        var rows = await connection.QueryAsync<long>(
            new CommandDefinition(sql,
                new { CustomerID = customerId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue) },
                cancellationToken: cancellationToken));

        return rows.AsList();
    }

    public async Task<bool> TryClaimSendAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        return await connection.QuerySingleAsync<bool>(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestClaimSend",
                new { CustomerID = customerId, UserID = userId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue) },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task RecordOutcomeAsync(int customerId, long userId, DateOnly weekEnding, string outcome,
        string? source = null, string? providerUsed = null, string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestRecordOutcome",
                new
                {
                    CustomerID = customerId,
                    UserID = userId,
                    WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue),
                    Outcome = outcome,
                    Source = source,
                    ProviderUsed = providerUsed,
                    // The column is NVARCHAR(400); a long validator explanation must not blow up the write.
                    Detail = detail is { Length: > 400 } ? detail[..400] : detail,
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task ReleaseClaimAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestReleaseClaim",
                new { CustomerID = customerId, UserID = userId, WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue) },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task SuppressAsync(int customerId, long userId, DigestSuppressionReason reason,
        string? detail = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_DigestSuppress",
                new
                {
                    CustomerID = customerId,
                    UserID = userId,
                    Reason = reason.ToSqlValue(),
                    Detail = detail is { Length: > 400 } ? detail[..400] : detail,
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DigestSuppression>> GetSuppressionsAsync(
        int? customerId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<SuppressionRow>(
            new CommandDefinition(
                "dbo.usp_Insights_DigestSuppressionList",
                new { CustomerID = customerId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return rows.Select(r => new DigestSuppression(
            r.CustomerID, r.UserID, r.Email, ParseReason(r.Reason), r.SuppressedAtUtc, r.Detail)).ToList();
    }

    /// <summary>Fails closed - an unknown reason must not be silently treated as a benign one.</summary>
    private static DigestSuppressionReason ParseReason(string value) => value switch
    {
        "unsubscribed" => DigestSuppressionReason.Unsubscribed,
        "hard_bounce" => DigestSuppressionReason.HardBounce,
        "manual" => DigestSuppressionReason.Manual,
        _ => throw new InvalidOperationException($"Unknown suppression Reason '{value}' from InsightsDigestSuppression."),
    };

    private sealed record SuppressionRow(int CustomerID, long UserID, string? Email, string Reason, DateTime SuppressedAtUtc, string? Detail);
}




using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <summary>
/// Reads one monthly free-digest slot (sql/36-41). Same scope as the weekly digest - the procs resolve
/// scope through tvfInsightsScopedInstances for <c>userId</c>, exactly as sql/06 does.
/// </summary>
public interface IFreeMonthlyDigestRepository
{
    /// <exception cref="FreeMonthlyDigestRefusedException">The proc THROWs a 51230-51309 code - fail closed.</exception>
    Task<MonthlyDigestData> GetSlotAsync(
        MonthlyDigestEdition edition, int customerId, int userId, DateTime asOf, bool allowPersonNames,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IFreeMonthlyDigestRepository"/>
public sealed class SqlFreeMonthlyDigestRepository(string connectionString) : IFreeMonthlyDigestRepository
{
    /// <summary>
    /// LoadFacts over a very large tenant (1216: 1.49M past-due schedules) is unmeasured - see
    /// docs/FREE_TIER_MONTHLY_SQL_HANDOFF.md. The default 30s would fail it before SQL has a chance
    /// to; a genuinely wedged call still surfaces as a timeout rather than hanging the activity.
    /// </summary>
    private const int CommandTimeoutSeconds = 300;

    public async Task<MonthlyDigestData> GetSlotAsync(
        MonthlyDigestEdition edition, int customerId, int userId, DateTime asOf, bool allowPersonNames,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var parameters = new DynamicParameters();
        parameters.Add("UserID", userId);
        parameters.Add("CustomerID", customerId);
        // [TRAP] Dapper 2.1.66 cannot bind DateOnly - see SqlFreeDigestRepository. The column side is DATE.
        parameters.Add("CurrMonthStart", edition.CurrMonthStart.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("AsOf", asOf, DbType.DateTime);
        if (edition.Slot == MonthlyDigestSlot.Users)
            parameters.Add("AllowPersonNames", allowPersonNames);

        try
        {
            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
                ProcFor(edition.Slot), parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            /*  Five result sets, in contract order (sql/36 Sec.8). The helpers (sql/34, 35, 37)
                return NO grid, so control_totals is result set #1 - if a helper ever regains a
                SELECT, the column-name checks below fail loudly instead of shifting silently. */
            var control = (IDictionary<string, object?>)await multi.ReadSingleAsync();
            RequireResultSet(control, "control_totals");

            var facts = (await multi.ReadAsync<FactRow>()).Select(r => r.ToDomain()).ToList();
            var policy = (await multi.ReadAsync<PolicyRow>()).Select(r => r.ToDomain()).ToList();
            var candidates = (await multi.ReadAsync<CandidateRow>()).Select(r => r.ToDomain()).ToList();
            var quality = (await multi.ReadAsync<QualityRow>()).Select(r => r.ToDomain()).ToList();

            var headlineSource = control.TryGetValue("HeadlineSource", out var hs) ? hs as string : null;
            if (headlineSource is not ("fact" or "candidate"))
                throw new InvalidOperationException($"{ProcFor(edition.Slot)} returned HeadlineSource '{headlineSource}' - expected 'fact' or 'candidate'.");

            return new MonthlyDigestData(edition, asOf, headlineSource, facts, policy, candidates, quality);
        }
        catch (SqlException ex) when (FreeMonthlyDigestRefusedException.IsMonthlyErrorNumber(ex.Number))
        {
            throw new FreeMonthlyDigestRefusedException(ex.Number, ex.Message, ex);
        }
    }

    internal static string ProcFor(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => "dbo.usp_Insights_FreeMonthly_Overview",
        MonthlyDigestSlot.Users => "dbo.usp_Insights_FreeMonthly_Users",
        MonthlyDigestSlot.Location => "dbo.usp_Insights_FreeMonthly_Location",
        MonthlyDigestSlot.Act => "dbo.usp_Insights_FreeMonthly_Act",
        MonthlyDigestSlot.Licence => "dbo.usp_Insights_FreeMonthly_Licence",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };

    private static void RequireResultSet(IDictionary<string, object?> row, string expected)
    {
        if (!row.TryGetValue("ResultSet", out var value) || !string.Equals(value as string, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Monthly digest result set #1 is '{value}', expected '{expected}' - the proc contract has shifted.");
    }

    /*  Mutable row classes, not positional records: the columns are TINYINT/BIGINT/DECIMAL, and
        Dapper converts those onto int/long/decimal PROPERTIES but requires an exact type match for
        constructor parameters. The domain records stay immutable.                              */

    private sealed class FactRow
    {
        public string FactKey { get; set; } = "";
        public int FactValue { get; set; }
        public string DisplayLabel { get; set; } = "";
        public string Section { get; set; } = "";
        public int DisplayOrder { get; set; }
        public string WindowScope { get; set; } = "";
        public string ImpactClass { get; set; } = "";
        public int SeverityTier { get; set; }
        public bool AsAtRequired { get; set; }
        public bool IsHeadline { get; set; }

        public MonthlyFact ToDomain() => new(FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, IsHeadline);
    }

    private sealed class PolicyRow
    {
        public string Detector { get; set; } = "";
        public int Eligible { get; set; }
        public int Flagged { get; set; }
        public decimal? FlaggedPct { get; set; }
        public string? EmitMode { get; set; }
        public string? Note { get; set; }

        public MonthlyDetectorPolicy ToDomain() => new(Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note);
    }

    private sealed class CandidateRow
    {
        public int? DefaultSlot { get; set; }
        public string Detector { get; set; } = "";
        public int Priority { get; set; }
        public int SeverityTier { get; set; }
        public int RankInDetector { get; set; }
        public string EntityKind { get; set; } = "";
        public long? EntityId { get; set; }
        public string? EntityLabel { get; set; }
        public string? ContextKind { get; set; }
        public string? ContextLabel { get; set; }
        public string Metric { get; set; } = "";
        public int? MetricPct { get; set; }
        public int? TenantPct { get; set; }
        public int? ItemCount { get; set; }
        public int? BaseCount { get; set; }
        public DateTime? EventDate { get; set; }
        public bool AsAtRequired { get; set; }
        public int ProblemCount { get; set; }
        public int PopulationCount { get; set; }
        public int ResidualCount { get; set; }

        public MonthlyCandidate ToDomain() => new(
            DefaultSlot, Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
            ContextKind, ContextLabel, Metric, MetricPct, TenantPct, ItemCount, BaseCount, EventDate, AsAtRequired,
            ProblemCount, PopulationCount, ResidualCount);
    }

    private sealed class QualityRow
    {
        public string Code { get; set; } = "";
        public int ItemCount { get; set; }
        public string Detail { get; set; } = "";

        public MonthlyDataQuality ToDomain() => new(Code, ItemCount, Detail);
    }
}

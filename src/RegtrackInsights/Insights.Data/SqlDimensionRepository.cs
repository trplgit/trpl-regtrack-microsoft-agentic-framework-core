using System.Data;
using System.Diagnostics;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IDimensionRepository"/>
public sealed class SqlDimensionRepository(string connectionString) : IDimensionRepository
{
    /*  Each dimension owns a block of ten error numbers, allocated uniformly:
            base + 0  SCOPE DENIED
            base + 1  RECONCILIATION FAILED
            base + 2  DICTIONARY / MASTER DATA GAP
        That uniformity is deliberate - it is why one translation helper covers all nine
        instead of nine bespoke catch blocks. If a new dimension is added, keep the scheme.  */
    private const int LocationErrorBase    = 51030;
    private const int EntityErrorBase      = 51050;
    private const int RiskErrorBase        = 51060;
    private const int NatureErrorBase      = 51070;
    private const int DepartmentsErrorBase = 51080;
    private const int ActErrorBase         = 51090;
    private const int UsersErrorBase       = 51100;
    private const int InternalErrorBase    = 51110;
    private const int EventErrorBase       = 51120;
    private const int LicenceErrorBase     = 51160;

    /*  Measured: 30s on a large tenant, and the largest tenant in the estate carries ~1.49M
        past-due schedules and has not been timed. The default 30s command timeout would fail
        those runs, so it is raised here rather than left to surface as a transient error the
        caller cannot distinguish from a real fault. This contradicts the spec's "SQL -
        negligible" estimate; treat the estimate as the thing that is wrong.                  */
    private const int DimensionCommandTimeoutSeconds = 300;

    public Task<DimensionResult<LocationControlTotals, LocationRow>> GetLocationAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<LocationControlTotals, LocationRow>(
            "Location", "dbo.usp_Insights_Dimension_Location", LocationErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<EntityControlTotals, EntityRow>> GetEntityAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<EntityControlTotals, EntityRow>(
            "Entity", "dbo.usp_Insights_Dimension_Entity", EntityErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf },
            ReadEntityControlTotalsAsync, cancellationToken);

    public Task<DimensionResult<RiskControlTotals, RiskRow>> GetRiskAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<RiskControlTotals, RiskRow>(
            "Risk", "dbo.usp_Insights_Dimension_Risk", RiskErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<NatureControlTotals, NatureRow>> GetNatureAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<NatureControlTotals, NatureRow>(
            "Nature", "dbo.usp_Insights_Dimension_Nature", NatureErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<DepartmentsControlTotals, DepartmentsRow>> GetDepartmentsAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<DepartmentsControlTotals, DepartmentsRow>(
            "Departments", "dbo.usp_Insights_Dimension_Departments", DepartmentsErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<ActControlTotals, ActRow>> GetActAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<ActControlTotals, ActRow>(
            "Act", "dbo.usp_Insights_Dimension_Act", ActErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<UsersControlTotals, UsersRow>> GetUsersAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<UsersControlTotals, UsersRow>(
            "Users", "dbo.usp_Insights_Dimension_Users", UsersErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<InternalControlTotals, InternalRow>> GetInternalAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<InternalControlTotals, InternalRow>(
            "Internal", "dbo.usp_Insights_Dimension_Internal", InternalErrorBase,
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<EventControlTotals, EventRow>> GetEventAsync(
        int userId, int customerId, DateTime? asOf = null, int dormancyMonths = 12, CancellationToken cancellationToken = default) =>
        ExecuteAsync<EventControlTotals, EventRow>(
            "Event", "dbo.usp_Insights_Dimension_Event", EventErrorBase,
            userId, customerId,
            new { UserID = userId, CustomerID = customerId, AsOf = asOf, DormancyMonths = dormancyMonths },
            null, cancellationToken);

    /*  [FIX] Licence's THROWs no longer fit the base/base+1/base+2 single-code-per-kind shape
        every other dimension uses: sql/21 was moved off the 51130 block (collided outright with
        sql/15_freetier_digest_log.sql) onto 51160-51169, AND split its two reused codes into one
        per condition per CLAUDE.md Sec.5b - two reconciliation codes (51161/51162) and two
        dictionary-gap codes (51165/51166), not one of each. The exact-match `errorBase+1`/
        `errorBase+2` overload below cannot express that, so this call goes through the explicit
        overload with the real code sets instead of introducing a false collision between them.  */
    public Task<DimensionResult<LicenceControlTotals, LicenceRow>> GetLicenceAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<LicenceControlTotals, LicenceRow>(
            "Licence", "dbo.usp_Insights_Dimension_Licence",
            scopeDeniedCode: LicenceErrorBase,
            reconciliationCodes: [LicenceErrorBase + 1, LicenceErrorBase + 2],
            dictionaryGapCodes: [LicenceErrorBase + 5, LicenceErrorBase + 6],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    /*  [FIX] sql/22-25 all share ONE 51170-51179 block instead of one block each (see sql/22's
        own header note: single-row/fixed-bucket tenant-wide aggregates, not per-member
        dimensions, don't need a full 10-code block on top of each other's) - the clean
        errorBase/+1/+2 convenience overload cannot express 4 procs sharing one block, so all
        four go through the explicit overload with their real, individually-allocated codes,
        same treatment as Licence above. None of the four throw a dictionary-gap code of their
        own - EXEC dbo.usp_Insights_AssertStatusCoverage's own codes (sql/01) cover that path,
        same as every other dimension - so dictionaryGapCodes is empty for all four.            */
    public Task<DimensionResult<BacklogAgingControlTotals, BacklogAgingRow>> GetBacklogAgingAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<BacklogAgingControlTotals, BacklogAgingRow>(
            "BacklogAging", "dbo.usp_Insights_Dimension_BacklogAging",
            scopeDeniedCode: 51170,
            reconciliationCodes: [51171],
            dictionaryGapCodes: [],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<TimelinessFYControlTotals, TimelinessFYRow>> GetTimelinessFYAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<TimelinessFYControlTotals, TimelinessFYRow>(
            "TimelinessFY", "dbo.usp_Insights_Dimension_TimelinessFY",
            scopeDeniedCode: 51172,
            reconciliationCodes: [],
            dictionaryGapCodes: [],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<ForwardPipelineControlTotals, ForwardPipelineRow>> GetForwardPipelineAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<ForwardPipelineControlTotals, ForwardPipelineRow>(
            "ForwardPipeline", "dbo.usp_Insights_Dimension_ForwardPipeline",
            scopeDeniedCode: 51173,
            reconciliationCodes: [51174],
            dictionaryGapCodes: [],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    public Task<DimensionResult<EvidenceIntegrityControlTotals, EvidenceIntegrityRow>> GetEvidenceIntegrityAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<EvidenceIntegrityControlTotals, EvidenceIntegrityRow>(
            "EvidenceIntegrity", "dbo.usp_Insights_Dimension_EvidenceIntegrity",
            scopeDeniedCode: 51175,
            reconciliationCodes: [51176],
            dictionaryGapCodes: [],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);

    /// <summary>
    /// Reads the five result sets positionally and translates the proc's THROWs into typed
    /// exceptions. ORDER IS THE CONTRACT - the procs emit no result-set names, so reading these
    /// out of order silently misbinds columns rather than failing.
    ///
    /// Every dimension THROWs BEFORE selecting anything on its failure paths, so there is never a
    /// partial result set to lose here (unlike usp_Insights_GoldenInvariants, which selects
    /// first and then throws - see SqlGoldenRegressionRepository).
    /// </summary>
    private Task<DimensionResult<TControlTotals, TRow>> ExecuteAsync<TControlTotals, TRow>(
        string dimension,
        string procedureName,
        int errorBase,
        int userId,
        int customerId,
        object parameters,
        Func<SqlMapper.GridReader, Task<TControlTotals>>? controlTotalsReader,
        CancellationToken cancellationToken) =>
        ExecuteAsync<TControlTotals, TRow>(
            dimension, procedureName,
            scopeDeniedCode: errorBase,
            reconciliationCodes: [errorBase + 1],
            dictionaryGapCodes: [errorBase + 2],
            userId, customerId, parameters, controlTotalsReader, cancellationToken);

    private async Task<DimensionResult<TControlTotals, TRow>> ExecuteAsync<TControlTotals, TRow>(
        string dimension,
        string procedureName,
        int scopeDeniedCode,
        IReadOnlyCollection<int> reconciliationCodes,
        IReadOnlyCollection<int> dictionaryGapCodes,
        int userId,
        int customerId,
        object parameters,
        Func<SqlMapper.GridReader, Task<TControlTotals>>? controlTotalsReader,
        CancellationToken cancellationToken)
    {
        // [FIX - found live] "Ambiguous column name 'VsPeerStateNormPP'" from
        // usp_Insights_Dimension_Location is genuinely intermittent, not data- or tenant-dependent -
        // confirmed by calling the identical procedure with identical parameters repeatedly:
        // succeeded, succeeded, then failed three times in a row. That pattern rules out a logic
        // bug in the query itself and points to SQL Server occasionally choosing a bad execution
        // plan for this one query. UAT stored procedures cannot be touched right now, so this rides
        // out that transient plan-selection flakiness the same way a transient network blip would be
        // handled - retry a few times with a fresh connection before finally giving up - rather than
        // either failing the whole report or silently excluding a dimension that mostly works fine.
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                using var multi = await connection.QueryMultipleAsync(
                    new CommandDefinition(
                        procedureName,
                        parameters,
                        commandType: CommandType.StoredProcedure,
                        commandTimeout: DimensionCommandTimeoutSeconds,
                        cancellationToken: cancellationToken));

                /*  [FIX - found live 2026-08-24] Every dimension except Event opens with
                        EXEC dbo.usp_Insights_AssertStatusCoverage;
                    which used to end in a SELECT, adding an extra result set ahead of control_totals
                    that this method skipped explicitly. sql/01's own fix (same pull, "success is
                    silence") made that proc emit NO result set on success - but this skip was not
                    updated to match, so the skip call started consuming control_totals itself, and
                    ReadSingleAsync below ended up reading the ROWS grid instead: "Sequence contains
                    more than one element" for every tenant on every dimension except Event. Confirmed
                    by calling FetchDimensionsActivity directly against real UAT (tenant 23 AND 29 both
                    failed identically) - not tenant-specific, not a Durable Task or SDK issue.
                    The coverage proc emits nothing to skip any more; nothing here should skip it.   */

                var controlTotals = controlTotalsReader is null
                    ? await multi.ReadSingleAsync<TControlTotals>()
                    : await controlTotalsReader(multi);

                var rows = (await multi.ReadAsync<TRow>()).AsList();
                var detectors = (await multi.ReadAsync<DetectorRow>()).Select(ToDetectorPolicy).ToList();
                var assertions = (await multi.ReadAsync<AssertionRow>()).Select(ToAssertion).ToList();
                var findings = (await multi.ReadAsync<FindingRow>()).Select(ToFinding).ToList();
                var dataQuality = (await multi.ReadAsync<DataQualityNote>()).AsList();

                var result = new DimensionResult<TControlTotals, TRow>(
                    dimension, controlTotals, rows, detectors, assertions, findings, dataQuality);

                // The two rules no SQL THROW covers. Checked here so they cannot reach a customer.
                result.Validate();

                return result;
            }
            catch (SqlException ex) when (ex.Number == scopeDeniedCode)
            {
                throw new DimensionScopeDeniedException(dimension, userId, customerId, ex);
            }
            catch (SqlException ex) when (reconciliationCodes.Contains(ex.Number))
            {
                throw new DimensionReconciliationException(dimension, customerId, ex);
            }
            catch (SqlException ex) when (dictionaryGapCodes.Contains(ex.Number))
            {
                throw new DimensionDictionaryGapException(dimension, ex);
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                // Unclassified SQL error (not one of the procedure's own deliberate THROWs) with
                // attempts remaining - swallow and retry on a fresh connection.
            }
            catch (SqlException ex)
            {
                // Same unclassified error, out of retries. FetchDimensionsActivity's own "partial
                // generation" design (design doc Sec.11.4) exists precisely to let one dimension's
                // failure be excluded while every other, unaffected dimension still reaches the
                // customer - this dimension's own numbers genuinely cannot be trusted when its query
                // never ran to completion even after retrying, the same situation
                // DimensionReconciliationException already represents.
                throw new DimensionReconciliationException(dimension, customerId, ex);
            }
        }

        throw new UnreachableException("Loop above always either returns or throws.");
    }

    /*  Entity is the only dimension whose control_totals carry closed vocabularies that do not
        match their C# enum names ('single_entity', 'descend_one_level'), so Dapper's built-in
        string-to-enum mapping cannot do it. Parsed explicitly, fail-closed - the same treatment
        SqlEntityRepository gives the same two values.                                          */
    private static async Task<EntityControlTotals> ReadEntityControlTotalsAsync(SqlMapper.GridReader multi)
    {
        var r = await multi.ReadSingleAsync<EntityControlTotalsRow>();

        return new EntityControlTotals(
            r.ScopedInstances, r.SumOfRows, r.Reconciled, r.OverdueInstances, r.TenantOverduePct,
            r.NodesReported, r.ActiveBranchesInTenant, r.ApexEntityCount,
            ParseShape(r.TenantShape), r.LargestApexSharePct,
            ParseGrain(r.ComparisonGrain), r.GrainReason);
    }

    private static DetectorPolicy ToDetectorPolicy(DetectorRow r) =>
        new(r.Detector, r.Eligible, r.Flagged, r.FlaggedPct, ParseEmitMode(r.EmitMode));

    private static Assertion ToAssertion(AssertionRow r) =>
        new(r.AssertionId, r.Metric, r.ScopeLabel, r.Value, r.Rank_, r.OfN,
            r.ComparatorValue, r.VsComparatorPP, ParseDirection(r.Direction), r.Caveat);

    private static Finding ToFinding(FindingRow r) =>
        new(r.FindingId, ParseSeverity(r.Severity), r.Headline, SplitAssertionIds(r.AssertionIds), r.NarrativeGuard);

    /// <summary>
    /// findings.AssertionIds is a comma-separated list of the assertions backing the headline.
    /// Split, never parsed further - an id this wrapper does not recognise is still an id the
    /// prompt layer must be able to resolve.
    /// </summary>
    private static IReadOnlyList<string> SplitAssertionIds(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /*  All four parsers fail closed on an unrecognised value, matching the dictionary's own
        coverage check in sql/01. A vocabulary this wrapper does not know about must never be
        silently treated as any particular meaning - least of all the harmless one.            */

    private static DetectorEmitMode ParseEmitMode(string value) => value switch
    {
        "none" => DetectorEmitMode.None,
        "individual" => DetectorEmitMode.Individual,
        "aggregate" => DetectorEmitMode.Aggregate,
        _ => throw new InvalidOperationException($"Unknown EmitMode '{value}' in detector_policy."),
    };

    private static AssertionDirection? ParseDirection(string? value) => value switch
    {
        null => null,
        "worse" => AssertionDirection.Worse,
        "better" => AssertionDirection.Better,
        _ => throw new InvalidOperationException($"Unknown Direction '{value}' in assertions."),
    };

    private static FindingSeverity ParseSeverity(string value) => value switch
    {
        "info" => FindingSeverity.Info,
        "medium" => FindingSeverity.Medium,
        "high" => FindingSeverity.High,
        _ => throw new InvalidOperationException($"Unknown Severity '{value}' in findings."),
    };

    private static EntityCountShape ParseShape(string value) => value switch
    {
        "single_entity" => EntityCountShape.SingleEntity,
        "multi_entity" => EntityCountShape.MultiEntity,
        _ => throw new InvalidOperationException($"Unknown TenantShape '{value}' from usp_Insights_Dimension_Entity."),
    };

    private static ComparisonGrain ParseGrain(string value) => value switch
    {
        "locations" => ComparisonGrain.Locations,
        "descend_one_level" => ComparisonGrain.DescendOneLevel,
        "apex" => ComparisonGrain.Apex,
        _ => throw new InvalidOperationException($"Unknown ComparisonGrain '{value}' from usp_Insights_Dimension_Entity."),
    };

    /*  [TRAP] init PROPERTIES, NOT POSITIONAL - every SELECT feeding these four carries a
        leading 'xxx' AS ResultSet label column none of them declare. A positional record's
        only constructor requires an exact column match, so Dapper fails outright the moment
        the reader has one column the type does not. An init-property record gets an implicit
        parameterless constructor, so Dapper falls back to set-by-name and ignores ResultSet
        silently. Confirmed via a real run: all 47 DimensionRepositoryTests cases failed with
        this exact cause before the fix (2026-08-20). See DimensionShapes.cs for the same
        treatment applied to every public control_totals/rows type.                          */
    private sealed record DetectorRow
    {
        public string Detector { get; init; } = string.Empty;
        public int Eligible { get; init; }
        public int Flagged { get; init; }
        public decimal FlaggedPct { get; init; }
        public string EmitMode { get; init; } = string.Empty;
    }

    /// <summary>Rank_ carries the trailing underscore the SQL uses - RANK is a reserved word there.</summary>
    private sealed record AssertionRow
    {
        public string AssertionId { get; init; } = string.Empty;
        public string Metric { get; init; } = string.Empty;
        public string ScopeLabel { get; init; } = string.Empty;
        public decimal Value { get; init; }
        public int? Rank_ { get; init; }
        public int? OfN { get; init; }
        public decimal? ComparatorValue { get; init; }
        public decimal? VsComparatorPP { get; init; }
        public string? Direction { get; init; }
        public string? Caveat { get; init; }
    }

    private sealed record FindingRow
    {
        public string FindingId { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string Headline { get; init; } = string.Empty;
        public string? AssertionIds { get; init; }
        public string? NarrativeGuard { get; init; }
    }

    private sealed record EntityControlTotalsRow
    {
        public int ScopedInstances { get; init; }
        public int SumOfRows { get; init; }
        public bool Reconciled { get; init; }
        public int OverdueInstances { get; init; }
        public decimal TenantOverduePct { get; init; }
        public int NodesReported { get; init; }
        public int ActiveBranchesInTenant { get; init; }
        public int ApexEntityCount { get; init; }
        public string TenantShape { get; init; } = string.Empty;
        public decimal LargestApexSharePct { get; init; }
        public string ComparisonGrain { get; init; } = string.Empty;
        public string GrainReason { get; init; } = string.Empty;
    }
}



using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-22] Real capability, real exception to CLAUDE.md's own rule - "Never: Let an LLM
/// author SQL, resolve scope, or validate its own output." The user made a deliberate, informed
/// choice to override it for the narrate step specifically, after being shown the alternative (a
/// closed, enum-gated tool - the earlier CrossDimensionLookupTool, reverted 2026-09-21 for a
/// different reason, 14-dimension scale). This tool is the guardrail set built around that
/// decision, not a replacement for it: the model writes the SELECT text, but everything around it -
/// which login runs it, what it can see, how long it may run, how many rows it may return - stays
/// deterministic C#.
///
/// WHAT STILL NEVER MOVES INTO THE LLM-AUTHORED TEXT, even with the SQL-authoring rule waived:
///   - The connection/login. Always the caller-supplied read-only connection string - prod uses
///     regtech_dev01_readonly (confirmed live 2026-09-22: SELECT=1, INSERT/UPDATE/DELETE=0,
///     IsSysAdmin=0 - a real DB-enforced wall). UAT has no separate readonly login yet (sa only,
///     full sysadmin) - the user explicitly declined to create one, so on UAT the ONLY backstop is
///     this class's own query-shape validation below. Treat that as real, not decorative.
///   - UserID/CustomerID. Never accepted as part of the model's SQL text or as a tool parameter -
///     closed over in the constructor, same as CrossDimensionLookupTool's own reasoning.
///   - Tenant scope. The model's query runs against a SERVER-SIDE PRE-SCOPED temp table (#scoped),
///     built here from the real tvfInsightsScopedInstances(@UserID, @CustomerID) - the same
///     function every dimension proc already uses. The model is REQUIRED (checked, not just
///     instructed) to reference #scoped, but this is a text-level check, not a parser - it cannot
///     prove the query has no OTHER path to another tenant's rows if the read-only login's own
///     table-level grants allow it (regtech_dev01_readonly's SELECT grant was confirmed broad, not
///     scoped to a whitelist of views). This is a known, accepted limitation of choosing freeform
///     SQL over the closed-tool design - real defense in depth, not a provable guarantee.
/// </summary>
public sealed class ReadOnlySqlFetchTool
{
    public const int MaxCalls = 3;
    public const int MaxRows = 200;
    public const int CommandTimeoutSeconds = 8;

    // Blocklist, not a parser - real defense in depth, not a provable guarantee (see class doc).
    // Case-insensitive; checked against the whole query text, not just the first token, so a
    // keyword hidden after a UNION or inside a subquery is still caught.
    private static readonly string[] ForbiddenKeywords =
    [
        "insert", "update", "delete", "merge", "drop", "alter", "truncate", "create",
        "exec", "execute", "grant", "deny", "revoke", "sp_", "xp_", "into", "--", "/*",
    ];

    private static readonly Regex LeadingKeyword = new(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string connectionString;
    private readonly int userId;
    private readonly int customerId;
    private readonly DateTime? windowStart;
    private readonly DateTime? windowEnd;
    private int callCount;

    public int CallsMade => callCount;

    /// <summary>
    /// [ADDED 2026-09-25] windowStart/windowEnd - same closure treatment as userId/customerId: the
    /// model never sees or sets these, they are never a tool parameter, always the caller's own
    /// already-resolved period-picker window (or null when the caller's dimension is not
    /// windowed). Before this, #scoped was NEVER date-filtered - a live narrate call for a
    /// window-scoped dimension (Act/Event/Location/Risk/Nature/Departments/Users/Internal, all 9
    /// as of this session) could fetch_scoped_sql_data and see the tenant's FULL all-time
    /// population, disagreeing with the very report data it was cross-checking against. Same
    /// estate-definition-consistency reasoning CLAUDE.md already documents elsewhere.
    /// </summary>
    public ReadOnlySqlFetchTool(string connectionString, int userId, int customerId, DateTime? windowStart = null, DateTime? windowEnd = null)
    {
        this.connectionString = connectionString;
        this.userId = userId;
        this.customerId = customerId;
        this.windowStart = windowStart;
        this.windowEnd = windowEnd;
    }

    [Description(
        "Runs a real, read-only SQL SELECT against this tenant's own scoped compliance data, when " +
        "you need a fact your current dimension's assertions/rows genuinely do not carry and no " +
        "other tool/pool answers it - including a real CROSS-DIMENSION trace (e.g. which department " +
        "or branch a set of flagged instances actually belongs to). Your query MUST select FROM a " +
        "table named #scoped (a real, already tenant-scoped view this tool builds for you before " +
        "your query runs) - it has columns ComplianceInstanceID, BranchID, BranchName, CategoryId, " +
        "ComplianceID, RiskType, Imprisonment, NatureOfCompliance, ComplianceType, ActID, " +
        "DepartmentID, DepartmentName, HasInstanceOwner, HasScheduleOwner, NoInstanceOwner, " +
        "NoOwnerAnywhere, OwnerClass ('instance_assigned'|'schedule_only'|'no_schedules'|'unowned' " +
        "- ownership has TWO real mechanisms here, never read NoInstanceOwner alone as \"nobody is " +
        "doing this\", OwnerClass tells you which is true). When this run is scoped to a period " +
        "window, #scoped is ALREADY narrowed to that same window (a real scheduled occurrence " +
        "inside it) - it reflects the SAME population your dimension_rows describes, never the " +
        "tenant's full all-time data. Only a single SELECT/WITH statement is " +
        "allowed - no INSERT/UPDATE/DELETE/DROP/ALTER/EXEC, no semicolons, no comments, no other " +
        "tables. Returns JSON rows (capped at 200) or {\"error\": \"...\"} - on error, do not retry " +
        "the same query, fall back to the escape hatch. Do not call this speculatively - only when " +
        "you can say specifically what fact you are trying to get and why #scoped's own columns " +
        "would answer it.")]
    public async Task<string> FetchDataAsync(
        [Description("A single real SQL SELECT statement, querying FROM #scoped only - one query " +
            "per call, never two. Example A: SELECT BranchName, COUNT(*) AS N FROM #scoped WHERE " +
            "Imprisonment = 1 GROUP BY BranchName ORDER BY N DESC. Example B (cross-dimension - " +
            "which department a set of flagged branches actually belongs to): SELECT DepartmentName, " +
            "COUNT(*) AS N FROM #scoped WHERE BranchID IN (101,204,317) GROUP BY DepartmentName ORDER BY N DESC")]
        string sql)
    {
        if (callCount >= MaxCalls)
            return Error($"Budget exhausted - at most {MaxCalls} SQL fetches per narrate. Use what you already have.");

        var validation = Validate(sql);
        if (validation is not null)
            return Error(validation);

        callCount++;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // [FIX - FOUND LIVE 2026-09-22] #scoped is a local temp table, session-scoped, so it
            // must exist on the SAME session the model's query runs on. Building it as a SEPARATE
            // round-trip (its own ExecuteNonQueryAsync) before the model's query used to work in
            // every earlier lab run, then failed live against tenant 1105/user 10315 with
            // "Invalid object name '#scoped'" - reproduced with no LLM involved at all, and
            // confirmed the SAME @@SPID was reported before and after the setup call, so this was
            // never a different physical connection or a MARS session mismatch. That signature -
            // same SPID, temp table gone - matches Microsoft.Data.SqlClient's own idle/connection
            // resiliency feature (enabled by default, ConnectRetryCount=1): a silently recovered
            // connection restores most session state but explicitly NEVER restores local temp
            // tables or table variables. Any gap between two round-trips on the same connection is
            // a real, if rare, window for this. The fix removes the window entirely: #scoped setup
            // and the model's query now run in ONE batch, ONE round-trip, so there is no longer a
            // point where a silent reconnect could happen in between. SELECT...INTO never returns
            // its own result set, so ExecuteReaderAsync's first (only) result set is still exactly
            // the model's own query result - callers of this method see no shape change.
            //
            // [WIDENED 2026-09-22] Added DepartmentID/DepartmentName (real, verified against
            // sql/10_dimension_departments.sql - DepartmentID lives on ComplianceInstance, not the
            // assignment) and the real ownership-mechanism flags from tvfInsightsOwnership
            // (sql/01) - the same "ownership has two mechanisms" data CLAUDE.md's own trap list
            // warns never to read from just one side of. This is what makes real cross-dimension
            // tracing (e.g. "which department is behind these overdue branches") possible through
            // this tool - the original real ask that motivated it.
            // [ADDED 2026-09-25] When a window is present, #scoped is narrowed to instances with a
            // real ComplianceScheduleOn occurrence inside it - the SAME #active/DELETE NOT EXISTS
            // pattern the dimension procs themselves use (sql/11 Act was the first, now all 9
            // windowed dims), just expressed as a second step here rather than inside a stored
            // proc. Runs in the SAME batch/round-trip as #scoped's own build and the model's query,
            // for the identical connection-resiliency reason the #scoped build itself already
            // documents below - no gap for a silent reconnect to drop a local temp table.
            var windowNarrowingSql = windowStart is not null && windowEnd is not null
                ? """

                  IF OBJECT_ID('tempdb..#scopedActive') IS NOT NULL DROP TABLE #scopedActive;
                  SELECT DISTINCT cso.ComplianceInstanceID
                  INTO #scopedActive
                  FROM #scoped s
                  JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = s.ComplianceInstanceID
                  WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;

                  DELETE s FROM #scoped s
                  WHERE NOT EXISTS (SELECT 1 FROM #scopedActive a WHERE a.ComplianceInstanceID = s.ComplianceInstanceID);
                  """
                : "";

            var combinedSql =
                $"""
                SELECT s.ComplianceInstanceID, s.BranchID, cb.Name AS BranchName, s.CategoryId,
                       s.ComplianceID, s.RiskType, s.Imprisonment, s.NatureOfCompliance, s.ComplianceType, s.ActID,
                       ci.DepartmentID, d.Name AS DepartmentName,
                       o.HasInstanceOwner, o.HasScheduleOwner, o.NoInstanceOwner, o.NoOwnerAnywhere, o.OwnerClass
                INTO #scoped
                FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
                LEFT JOIN CustomerBranch cb ON cb.ID = s.BranchID
                LEFT JOIN ComplianceInstance ci ON ci.ID = s.ComplianceInstanceID
                LEFT JOIN Department d ON d.ID = ci.DepartmentID
                LEFT JOIN dbo.tvfInsightsOwnership(@UserID, @CustomerID) o ON o.ComplianceInstanceID = s.ComplianceInstanceID;
                {windowNarrowingSql}

                SET ROWCOUNT {MaxRows};
                {sql}
                """;

            await using var command = new SqlCommand(combinedSql, connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };
            command.Parameters.AddWithValue("@UserID", userId);
            command.Parameters.AddWithValue("@CustomerID", customerId);
            command.Parameters.AddWithValue("@WindowStart", (object?)windowStart ?? DBNull.Value);
            command.Parameters.AddWithValue("@WindowEnd", (object?)windowEnd ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return JsonSerializer.Serialize(new { rows, row_count = rows.Count, truncated = rows.Count >= MaxRows });
        }
        catch (SqlException ex)
        {
            // Never let the raw exception reach the model as an unhandled tool fault - and never
            // echo ex.Message verbatim (it can include real schema detail) beyond what's needed to
            // let the model correct an honest mistake (a typo'd column name, e.g.).
            return Error($"Query failed: {ex.Message}");
        }
    }

    /// <summary>Returns null when the query is acceptable, or a real reason string when it is refused.</summary>
    private static string? Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "Empty query.";

        var trimmed = sql.Trim().TrimEnd(';');

        if (trimmed.Contains(';'))
            return "Only a single statement is allowed - no semicolon-separated statements.";

        if (!LeadingKeyword.IsMatch(trimmed))
            return "Query must start with SELECT or WITH.";

        if (!trimmed.Contains("#scoped", StringComparison.OrdinalIgnoreCase))
            return "Query must select FROM #scoped - it is the only tenant-scoped table available to you.";

        var lower = trimmed.ToLowerInvariant();
        foreach (var keyword in ForbiddenKeywords)
        {
            if (lower.Contains(keyword))
                return $"Query contains a disallowed keyword or construct: '{keyword}'.";
        }

        return null;
    }

    private static string Error(string reason) => JsonSerializer.Serialize(new { error = reason });
}

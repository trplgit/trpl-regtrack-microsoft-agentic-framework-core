using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Insights.Data;
using Insights.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// [FOUND LIVE 2026-09-20] The model sometimes emits row_refs_used[].member_key as a raw JSON
/// number (e.g. DepartmentID) instead of the real string name field the prompt's worked example
/// uses - confirmed live against tenant 29's real Departments data. Accepting either and
/// coercing to string here is a defensive fix on top of the prompt clarification also made the
/// same day; a model output contract should not hard-fail on a plausible, if wrong, token type.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a string or number for member_key, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// [ADDED 2026-09-20] prompts/v2/03_narrative_analyst.md - replaces INarrativeAgent +
/// INarrativeReflectionAgent for the 5 freehand dimensions only (see the design doc:
/// docs/superpowers/specs/2026-09-20-narrative-analyst-agent-design.md). One call does both jobs:
/// writes prose AND performs its own self-reflection pass before returning, instead of a second
/// round-trip to a separate reflection agent.
///
/// LAB/EXPERIMENTAL - not yet wired into PaidReportAgentsRegistration or the orchestrator. See
/// tests/Insights.IntegrationTests/NarrativeAnalystLabTests.cs for the real comparison harness.
/// </summary>
public interface IAnalystNarrativeAgent
{
    /// <summary>
    /// Writes prose for every block in <paramref name="plan"/>, from
    /// <paramref name="assertions"/>/<paramref name="findings"/> PLUS the raw per-member
    /// <paramref name="dimensionRowsJson"/> for this dimension - the same real, already-reconciled
    /// data FetchDimensionsActivity already retrieved, just now threaded to this agent so it can
    /// trace a number back to a specific member (performer, department, branch) instead of only
    /// restating it. <paramref name="dimensionControlTotalsJson"/> is optional - not every
    /// dimension's tenant-level aggregates are relevant to every trace.
    ///
    /// Unlike INarrativeAgent, there is no separate revision parameter fed back from an external
    /// reflection agent - self-critique happens inside this one call (prompts/v2's own
    /// self_reflection block). <paramref name="revision"/> still exists for the SAME class of
    /// retry PublishGate or an external caller might request (e.g. a claim-checker rejection),
    /// not for a narrative-reflection round-trip that no longer exists.
    ///
    /// <paramref name="userId"/>/<paramref name="customerId"/> [ADDED 2026-09-22] - optional,
    /// trailing, so every existing caller is unaffected. Only meaningful when this instance was
    /// constructed with a real read-only connection string (see MafAnalystNarrativeAgent's own
    /// doc comment) - when both that AND these are present, the model may call
    /// ReadOnlySqlFetchTool for itself this run. Never accepted as JSON text the model writes;
    /// always the caller's own real, already-validated tenant identity.
    ///
    /// <paramref name="runId"/> [ADDED 2026-09-23] - optional, trailing, same reasoning. The real
    /// DTFx run id, threaded through only so MafAnalystNarrativeAgent's tool-invocation logging
    /// (see its own doc comment) can key its rows by the run they belong to - never used for
    /// anything else, never sent to the model.
    ///
    /// <paramref name="windowStart"/>/<paramref name="windowEnd"/> [ADDED 2026-09-25] - optional,
    /// trailing, same reasoning as userId/customerId. When this dimension's own fetch was scoped to
    /// a period-picker window, passing the SAME window here narrows ReadOnlySqlFetchTool's #scoped
    /// population to match - without this, a live SQL tool call would see the tenant's full
    /// all-time data while dimension_rows describes only the window, a real two-populations-
    /// disagreeing risk. Never accepted as JSON text the model writes.
    /// </summary>
    Task<AgentCallResult<NarrativeResult>> AnalyzeAndNarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionName,
        string dimensionRowsJson,
        string? dimensionControlTotalsJson,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        int? userId = null,
        int? customerId = null,
        string? runId = null,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// [EXTENDED 2026-09-22] <paramref name="readOnlySqlConnectionString"/> - optional. When set (and
/// AnalyzeAndNarrateAsync also receives real userId/customerId), this agent gets a real tool,
/// ReadOnlySqlFetchTool, letting it run its own read-only SELECT when unsure - a deliberate,
/// user-approved exception to CLAUDE.md's own "never let an LLM author SQL" rule (see that class's
/// own doc comment for the guardrails that stayed in place around the exception). A single
/// singleton instance of this class serves every tenant, so this connection string is
/// environment-wide config (which DB, which login), never tenant-specific - tenant scope is
/// per-call (userId/customerId), never baked into the connection.
///
/// <paramref name="onSqlToolInvoked"/> [WIDENED 2026-09-23, was Action&lt;string,string&gt;] Real
/// gap found live: production DI (PaidReportAgentsRegistration.cs) passed this null, so for every
/// real report generated so far there was no way to tell whether the model actually called the
/// tool for a given run - CallsMade exists on the tool instance itself, but that instance is never
/// exposed outside this method, and nothing durable recorded a call even when it happened. Now
/// async (so a caller can await a real DB insert - see IToolInvocationRecorder) and carries the
/// real runId and a success flag, not just (sql, result) - still null in every real production
/// wiring UNTIL PaidReportAgentsRegistration.cs wires SqlToolInvocationRecorder in.
///
/// <paramref name="onMemoryWriteInvoked"/> [ADDED 2026-09-23] Same gap, same fix, for
/// write_tenant_memory - unlike the SQL tool this has never had ANY observability hook before now.
/// </summary>
public sealed class MafAnalystNarrativeAgent(
    AIAgent agent,
    string? readOnlySqlConnectionString = null,
    Func<string?, string, string, bool, Task>? onSqlToolInvoked = null,
    // [ADDED 2026-09-22] Tenant memory - see docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md.
    // All four null (the default) is a complete no-op, same pattern as readOnlySqlConnectionString
    // above. When all four are set, this call reads its OWN dimension's history section before
    // narrating (injected as tenant_history) and gets a real write_tenant_memory tool, scoped to
    // exactly the one dimension this call is narrating - allowedDimensions is always a single-entry
    // list here, since AnalyzeAndNarrateAsync is always single-dimension (unlike v1's NarrateAsync).
    IReportEncryptor? memoryEncryptor = null,
    IReportDecryptor? memoryDecryptor = null,
    string? memoryBlobConnectionString = null,
    string? memoryContainerName = null,
    Func<string?, string, bool, Task>? onMemoryWriteInvoked = null) : IAnalystNarrativeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringConverter() },
    };

    public async Task<AgentCallResult<NarrativeResult>> AnalyzeAndNarrateAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        string dimensionName,
        string dimensionRowsJson,
        string? dimensionControlTotalsJson,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        int? userId = null,
        int? customerId = null,
        string? runId = null,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        CancellationToken cancellationToken = default)
    {
        // [ADDED 2026-09-22] Tenant memory read-side: always injected, never a tool call (same
        // reasoning as dimension_rows itself) - resolved BEFORE the payload is built so it can be a
        // real field on it, not a separate round trip. Degrades to "" on any failure (Key
        // Vault/blob down) - see TenantMemoryTool's own doc comment; never blocks narration.
        TenantMemoryTool? memoryTool = null;
        var tenantHistory = "";
        if (memoryEncryptor is not null && memoryDecryptor is not null && memoryBlobConnectionString is not null
            && memoryContainerName is not null && customerId is not null)
        {
            memoryTool = new TenantMemoryTool(
                memoryEncryptor, memoryDecryptor, memoryBlobConnectionString, memoryContainerName,
                customerId.Value, [dimensionName]);
            var sections = await memoryTool.ReadSectionsAsync(cancellationToken);
            tenantHistory = sections.GetValueOrDefault(dimensionName, "");
        }

        // dimension_rows/dimension_control_totals are already-serialized JSON strings (same shape
        // InsightsReportOrchestrator already builds for RenderHtmlInput - see the design doc
        // Sec.4) - embed as raw JSON, not as a re-escaped string, so the agent sees real objects.
        var payload =
            $$"""
            {
              "composition_plan": {{JsonSerializer.Serialize(plan, JsonOptions)}},
              "assertions": {{JsonSerializer.Serialize(assertions, JsonOptions)}},
              "findings": {{JsonSerializer.Serialize(findings, JsonOptions)}},
              "dimension_name": {{JsonSerializer.Serialize(dimensionName)}},
              "dimension_rows": {{dimensionRowsJson}},
              "dimension_control_totals": {{dimensionControlTotalsJson ?? "null"}},
              "tenant_history": {{JsonSerializer.Serialize(tenantHistory)}},
              "previous_narrative": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.PreviousNarrative, JsonOptions))}},
              "reflection_issues": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.Issues, JsonOptions))}}
            }
            """;

        // [TRAP] Same Responses-API constraint every other JSON-mode agent here already documents:
        // 400s unless the input message itself contains the literal word "json".
        var message = (revision is not null
            ? "A prior attempt needs revision against these issues, as JSON:\n"
            : "Here is the approved composition plan, the assertion/finding pools, and the raw dimension data for root-cause tracing, as JSON:\n") + payload;

        var tools = new List<AITool>();

        ReadOnlySqlFetchTool? sqlTool = null;
        if (readOnlySqlConnectionString is not null && userId is not null && customerId is not null)
        {
            sqlTool = new ReadOnlySqlFetchTool(readOnlySqlConnectionString, userId.Value, customerId.Value, windowStart, windowEnd);

            // [ADDED 2026-09-22] onSqlToolInvoked is a diagnostic-only hook (null in every real
            // production DI registration today) - a caller that wants to SEE whether the model
            // actually called the tool (not just that it was available) wraps FetchDataAsync
            // instead of guessing from CallsMade after the fact, since the tool instance itself is
            // never otherwise exposed outside this method.
            //
            // A local function, not a lambda: AIFunctionFactory.Create reflects the delegate's own
            // parameter attributes to build the tool's JSON schema, and a lambda's compiler-
            // generated parameter carries none - a bare Func<string,Task<string>> lambda here would
            // have silently dropped ReadOnlySqlFetchTool.FetchDataAsync's own [Description] (the
            // exact #scoped contract/example queries the model needs to call this correctly).
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
            async Task<string> FetchWithLogging(
                [Description("A single real SQL SELECT statement, querying FROM #scoped only - one query " +
                    "per call, never two. Example A: SELECT BranchName, COUNT(*) AS N FROM #scoped WHERE " +
                    "Imprisonment = 1 GROUP BY BranchName ORDER BY N DESC. Example B (cross-dimension - " +
                    "which department a set of flagged branches actually belongs to): SELECT DepartmentName, " +
                    "COUNT(*) AS N FROM #scoped WHERE BranchID IN (101,204,317) GROUP BY DepartmentName ORDER BY N DESC")]
                string sql)
            {
                var result = await sqlTool.FetchDataAsync(sql);
                await TryLogSqlCallAsync(onSqlToolInvoked, runId, sql, result);
                return result;
            }

            tools.Add(AIFunctionFactory.Create(FetchWithLogging, name: "fetch_scoped_sql_data"));
        }

        // [ADDED 2026-09-22, LOGGING ADDED 2026-09-23] Tenant memory write-side. A local function
        // (not the method group directly, now that logging wraps it) - re-declares
        // WriteTenantMemoryAsync's own [Description] attributes verbatim, same "AIFunctionFactory
        // reflects the delegate's own parameter attributes" trap FetchWithLogging above already
        // works around, since a wrapping local function's parameters need their own copies.
        if (memoryTool is not null)
        {
            [Description(
                "Saves a short markdown note about this run to this tenant's persistent cross-run memory - " +
                "use it to record a finding worth remembering for the NEXT time this dimension is reported " +
                "on (e.g. \"same gap as last run\", \"this is new since the last run\"). Only call this for " +
                "a dimension you are actually narrating THIS run - never another tenant's or another " +
                "dimension's data. Your new text REPLACES what was there for this dimension, so include " +
                "everything worth keeping, not just what changed.")]
            async Task<string> WriteWithLogging(
                [Description("Must be exactly one of the dimensions this run is narrating.")]
                string dimensionName,
                [Description("The new full markdown body for this dimension's section (not the heading itself). " +
                    "Keep it under ~3000 characters where possible - if it is approaching that size, condense " +
                    "older entries (keep dates, drop restated detail, merge unchanged runs into one line) " +
                    "rather than just appending. Refused outright above 6000 characters.")]
                string newSectionMarkdown)
            {
                var result = await memoryTool.WriteTenantMemoryAsync(dimensionName, newSectionMarkdown);
                await TryLogMemoryWriteAsync(onMemoryWriteInvoked, runId, dimensionName, result);
                return result;
            }

            tools.Add(AIFunctionFactory.Create(WriteWithLogging, name: "write_tenant_memory"));
        }

        var runOptions = tools.Count > 0
            ? new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { Tools = tools } }
            : null;

        var response = runOptions is not null
            ? await agent.RunAsync(message, session: null, options: runOptions, cancellationToken: cancellationToken)
            : await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Analyst narrative agent returned no text.");

        var narrative = JsonSerializer.Deserialize<NarrativeResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Analyst narrative agent returned unparsable JSON: {text}");

        // [FIX - FOUND LIVE 2026-09-22] System.Text.Json's default unmapped/shape-mismatch
        // handling does not throw - a response whose top-level shape does not match
        // NarrativeResult (e.g. "blocks" nested one level deeper than expected, or a genuinely
        // different key) silently deserializes to an EMPTY Blocks list instead of failing. That
        // is a real, billed, successful LLM call producing NOTHING, previously accepted without
        // comment - confirmed live on a real Risk run (tenant 29): the call billed 9509 tokens and
        // returned zero blocks, no exception, no log. CLAUDE.md non-negotiable #2 ("fail closed,
        // and fail loudly") applies here exactly as much as to a SQL-level failure - an empty
        // result reaching PersistActivity as if it were a real, on-topic zero-block report is the
        // silent-wrong-number failure mode this whole project exists to prevent.
        if (narrative.Blocks.Count == 0)
            throw new InvalidOperationException($"Analyst narrative agent returned zero blocks - the plan had {plan.Blocks.Count} block(s) approved. Raw response: {text}");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<NarrativeResult>(narrative, totalTokens, ReasoningSummaryExtractor.Extract(response.Messages));
    }

    /// <summary>
    /// [ADDED 2026-09-23, LAB] Sibling to <see cref="AnalyzeAndNarrateAsync"/> for a call that
    /// narrates SEVERAL dimensions at once - the fixed Holistic Insights report's own shape (each
    /// of its six blocks already draws on more than one dimension), the same real shape v1's
    /// NarrateAsync has always handled for this report type. Uses
    /// prompts/v2/03_narrative_analyst_holistic.md (that file's own doc comment explains the
    /// relationship to both v1's 03_narrative.md and this class's own single-dimension
    /// AnalyzeAndNarrateAsync) - this instance must have been constructed with that prompt's
    /// instructions, not the single-dimension one, or the payload shape below will not match what
    /// the model expects.
    ///
    /// Deliberately a SEPARATE method rather than an overload/branch inside
    /// AnalyzeAndNarrateAsync - the payload shape genuinely differs (per-dimension-keyed
    /// dictionaries instead of one dimension's flat JSON), and keeping them apart means this
    /// addition cannot change AnalyzeAndNarrateAsync's own behaviour for any existing caller.
    /// </summary>
    public async Task<AgentCallResult<NarrativeResult>> AnalyzeAndNarrateHolisticAsync(
        CompositionPlan plan,
        IReadOnlyList<Assertion> assertions,
        IReadOnlyList<Finding> findings,
        IReadOnlyDictionary<string, string> dimensionRowsJsonByDimension,
        IReadOnlyDictionary<string, string>? dimensionControlTotalsJsonByDimension = null,
        IReadOnlyDictionary<string, string>? dataQualityJsonByDimension = null,
        (NarrativeResult PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue> Issues)? revision = null,
        int? userId = null,
        int? customerId = null,
        string? runId = null,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        CancellationToken cancellationToken = default)
    {
        var dimensionNames = dimensionRowsJsonByDimension.Keys.ToList();

        TenantMemoryTool? memoryTool = null;
        var tenantHistory = new Dictionary<string, string>();
        if (memoryEncryptor is not null && memoryDecryptor is not null && memoryBlobConnectionString is not null
            && memoryContainerName is not null && customerId is not null && dimensionNames.Count > 0)
        {
            memoryTool = new TenantMemoryTool(
                memoryEncryptor, memoryDecryptor, memoryBlobConnectionString, memoryContainerName,
                customerId.Value, dimensionNames);
            tenantHistory = (Dictionary<string, string>)await memoryTool.ReadSectionsAsync(cancellationToken);
        }

        // Every *ByDimension parameter is a dictionary of ALREADY-SERIALIZED JSON strings, keyed by
        // real dimension name - embedded as raw JSON objects (never re-escaped), same convention
        // AnalyzeAndNarrateAsync already uses for its own single dimensionRowsJson string.
        static string JoinAsObject(IReadOnlyDictionary<string, string>? byDimension) =>
            byDimension is null || byDimension.Count == 0
                ? "{}"
                : "{" + string.Join(",", byDimension.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}:{kv.Value}")) + "}";

        var payload =
            $$"""
            {
              "composition_plan": {{JsonSerializer.Serialize(plan, JsonOptions)}},
              "assertions": {{JsonSerializer.Serialize(assertions, JsonOptions)}},
              "findings": {{JsonSerializer.Serialize(findings, JsonOptions)}},
              "dimension_rows_by_dimension": {{JoinAsObject(dimensionRowsJsonByDimension)}},
              "dimension_control_totals_by_dimension": {{JoinAsObject(dimensionControlTotalsJsonByDimension)}},
              "data_quality_by_dimension": {{JoinAsObject(dataQualityJsonByDimension)}},
              "tenant_history": {{JsonSerializer.Serialize(tenantHistory, JsonOptions)}},
              "previous_narrative": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.PreviousNarrative, JsonOptions))}},
              "reflection_issues": {{(revision is null ? "null" : JsonSerializer.Serialize(revision.Value.Issues, JsonOptions))}}
            }
            """;

        var message = (revision is not null
            ? "A prior attempt needs revision against these issues, as JSON:\n"
            : "Here is the approved composition plan, the assertion/finding pools, and the raw per-dimension data for cross-dimension root-cause tracing, as JSON:\n") + payload;

        var tools = new List<AITool>();

        ReadOnlySqlFetchTool? sqlTool = null;
        if (readOnlySqlConnectionString is not null && userId is not null && customerId is not null)
        {
            sqlTool = new ReadOnlySqlFetchTool(readOnlySqlConnectionString, userId.Value, customerId.Value, windowStart, windowEnd);

            [Description(
                "Runs a real, read-only SQL SELECT against this tenant's own scoped compliance data, when " +
                "you need a fact your current dimension_rows_by_dimension/assertions genuinely do not carry " +
                "and no other tool/pool answers it - including a real CROSS-DIMENSION trace (e.g. which " +
                "department or branch a set of flagged instances actually belongs to). Your query MUST " +
                "select FROM a table named #scoped (a real, already tenant-scoped view this tool builds for " +
                "you before your query runs) - it has columns ComplianceInstanceID, BranchID, BranchName, " +
                "CategoryId, ComplianceID, RiskType, Imprisonment, NatureOfCompliance, ComplianceType, ActID, " +
                "DepartmentID, DepartmentName, HasInstanceOwner, HasScheduleOwner, NoInstanceOwner, " +
                "NoOwnerAnywhere, OwnerClass ('instance_assigned'|'schedule_only'|'no_schedules'|'unowned' " +
                "- ownership has TWO real mechanisms here, never read NoInstanceOwner alone as \"nobody is " +
                "doing this\", OwnerClass tells you which is true). When this run is scoped to a period " +
                "window, #scoped is ALREADY narrowed to that same window - it reflects the SAME population " +
                "your dimension_rows_by_dimension describes, never the tenant's full all-time data. Only a " +
                "single SELECT/WITH statement is " +
                "allowed - no INSERT/UPDATE/DELETE/DROP/ALTER/EXEC, no semicolons, no comments, no other " +
                "tables. Returns JSON rows (capped at 200) or {\"error\": \"...\"} - on error, do not retry " +
                "the same query, fall back to the escape hatch. Do not call this speculatively - only when " +
                "you can say specifically what fact you are trying to get and why #scoped's own columns " +
                "would answer it.")]
            async Task<string> FetchWithLogging(
                [Description("A single real SQL SELECT statement, querying FROM #scoped only - one query " +
                    "per call, never two. Example (cross-dimension - which department a set of flagged " +
                    "branches actually belongs to): SELECT DepartmentName, COUNT(*) AS N FROM #scoped WHERE " +
                    "BranchID IN (101,204,317) GROUP BY DepartmentName ORDER BY N DESC")]
                string sql)
            {
                var result = await sqlTool.FetchDataAsync(sql);
                await TryLogSqlCallAsync(onSqlToolInvoked, runId, sql, result);
                return result;
            }

            tools.Add(AIFunctionFactory.Create(FetchWithLogging, name: "fetch_scoped_sql_data"));
        }

        if (memoryTool is not null)
        {
            [Description(
                "Saves a short markdown note about this run to this tenant's persistent cross-run memory - " +
                "use it to record a finding worth remembering for the NEXT time this dimension is reported " +
                "on. Only call this for a dimension you are actually narrating THIS run - check " +
                "dimension_rows_by_dimension's own keys. Your new text REPLACES what was there for this " +
                "dimension, so include everything worth keeping, not just what changed.")]
            async Task<string> WriteWithLogging(
                [Description("Must be exactly one of the dimensions this run is narrating (a real key of dimension_rows_by_dimension).")]
                string dimensionName,
                [Description("The new full markdown body for this dimension's section (not the heading itself). " +
                    "Keep it under ~3000 characters where possible. Refused outright above 6000 characters.")]
                string newSectionMarkdown)
            {
                var result = await memoryTool.WriteTenantMemoryAsync(dimensionName, newSectionMarkdown);
                await TryLogMemoryWriteAsync(onMemoryWriteInvoked, runId, dimensionName, result);
                return result;
            }

            tools.Add(AIFunctionFactory.Create(WriteWithLogging, name: "write_tenant_memory"));
        }

        var runOptions = tools.Count > 0
            ? new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { Tools = tools } }
            : null;

        var response = runOptions is not null
            ? await agent.RunAsync(message, session: null, options: runOptions, cancellationToken: cancellationToken)
            : await agent.RunAsync(message, cancellationToken: cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Holistic analyst narrative agent returned no text.");

        var narrative = JsonSerializer.Deserialize<NarrativeResult>(text, JsonOptions)
            ?? throw new InvalidOperationException($"Holistic analyst narrative agent returned unparsable JSON: {text}");

        // Same "fail closed and loud" reasoning as AnalyzeAndNarrateAsync's own identical check -
        // see that method's doc comment for the real live incident that motivated it.
        if (narrative.Blocks.Count == 0)
            throw new InvalidOperationException($"Holistic analyst narrative agent returned zero blocks - the plan had {plan.Blocks.Count} block(s) approved. Raw response: {text}");

        var holisticTotalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<NarrativeResult>(narrative, holisticTotalTokens, ReasoningSummaryExtractor.Extract(response.Messages));
    }

    /// <summary>
    /// Both ReadOnlySqlFetchTool.FetchDataAsync and TenantMemoryTool.WriteTenantMemoryAsync return
    /// a JSON object shaped either as their real success payload or as <c>{"error": "..."}"</c> -
    /// never anything else, never a thrown exception past their own catch-all. Parsed structurally
    /// (a real top-level "error" property), not by a substring check - a legitimate SQL result row
    /// or memory note could easily contain the word "error" in its own data without this being a
    /// tool failure.
    /// </summary>
    private static bool IsSuccessResult(string toolResultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(toolResultJson);
            return !document.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            // Unparsable is not success either, but never let a logging concern throw past the
            // caller's own result - see this class's broader "logging is diagnostic-only" stance.
            return false;
        }
    }

    /// <summary>
    /// [FIX 2026-09-23] The callback is diagnostic-only, and MUST stay that way: swallowing its
    /// failure here is what actually makes that true. Without this, a logging outage (e.g. the
    /// InsightsToolInvocationLog table not yet deployed to a given environment) would throw out of
    /// FetchWithLogging itself and fail the real tool call the model was waiting on - the exact
    /// "never let a side channel take down the real path" discipline IAgentReasoningRecorder's own
    /// call site (AnalyzeAndNarrateActivity) already applies, now applied here too.
    /// </summary>
    private static async Task TryLogSqlCallAsync(
        Func<string?, string, string, bool, Task>? onSqlToolInvoked, string? runId, string sql, string result)
    {
        if (onSqlToolInvoked is null)
            return;

        try
        {
            await onSqlToolInvoked(runId, sql, result, IsSuccessResult(result));
        }
        catch
        {
            // Logging is diagnostic-only - see this method's own doc comment.
        }
    }

    /// <summary>Same reasoning as <see cref="TryLogSqlCallAsync"/>, for the memory-write hook.</summary>
    private static async Task TryLogMemoryWriteAsync(
        Func<string?, string, bool, Task>? onMemoryWriteInvoked, string? runId, string dimensionName, string result)
    {
        if (onMemoryWriteInvoked is null)
            return;

        try
        {
            await onMemoryWriteInvoked(runId, dimensionName, IsSuccessResult(result));
        }
        catch
        {
            // Logging is diagnostic-only - see TryLogSqlCallAsync's own doc comment.
        }
    }
}

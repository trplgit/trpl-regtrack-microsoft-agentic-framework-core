# Tenant memory (blob-backed cross-run history) - design

## Goal

Every real freehand-dimension report today answers "what is true now." Nothing in this engine can
answer "since when" or "is this the same finding I told this tenant last time" - the database holds
only the present, and each report run starts from zero context. This gives the narrate step (both
`MafAnalystNarrativeAgent`/v2 and `MafNarrativeAgent`/v1) a real, per-tenant, per-dimension memory of
its own past runs: a small markdown note it can read before narrating and update after, stored
encrypted in blob, one file per tenant.

This is a narrative/trend aid, not a data source. `sql/33_metric_snapshot.sql` (dormant, structured
numeric trend store: control-total scalars only, `IsComparable`/`MetricClass` discipline) remains the
correct place for anything that needs a rigorous "is this really comparable" guarantee. This feature
is qualitative and LLM-authored by design - the agent's own judgement about what was worth
remembering from a prior run, not a reconciled numeric series.

## Non-negotiables carried over

- **CLAUDE.md #1 (determinism owns truth and safety):** the model authors the memory TEXT; it never
  chooses the blob path, the tenant, or which dimension's section it may touch. Scope is always
  closed over or validated against a closed set built by deterministic C#, same principle as
  `ReadOnlySqlFetchTool`'s `#scoped`/`userId`/`customerId` closure.
- **CLAUDE.md #2 (fail closed, fail loud) - with one deliberate exception:** a memory read or write
  failure (Key Vault down, blob unreachable, conflict retries exhausted) must NEVER fail or degrade
  the actual report. This is the one place in the system where "fail soft, log it" is the correct
  behaviour, because memory is an enhancement layered on top of an already-complete, already-correct
  report - not a claim the report depends on. Matches how a failed `ReadOnlySqlFetchTool` call already
  degrades today (narrate proceeds without that fact).
- **Every claim in the report itself still traces to a typed assertion** (CLAUDE.md #5) - history
  context can inform HOW the agent frames this run's numbers (e.g. "this is the third run in a row
  this branch has shown up"), but a historical fact from memory is never itself the source of a new
  numeric claim in the CURRENT report. The current run's own assertions remain the only source of
  numbers shown to the reader.

## Data shape

One blob per tenant: `<tenantId>/history.md.enc` in a new container, `insights-tenant-memory`
(separate from `insights-reports-temp` - different retention/access profile, and keeps a lifecycle
policy on report blobs from accidentally sweeping memory files). Encrypted with the same envelope
scheme as reports (`IReportEncryptor`/`IReportDecryptor` -> `AdalKeyVaultReportEncryptor`, IV-prepended
AES-256-CBC content, RSA-OAEP-wrapped key via Key Vault) - reused as-is, no new crypto code.

Inside the decrypted markdown, one section per dimension, fixed heading format:

```markdown
## Internal
2026-09-15: 44 of 99 locations run statutory with no internal governance - unchanged from the prior
run. karad Pvt Ltd Info and A Testing Location remain the two largest gaps.
2026-09-22: Same 44-of-99 gap. Branch mix identical to last run - no new locations opened statutory
obligations without internal governance since 2026-09-15.

## Risk
2026-09-18: Critical-risk instances concentrate in Gujarat (41% of tenant total)...
```

Each dimension owns ONLY its own section (`## {DimensionName}` up to the next `## ` heading or EOF).
Sections are disjoint by construction, so two dimensions' narrate calls touching the SAME tenant
blob concurrently modify different byte ranges of the logical document, not the same one - the only
real collision is two writes landing on the exact same blob generation at once, which optimistic
concurrency (below) handles as a retry, not a merge.

## Components

### 1. `Insights.Domain/TenantMemorySections.cs` (NEW) - pure functions, no I/O

```csharp
public static class TenantMemorySections
{
    public static string ExtractSection(string fullMarkdown, string dimensionName);
    public static string ReplaceSection(string fullMarkdown, string dimensionName, string newSectionBody);
}
```

`ExtractSection` returns `""` if the document is empty or the heading is absent (normal case: first
run ever, or this dimension has never written before - never an error). `ReplaceSection` inserts a
new `## {dimensionName}` section (appended at the end) if absent, or replaces an existing one in
place, preserving every other section's content and order exactly. Both are regex/string-split based
on the fixed `^## ` heading marker - never LLM-parsed. Fully unit-testable with no blob/network
dependency, same testing shape as `AzureReportBlobWriter.BuildBlobPath`/`Slug`.

### 2. `Insights.Agents/TenantMemoryTool.cs` (NEW)

```csharp
public sealed class TenantMemoryTool
{
    public const int MaxSectionChars = 6000;   // hard refuse above this - forces real compaction
    public const int MaxCallsPerRun = 5;       // covers fixed_holistic's ~7 dims with margin, not unbounded
    public const int MaxWriteRetries = 3;      // ETag-conflict retries before giving up

    public TenantMemoryTool(
        IReportEncryptor encryptor, IReportDecryptor decryptor,
        string blobConnectionString, string containerName,
        int tenantId, IReadOnlyList<string> allowedDimensions);

    // Plain C# method - NOT an AI function. Called deterministically BEFORE building the narrate
    // payload, for every dimension in allowedDimensions, so the agent has all its own dimensions'
    // history already in context without needing a read tool call (this session's own "SQL tool
    // read-side: always injected, not a tool" decision, reused here for the same reason).
    public async Task<IReadOnlyDictionary<string, string>> ReadSectionsAsync(CancellationToken ct = default);

    // The one model-facing AI function.
    [Description(/* ... */)]
    public async Task<string> WriteTenantMemoryAsync(
        [Description("Must be one of the dimensions this run is narrating - never another tenant's or another dimension's data.")]
        string dimensionName,
        [Description("The new full markdown body for this dimension's section - replaces what was there, not an append. Keep it under ~3000 chars where possible; a real compaction pass (condense older entries, keep the most recent run's finding verbatim) is expected once it starts approaching the hard cap.")]
        string newSectionMarkdown);
}
```

**`allowedDimensions`** is the closed set for THIS call - one entry for every v2/freehand call
(degenerates to the same closure-only shape `ReadOnlySqlFetchTool` uses for `userId`/`customerId`),
multiple entries for a v1 call covering several dimensions at once (`fixed_holistic`, multi-select
`dimension_selection`). `WriteTenantMemoryAsync` validates `dimensionName` against this set and
refuses (returns an error string, never throws) anything outside it - the model chooses WHICH of ITS
OWN dimensions to update, never an arbitrary one.

**Write mechanics:** `GET` current blob (`BlobRequestConditions` not needed for the read), decrypt,
`TenantMemorySections.ReplaceSection`, re-encrypt, `PUT` with `BlobRequestConditions.IfMatch` (the
ETag from the GET) if the blob existed, or `IfNoneMatch = "*"` if it did not (first-ever write for
this tenant - guards two dimensions racing to create the blob simultaneously). On a `RequestFailedException`
with status 412 (precondition failed - someone else wrote in between): re-GET, reapply the section
replace against the NEW content, retry the PUT, up to `MaxWriteRetries`. Exhausting retries returns an
error to the model; the report itself is unaffected (see fail-soft non-negotiable above).

**Read mechanics:** single `GET`, decrypt, `TenantMemorySections.ExtractSection` per dimension in
`allowedDimensions`. Blob not found (404) -> every section is `""`, not an error - the expected shape
for a tenant's first-ever run.

**All Key Vault/blob failures are caught and degrade to `""` (read) or an error string (write) -
never an unhandled exception reaching the narrate agent's caller.** This is the fail-soft exception
called out above, applied concretely.

### 3. Wiring into `MafAnalystNarrativeAgent` (v2, `Insights.Agents/AnalystNarrativeAgent.cs`)

Same optional-trailing-parameter pattern already used for `ReadOnlySqlFetchTool` earlier this
session: `MafAnalystNarrativeAgent` gains optional constructor params
`(IReportEncryptor? memoryEncryptor, IReportDecryptor? memoryDecryptor, string? memoryBlobConnectionString, string? memoryContainerName)`.
`AnalyzeAndNarrateAsync` already receives `dimensionName`/`userId`/`customerId` - when all the memory
params are present too, it builds a `TenantMemoryTool` with `allowedDimensions = [dimensionName]`
(always exactly one for this path), reads that one section BEFORE building the payload (new
`tenant_history` field, same shape as `dimension_rows` today), and attaches
`WriteTenantMemoryAsync` as a second tool alongside `fetch_scoped_sql_data` (both can be present at
once - a real freehand narrate call may want to trace a fact via SQL AND record what it found).

### 4. Wiring into `MafNarrativeAgent` (v1, `Insights.Agents/NarrativeAgent.cs`) - NEW capability

v1 has no tool-calling today (`agent.RunAsync(message, cancellationToken)`, no `ChatClientAgentRunOptions`).
Gains the same shape v2 already has: optional trailing constructor params (same four as above) PLUS
`NarrateAsync` gains a trailing optional `IReadOnlyList<string>? dimensionNames = null,
int? tenantId = null` (mirrors v2's `userId`/`customerId` addition pattern from earlier today). When
present, builds `TenantMemoryTool` with `allowedDimensions = dimensionNames`, reads ALL those
dimensions' sections, joins them (labelled) into a new `tenant_history` payload field, and attaches
the tool via `ChatClientAgentRunOptions` exactly like v2 already does.

### 5. Activity/orchestrator plumbing

- `NarrateInput` (`Insights.Worker/Orchestration/Activities/NarrateActivity.cs`) gains trailing
  optional `IReadOnlyList<string>? DimensionNames = null, int? TenantId = null`. `NarrateActivity.RunAsync`
  passes them through to `NarrateAsync`.
- `AnalyzeAndNarrateInput` already carries `UserId`/`CustomerId` (added earlier today) - no change
  needed there; `DimensionName` (singular, already present) becomes the tool's one-entry
  `allowedDimensions` list inside `MafAnalystNarrativeAgent` itself, not a new field.
- `InsightsReportOrchestrator.cs`'s v1 `NarrateActivity` call site (~line 573, the `else` branch)
  passes `input.RequestedDimensions` (or, for `fixed_holistic`, the fixed 7-dimension list
  `FixedHolisticComposition` covers - needs a small constant/property added there since
  `input.RequestedDimensions` is null for that ReportType) as `DimensionNames`, and `input.TenantId`.
  Both the initial narrate call AND the revision-loop narrate call (~line 586) pass the same values -
  history is read once at the top of the reflection loop's first pass, not re-read on every revision.

### 6. DI registration (`PaidReportAgentsRegistration.cs`)

New config keys, same fallback style as `ReadOnlySqlConnectionString` earlier today:

```
TenantMemory:BlobConnectionString   (falls back to Azure:BlobConnectionString - same storage account)
TenantMemory:ContainerName          (falls back to literal "insights-tenant-memory")
```

Both narrate agent registrations (`IAnalystNarrativeAgent`, `INarrativeAgent`) gain the encryptor/
decryptor (pulled from DI - already registered as singletons in `WorkerRegistration.cs`) and these
two strings, passed as the new optional constructor arguments. `IReportEncryptor`/`IReportDecryptor`
are registered in `Insights.Worker`, not `Insights.Agents` - `PaidReportAgentsRegistration` already
runs after worker registration in `Program.cs`'s composition, so `sp.GetRequiredService<IReportEncryptor>()`
resolves cleanly at registration time.

### 7. Prompt changes

- `prompts/v2/03_narrative_analyst.md`: new `<tenant_history>` section (mirrors the existing
  `<tools>` section added earlier today for the SQL tool) - documents the `tenant_history` input
  field, the `write_tenant_memory` tool, and explicit compaction guidance: "if your own section is
  approaching ~3000 characters, your job when you write is to CONDENSE older entries (keep dates,
  drop restated detail, merge 'still true' runs into one line) rather than just appending - the hard
  cap is 6000 characters and a write above it is refused outright."
- `prompts/03_narrative.md` (v1): same two additions, scoped to v1's existing structure. v1 handles
  multiple dimensions in one call - the prompt must be explicit that `write_tenant_memory`'s
  `dimensionName` parameter must match one of the ACTUAL dimensions this call is narrating (echoed in
  `tenant_history`'s own keys), never invented.

## Error handling summary

| Failure | Behaviour |
|---|---|
| No history blob yet (first run) | `ReadSectionsAsync` returns all-empty sections, no error |
| Key Vault unreachable during read | Caught, all-empty sections, logged - report proceeds normally |
| Key Vault unreachable during write | Caught, tool returns `{"error": "..."}` to the model, report proceeds normally |
| ETag conflict on write | Retried up to `MaxWriteRetries`, then returns an error to the model |
| `dimensionName` outside `allowedDimensions` | Refused (`{"error": "..."}`), same as an out-of-scope SQL query |
| Section text over `MaxSectionChars` | Refused, model must resubmit a compacted version |

## Testing plan

- **Unit, pure:** `TenantMemorySections` - extract/replace on empty doc, single section, multiple
  sections, replacing an existing vs. appending a new one, preserving unrelated sections verbatim.
  Same style as `ReadOnlySqlFetchToolTests.cs` (14 tests, no I/O).
- **Unit, tool validation:** `dimensionName` outside `allowedDimensions`, section text over the hard
  cap, budget exhaustion after `MaxCallsPerRun` - all pure/validation-only, no real blob connection
  needed (same "unreachable connection string, Validate runs first" trick `ReadOnlySqlFetchToolTests`
  already uses).
- **Real I/O lab test, explicitly flagged as blocked on an open risk:** the read/write round trip
  against a real blob container depends on `AdalKeyVaultReportEncryptor`, whose real-deployment Key
  Vault access has never been confirmed working this session (flagged repeatedly, e.g.
  `sql-fetch-tool-live` memory). A lab test proving the FULL encrypted round trip is written but may
  not pass until that dependency is confirmed - this is not a reason to skip building it, only a
  reason not to claim it works end-to-end until it is actually run against working Key Vault access.

## Explicitly out of scope for this pass

- A UI/report-facing "trend tile" that visibly renders history comparisons - this design only gets
  the history INTO the agent's context and back OUT to storage. Whether/how a dimension's render
  prompt chooses to surface a comparative line in the actual report HTML is that prompt's own
  judgement call (same as any other freehand composition decision) - not built here, and not
  guaranteed to be identical across dimensions.
- Retention/purge policy for `insights-tenant-memory` blobs (parallel to `sql/33`'s own
  `usp_Insights_SnapshotPurge` 400-day-minimum guard) - not addressed; flagged for a follow-up once
  real blob volume in that container is observed.

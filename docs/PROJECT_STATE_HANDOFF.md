# Project State Handoff

**Purpose:** this file exists so a model swap (or a fresh session) can pick up this project without
re-deriving everything from git history and chat transcripts. `CLAUDE.md` is the stable operating
contract (non-negotiables, schema traps, build order) - read that first. This file is the
**current state snapshot**: what's built, what's committed, what's verified, what's still open.

**Last updated:** 2026-09-30, after the orphaned-run safety net (see the incident section below -
read it before touching orchestrator versioning, deployment, or the Licence dimension).
**Keep this current:** when you finish a body of work in this repo, update this file's "Recent
work" and "Open items" sections before ending the session. Stale entries here are worse than none -
if something below turns out wrong, fix it rather than leaving it.

---

## 0. START HERE if you were just handed this project cold

Read in this order:
1. `CLAUDE.md` - the operating contract. Non-negotiables, schema traps, build order. Always current.
2. This file, section 1 (current state) and section 1a (the live incident/finding you need to know
   about before deploying anything or touching `InsightsReportOrchestrator.Version`).
3. `InsightsReportOrchestrator.cs`'s own `Version` doc comment - the authoritative, bump-by-bump
   changelog of every orchestrator call-sequence change. More detailed than this file for that one
   topic specifically.
4. Project memory (`memory/MEMORY.md` and its linked files) if you have access - a parallel,
   date-stamped log of this same work plus the user's own preferences/feedback, not duplicated here.

## 1. Where things stand right now

- **Branch:** `staging`, up to date with `origin/staging`.
- **Latest commit:** `0fab3cc` - "Report orphaned runs as failed instead of running forever" -
  pushed. **NOT yet redeployed to the real UAT pod as of this writing** - confirm with the user
  before assuming the safety net (section 1a below) is actually live.
- **Orchestrator version:** `InsightsReportOrchestrator.Version = "4.4"` (bumped from 4.3 same day
  as the incident in section 1a - see that class's own history doc comment for the full
  bump-by-bump log, the authoritative changelog for orchestrator call-sequence changes).
- **Build:** `dotnet build src/RegtrackInsights/RegtrackInsights.csproj` green, 0 errors (1 stale
  pre-existing nullable warning in `RunEndpoints.cs:104`, unrelated, not this session's doing).
- **Tests:** full `Insights.UnitTests` suite green, 1036/1036.
- **UAT reachable and healthy this session** (contradicts the "unreachable" note further down from
  2026-09-27 - that was a temporary condition, not a standing fact; always re-check reachability
  yourself, don't trust either claim blindly). Confirmed via many real orchestrator runs today plus
  `curl https://uatreginsights.internal.teamleaseregtech.com/health/ready` returning `200 Healthy`
  with the `durable_task_worker` check passing (real header: `X-Health-Token`, value in
  `appsettings.uat.json`'s `HealthCheckAuth:Token` - never paste that value into chat/docs).
- **A real UAT worker pod IS deployed and live** (`trpl-regtrack-dot-net-core-api-*`), actively
  dequeuing from the shared Durable Task hub, processing real frontend-triggered traffic alongside
  anything you enqueue yourself. This directly supersedes the 2026-09-27 "nothing Insights-related
  is confirmed deployed" note below - that was wrong even then per other evidence in this same
  file's history; trust the live health check over either written claim.

## 1a. Live incident, 2026-09-30: orphaned runs after an orchestrator version bump - READ BEFORE DEPLOYING

**What happened:** `InsightsReportOrchestrator.Version` was bumped `4.3` -> `4.4` (commit `d9e3968`,
part of a Licence-dimension feature). The user redeployed to pick it up. Redeploying a build that
registers a NEW version number strands every orchestration instance still recorded under the OLD
version - `WorkerRegistration.cs` registers exactly ONE version string
(`worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(InsightsReportOrchestrator.Name,
InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)))`), so a pod running the new
build has no handler left for a `4.3`-history instance. 3 real tenant-1285 Licence requests were
mid-`RenderHtmlActivity` at that exact moment and got permanently orphaned - reported "running"
forever, could not be resumed, and (important) **could not even be cancelled** - `IInsightsRunCanceller.CancelAsync`
just posts a termination event onto the same unreachable per-instance queue, which needs a pod that
still knows the instance's version to process it. Confirmed via direct SQL polling over a 2.5-minute
window: completely flat, zero queue activity, zero new history - not a slow retry loop, genuinely
stuck. A pre-existing Departments run (`73fec362...`) has been stuck the exact same way since
**2026-09-20**, from an earlier version bump - this is a systemic, recurring gap, not a one-off.

**Real fix shipped (commit `0fab3cc`):** `DurableTaskRunStatusReader.GetStatusAsync` now reports a
`"running"` status as `"failed"` the moment anyone checks it, if either (a) the run's recorded
`OrchestrationState.Version` isn't `InsightsReportOrchestrator.Version`, or (b) `LastUpdatedTime` is
30+ minutes stale. Pure read-time check (`IsOrphaned`, internal static, unit-tested directly -
`TaskHubClient.GetOrchestrationStateAsync` is not virtual so the class itself can't be mocked). No
background job, no new monitoring - CLAUDE.md non-negotiable #2 (fail closed, fail loudly) applied
to this specific gap. **Not yet verified live** - needs a real redeploy + a real version-mismatched
run to confirm it actually flips to "failed" in the API response.

**The 3 stuck instances were cleared** via the Durable Task SQL provider's own real
`dt.PurgeInstanceStateByID` stored procedure (table-valued `@InstanceIDs` parameter, deletes
`NewEvents`/`NewTasks`/`Instances`/`History`/`Payloads` for exactly those ids, one transaction) -
NOT a hand-rolled DELETE. This is the correct, scoped, official tool for this - prefer it over a
manual multi-table DELETE if you ever need to clear a stuck instance again. Verified with
before/after row counts on all 5 tables; `NewEvents` had exactly 5 rows before the purge - those
were the render RESULTS that had finished and queued but could never be picked up, direct
confirmation of the root cause.

**Still open, NOT built (the user was mid-decision on this when this file was last updated):**
- **Option A (small, same-day-sized):** when an orphaned run is detected, auto-purge its state
  right then (reuse `dt.PurgeInstanceStateByID`) so the NEXT generate request for that same
  (tenant, scope, reportType, period) key starts genuinely fresh instead of colliding with the
  zombie's deterministic run id. User still sees one failure, has to click generate again, but it
  then actually works with no manual intervention.
- **Option B (real structural fix, needs proper design/scoping first - explicitly deferred):** keep
  the PREVIOUS orchestrator version also registered (multi-version dispatch) so an in-flight run
  started under the old version can actually finish normally across a deploy, with zero visible
  impact to the user. Bigger change - replay-safety/determinism implications need real design work
  before writing code. A lighter variant (register the old version as a stub that cleanly refuses
  with "report interrupted by an update, please regenerate" instead of trying to let it finish) was
  also discussed - still just a discussion, nothing built.
- Neither A nor B is implemented. If you pick this up, brainstorm/scope B properly first per the
  project's own process convention (this file's own history shows every prior orchestrator
  call-sequence change gets a full doc-comment changelog entry in `InsightsReportOrchestrator.cs`
  BEFORE shipping - don't skip that discipline for whichever option gets built).

**Files to read for this specific incident, in order:**
1. `src/RegtrackInsights/Insights.Worker/Orchestration/InsightsReportOrchestrator.cs` - the
   `Version` const's own doc comment, specifically the 4.3->4.4 bump note and every `[VERIFY BEFORE
   DEPLOY]` note in the whole changelog (that convention exists exactly because of this failure
   mode - it was followed correctly in the commit message, then the actual check never happened).
2. `src/RegtrackInsights/Insights.Worker/WorkerRegistration.cs` - the single-version orchestrator
   registration (`worker.AddTaskOrchestrations(...)`), the real mechanism that makes an old-version
   instance unloadable.
3. `src/RegtrackInsights/Insights.Worker/Orchestration/DurableTaskRunStatusReader.cs` - the safety
   net (`IsOrphaned`) shipped today.
4. `src/RegtrackInsights/Insights.Worker/Orchestration/DurableTaskRunCanceller.cs` - why cancelling
   an orphaned run doesn't work either (same underlying queue problem).
5. `tests/Insights.UnitTests/DurableTaskRunStatusReaderOrphanTests.cs` - the safety net's tests,
   also the clearest worked examples of what "orphaned" means in code.
6. Project memory: look for a 2026-09-30 entry on this incident if you have memory access - it has
   the full diagnostic trail (SQL queries used, exact timestamps, the 2.5-minute polling proof).

## 2. Recent work, newest first

### 2026-09-30: Percentage/pp hover-link feature + the orchestrator-version incident (this file's biggest update)

**Feature:** a hover-link on percentage figures in freehand report prose (e.g. `(44.9%)`) that
opens a small popup - title, one-line description, a "HOW IT IS CALCULATED" fraction box with the
REAL numbers on it (not just field labels). Modeled on a real product reference screenshot the user
supplied. Live on 6 dimensions' render prompts: Licence (`_v10.md`), Location (`_v7.md`),
Departments (`_v7.md`), Users (`_v6.md`), BacklogAging (`_v6.md`), Act (`_v5.md`) - **not** Entity
(fixed_holistic template, a structurally different render path, not started).

**Real bugs found and fixed along the way (all via a LOCAL LAB, not real deploys - see the
methodology note below, this is the reusable part):**
1. The popup's panel used block-level tags (`aside`/`h4`/`p`) nested inside the prose `<p>` it
   lives in. A browser auto-closes a `<p>` on the first block-level tag it meets, silently
   orphaning the popup from its trigger - link rendered fine, nothing ever popped. Fixed: `span`
   only, throughout `.hr`, in every one of the 6 dimension prompts.
2. The panel used `position:absolute` - every section card is `overflow:hidden` (needed for its own
   corner-circle decoration), so the popup got silently clipped once it grew past the card's
   remaining space. Fixed: `position:fixed` (escapes any ancestor's overflow, same reason chart
   panels already use it) plus a small `pfPlace` script that computes the real screen anchor point
   from the trigger's `getBoundingClientRect()`, clamped to viewport.
3. The shared `.hr-panel` rule also sets `bottom:16px` (its own right-docked chart-panel layout) -
   left in place while `pfPlace` sets `top` to a large value, this stretches the popup's HEIGHT all
   the way to the viewport bottom (a 500px+ blank card). Fixed: explicit `right:auto;bottom:auto`
   override alongside the `position:fixed` change.
4. User asked for the fraction to show real numbers ("75"/"167") with the field label as a caption
   underneath, not just bare labels with no numbers - straightforward content change, verified live.

**Methodology, worth reusing:** rather than deploy-and-check each iteration (slow, and each fix
needed 2-3 rounds), built a LOCAL LAB - a temporary xunit test
(`tests/Insights.IntegrationTests/LicencePctLinkLabTemp.cs`,
`tests/Insights.IntegrationTests/ActPctLinkLabTemp.cs`, both still on disk, gated behind
`INSIGHTS_RUN_PREVIEW=1`, never committed) that runs `compose -> narrate -> render` DIRECTLY
in-process against real UAT data, with NO Durable Task hub, no enqueue, no worker pod involved -
real LLM calls (real tokens billed), real SQL data, but completely bypasses the queue/deploy cycle.
This is the RIGHT way to iterate on prompt content quickly. **One real gotcha found using it:** the
lab must also call `DimensionDataInjector.Inject(...)` (the same call the real orchestrator makes)
- without it, the model's own chart-reading script throws on the missing `#insights-data` block and
silently kills the rest of that `<script>` tag, including anything else in it. Looked exactly like a
positioning bug the first time; wasn't.

**CRITICAL, do not repeat this mistake:** do NOT run a second full Durable Task WORKER (i.e.
`AddInsightsOrchestrationWorker`, not `AddInsightsOrchestrationClient`) against the shared UAT hub
to test real orchestration wiring locally. This was tried earlier in this project's history and
caused real "Duplicate execution of ...Activity was detected!" errors from two workers racing the
same queued message - see `Program.cs` lines ~80-92 for the standing comment on this,
`Insights:ClientOnly=true` exists specifically to prevent it. If you need to prove real
orchestrator-level wiring (not just prompt content), the only safe options are unit tests or an
actual deploy - there is no safe local-worker shortcut for that specific class of test.

**The orchestrator version bump (4.3 -> 4.4) that was part of this same body of work directly
caused the incident in section 1a above** - see that section for the full story, root cause, fix,
and what's still open. Read it before deploying anything that touches `InsightsReportOrchestrator`.

**Licence dimension moved to production readiness:** testing team approved Licence specifically
(other 5 dimensions above are UAT-only, not yet tested/approved for production). A deployment
package was assembled for the DBA (`01_classification_dictionary_v2.sql`,
`22_licence_report_status_dictionary_v2.sql`, `25_usp_Insights_Dimension_Licence_window_text_v2.sql`,
plus `99_rollback_v2.sql`) - **note file 01 is NOT Licence-specific**, it flips the global overdue
rule for every other dimension too (a real FK dependency: `22`'s seed needs dictionary version 2's
row to exist, which only `01` creates) - the user explicitly chose to deploy the full v2 migration
as-is (byte-for-byte what's live on UAT) rather than a narrower Licence-only variant. See
`sql/v2/README.md` for the complete rule-by-rule diff and proof numbers.

### 2026-09-26/27: Reasoning-trace explainer pipeline

**What it is:** for every freehand single-dimension report, a second artifact - a Markdown
document explaining *how the report was built*: which raw rows back each claim, what formula
produced each percentage, how to read each chart, and the full underlying reasoning trail. Built
for internal testers, not customers. See `Insights.Agents/ReasoningExplainerAgent.cs`'s own doc
comment and `prompts/08_reasoning_explainer.md` for the prompt contract (7 sections: report
summary, composition rationale, claim-by-claim trace, how to read each visual, data quality notes,
live SQL run, full reasoning trail).

**Why gpt-4o-mini, not the reasoning-heavy deployment the other agents use:** this is an
explain/cite task over already-verified data, not a judgement call - a small model is enough, and
using the o-series/gpt-5 deployment for it would be needless cost. Confirmed live: gpt-4o-mini
rejects `ReasoningOptions`/`reasoning.effort` outright (`HTTP 400 unsupported_parameter`) - it has
no reasoning-effort concept at all. `MafAgentFactory.CreateSimpleTextAgent` (new method, alongside
the existing `CreateJsonAgent`/`CreateTextAgent`) skips the `RawRepresentationFactory` block
entirely for this reason - `supportsReasoning: bool` threaded through the shared private `Create`
method, zero behavior change for every existing caller.

**Pipeline (as of commit `d9a69be`):**

```
PersistActivity (report already saved)
    -> BuildReasoningTraceActivity (new, Node 12b)
         - only runs when freehandDimensionName is not null (single-dimension freehand path)
         - assembles ReasoningTraceBundle: CompositionPlan, Assertions, Findings, real dimension
           rows/control totals JSON, DataQuality, plus a REAL read-back of
           InsightsAgentReasoningLog / InsightsToolInvocationLog for this run's RunId
           (IAgentReasoningRecorder.GetForRunAsync / IToolInvocationRecorder.GetForRunAsync -
           new read methods on previously write-only interfaces)
         - calls IReasoningExplainerAgent.ExplainAsync(bundle) -> gpt-4o-mini -> Markdown
         - writes the Markdown via IReasoningTraceStore.WriteAsync - a PERMANENT, plain-text
           (not envelope-encrypted) blob, SAME container the report's own encrypted blob lives
           in (no new container - explicit user instruction), deterministic path derived from
           (TenantId, ReportType, GeneratedAtUtc, ReportId)
         - FAILS SOFT, ALWAYS: every exception caught/logged, returns Written:false, never
           affects what the orchestrator returns. The report has already been persisted by the
           time this runs - there is nothing to roll back.

ReportContentService.OpenAsync (the read side, API host)
    - re-derives the SAME deterministic blob path from GeneratedReport's own columns
      (CustomerId, ReportType, GeneratedAtUtc, Id) - no new schema/column needed
    - if the trace blob exists, republishes a FRESH throwaway copy via the SAME
      IReportViewPublisher the report itself uses (never a long-lived SAS against the
      permanent trace blob - matches the report's own "no public URL, no long-lived SAS"
      design principle, IReportViewPublisher's own doc comment)
    - returns ReportContentResult.ReasoningContentUrl (null if no trace exists for this report)

GET /api/insights/reports/{reportId}/content
    - JSON response gains "reasoningContentUrl" (null-safe) alongside the existing
      contentUrl/expiresUtc/sandboxRequired fields
```

**DI wiring (completed this pass - this was the only missing piece from the prior session):**
- `IReasoningTraceStore` -> `AzureReasoningTraceStore`, registered inside
  `WorkerRegistration.RegisterReportCodec` (shared by both the worker and the API host), reusing
  the SAME `Azure:BlobConnectionString`/`Azure:BlobContainer` the report's own blob writer already
  uses.
- `IReasoningExplainerAgent` -> `MafReasoningExplainerAgent`, registered in
  `PaidReportAgentsRegistration.cs`, model literal `"gpt-4o-mini"`, same `Llm:Maf:Endpoint`/
  `Llm:Maf:ApiKey` every other non-freehand agent in that file uses.
- `IToolInvocationRecorder` was previously only a **local variable** inside
  `PaidReportAgentsRegistration.cs` (built once, captured in a closure for the narrate agent's
  `onSqlToolInvoked` callback) - not resolvable via DI from anywhere else. Now ALSO registered as
  a singleton (`services.AddSingleton(toolInvocationRecorder)`) so `BuildReasoningTraceActivity`
  resolves the exact same instance rather than opening a second connection.
- `IAgentReasoningRecorder` was already DI-registered (`AgentReasoningRegistration.cs`,
  `AddScoped`) - no change needed there.
- `BuildReasoningTraceActivity` registered transient in `WorkerRegistration.
  AddInsightsOrchestrationWorker`, all four optional collaborators resolved via `GetService`
  (never `GetRequiredService`) so a host missing one of these registrations degrades to a no-op
  rather than failing to start. Added to the `ActivityCreator<...>` list passed to
  `worker.AddTaskActivities(...)`.

**Important correction from the prior session's notes:** an earlier doc comment in
`InsightsReportOrchestrator.cs` said this activity was "inert by default... until a separate,
deliberate DI registration step." That registration step is now DONE - **the feature is live on
any normally-configured host**, not inert. The doc comment was corrected in the same commit. If you
see any OTHER comment anywhere claiming this feature is unwired/inert, it is stale - fix it.

**Real cost/behavior change this introduces:** every freehand single-dimension report generation
now bills one additional small-model call and performs one additional blob write, after persist.
Fails soft, so it cannot break report generation - but it is not free, and nobody has yet decided
whether every freehand dimension should get this, or whether it should be gated behind a
config flag the way `Reports:CooldownEnabled` gates the cooldown toggle. **This was not asked for
by the user and is a real open question** - see Open Items below.

**Not yet done:**
- No live orchestrator run has exercised this end-to-end (UAT down). The lab test
  (`tests/Insights.IntegrationTests/ReasoningTraceLabTest.cs`) proves compose -> narrate -> render
  -> explain works against real Minda data, in-process, with no real Durable Task orchestration
  involved.
- No config flag exists to disable this feature independently of whether its DI dependencies are
  registered. Today "on" is entirely a function of whether `RegisterReportCodec`/
  `PaidReportAgentsRegistration` ran - there is no equivalent of `Reports:CooldownEnabled` for this
  feature.
- `sql/32_agent_reasoning_log.sql` and `sql/34_tool_invocation_log.sql` back
  `IAgentReasoningRecorder`/`IToolInvocationRecorder` - confirm these are actually deployed to
  whatever environment the reasoning-trace feature runs in before relying on the reasoning-log
  portion of the trace document (the rest of the bundle - CompositionPlan, Assertions, Findings,
  rows - never depends on either table).

### 2026-09-25/26: Cooldown redesign

Cooldown key changed from `(scope, reportType, period)` to `(scope, reportType, dimension)` - once
a tenant has generated a report for a given dimension, that dimension locks, full stop, no more
combination-hunting. `Reports:CooldownEnabled` config toggle added (default `true` = real
production behavior; set `false` locally/UAT to regenerate the same dimension repeatedly while
testing - already flipped `false` in the local, gitignored `appsettings.uat.json`).
`GeneratedReportUnit.Message` now returns the exact user-facing text: *"You already generated a
report for this. There is a cooldown period and N day(s) left."*

Dimension identity resolution: interim hack was reading a `::dim=` suffix baked into
`GeneratedReport.Period`; `sql/33_generated_report_dimension_key.sql` added a real
`GeneratedReport.RequestedDimensions NVARCHAR(200)` column (deployed to UAT, verified via
`sys.columns`) plus a CHECK constraint, with `ReportDimensionKey.Normalize` guaranteeing the old
suffix and the new column can never disagree. `PersistActivity` now populates the real column.

### 2026-09-25: Hard period-window gate on 9 dimensions

Every dimension (Act, Event, Location, Entity, Risk, Nature, Departments, Users, Internal) now
takes `@WindowStart`/`@WindowEnd` and narrows population via the `#active`/`DELETE ... WHERE NOT
EXISTS` pattern, joining `ComplianceScheduleOn.ScheduleOn` (the real per-occurrence due date - NEVER
`Act.StartDate`, which is the law's enactment date, not a due date). Deployed to UAT, live-verified
via direct report generation. `ReadOnlySqlFetchTool` (the LLM-facing read-only SQL tool used during
narrate) was found to NOT respect this same window - fixed by threading `windowStart`/`windowEnd`
into its constructor and narrowing its own `#scoped` temp table the same way, so a live SQL query
during narrate can never see data outside the report's own period. Verified via a direct,
no-LLM-involved comparison test (windowed count == the dimension's own `ScopedInstances`).

Narrative "window" `data_quality` entries needed real per-dimension text (not generic filler) -
delivered via **versioned `_v2` prompt files**, never overwriting the originals (composition and
render prompts both). This is now the standing convention for landing a field-level prompt fix
without touching an already-battle-tested prompt file in place.

### 2026-09-22/23: Live-review-found data bugs (all real SQL/C# name mismatches, not LLM hallucination)

Found by rigorously cross-checking real generated report output against raw fetched data on Minda:
- **Location:** `#assert.Caveat NVARCHAR(200)` truncation - a caveat literal was 205 chars, fixed
  to 173/186 chars, redeployed. Permanent regression test + `CLAUDE.md` trap added (§5).
- **Users:** `SqlDimensionRepository.GetUsersAsync` never called the existing, unit-tested
  `UsersHeadcountCalculator.Compute` - `PerformerUserCount`/`ReviewerUserCount` were always 0 while
  the report's own table showed real named users with real counts. Fixed via post-processing.
- **Departments:** C# `TenantOwnerlessPct` never matched SQL's `TenantNoInstanceOwnerPct` - renamed
  (confirmed unused elsewhere, no live-output impact, but a real landmine left for later).
- **Nature:** C# `SumOfRows` never matched SQL's `CategorisedInstances` - renamed, plus fixed the
  `_v2` prompts that referenced the wrong field name.
- **Internal:** six broken fields (`StatutoryOwnerlessPct`/`InternalOwnerlessPct` on
  `InternalControlTotals`, four more on `InternalRow`) - all renamed to match real SQL columns.

### 2026-09-22/23: Users moved from a fixed template to freehand (real, shipped)

Users' fixed dedicated template (Sambram's design system) retired entirely - real production
change, not a lab experiment. Orchestrator bumped 3.8 -> 3.9 to remove
`ValidateUserDimensionStructureActivity` (that gate enforced the OLD fixed template's markup and
would have refused every real freehand render). Lab-tested first: the composition agent surfaced a
genuinely different, tenant-specific hero (a 100% sole-reviewer-dependency finding) the fixed
template never could. **Entity is now the only real dimension that is NOT freehand** - it keeps its
fixed 6-tab template on purpose (real Angular-product reference UI).

## 3. Environment facts worth knowing

- **UAT** (`10.13.0.6`, backs `ConnectionStrings:RegTrack`, `ConnectionStrings:DurableTaskHub`,
  `ConnectionStrings:RegTrackReportsWrite`) - reachable and healthy as of 2026-09-30 (see section 1
  above), but it HAS been intermittently unreachable at other points in this project's history.
  Confirm reachability yourself before assuming any UAT-dependent step will work - don't trust a
  written claim (including this one) over a live check: `Test-NetConnection -ComputerName
  10.13.0.6 -Port 1433`, or `curl .../health/ready` with the real `X-Health-Token`.
- **A real worker pod is deployed and live**, actively dequeuing the shared Durable Task hub -
  confirmed 2026-09-30, and per `Program.cs`'s own standing comment (~line 80), confirmed earlier
  too ("a session from a Kubernetes pod, trpl-regtrack-dot-net-core-api-*, actively dequeuing").
  **Never run a second full local worker (`AddInsightsOrchestrationWorker`) against this shared
  hub** - causes real duplicate-execution races with the live pod. `AddInsightsOrchestrationClient`
  (enqueue/poll/cancel only, no dequeue) is always safe from a local process; the full worker
  registration is not. See section 2's 2026-09-30 entry for the fuller story and the local-lab
  alternative for testing prompt/render content without touching the hub at all.
- **Prod-readonly replica** (`regtech_dev01_readonly`, `10.224.254.4`) has real Minda tenant data
  but mirrors a SEPARATE, unmanaged source and does **not** have this project's newer SQL
  procs/columns deployed to it. Confirmed: calling a window-gated proc against it throws "Procedure
  or function ... has too many arguments specified." When lab-testing against this replica, pick a
  dimension that was NOT touched by whatever SQL work is currently in flight.
- **Key Vault / encryption**: `AdalKeyVaultReportEncryptor` must be constructed with a WRITABLE
  connection string, never the read-only replica's - its Key Vault client secret is stale on the
  replica's copy, producing a real `AADSTS7000215` failure. Same reasoning applies to
  `SqlAgentReasoningRecorder`/`SqlToolInvocationRecorder` - both should point at
  `ConnectionStrings:RegTrackReportsWrite` (or its `RegTrack` fallback), never a read-only-only
  connection string.
- **Test tenant/lab conventions:** tenant 1300 / user 22426 on the readonly replica is the
  established lab tenant for several lab tests. Tenant 1285 (user 11416) is the only tenant among
  the documented validation set that exercises Location's `ImprisonmentOverdue > 0` caveat branch -
  keep it in `DimensionRepositoryTests.ValidatedTenants`. Tenant 29 (user 645, `LicenseRoleID`
  MGMT) is a good choice specifically when you need a REAL, non-trivial percentage (real Act
  overdue rate 80.4%, real Licence expired rate up to 44.9% depending on window) - 1285's own real
  numbers are mostly too small to exercise percentage-shaped features meaningfully.
- **Deployment ownership:** Key Vault RBAC/whitelist is Tanvi's responsibility, not this project's.
  Production SQL deploys (as opposed to UAT) go through a DBA (Rahul Chopde, as of 2026-09-30) -
  hand over exact, ordered `.sql` files plus a README stating install order, never deploy production
  SQL yourself. Worker/API redeploys are the user's own action (Ravindra owns the hosting/deploy
  decision per earlier project history) - never assume a commit you pushed is live until the user
  confirms a redeploy happened.

## 4. Open items (real, not yet decided or built)

1. **Orchestrator-version orphaning (section 1a) - the safety net (Option A-adjacent) is shipped;
   Option B's real structural fix is now BUILT and available, not yet exercised by a real version
   bump.** See `docs/superpowers/specs/2026-09-30-orchestrator-multi-version-dispatch-design.md`
   and `docs/superpowers/plans/2026-09-30-orchestrator-multi-version-dispatch.md`. Confirmed live:
   DurableTask.Core's own NameVersionObjectManager supports multiple registered versions of the same
   orchestrator name, resolved by exact match - a full end-to-end test proves an old-version
   instance completes via a worker that also knows a new version
   (`MultiVersionOrchestratorResumeTests`). `WorkerRegistration.OrchestrationRegistrations` is the
   real registration list; `InsightsReportOrchestrator.cs`'s own changelog now documents the
   freeze-on-bump steps right next to its `Version` const. Nothing has actually frozen a real
   version yet - that happens naturally at the NEXT version bump, per the changelog note added
   there.
2. **Percentage hover-link feature - 4 of 6 dimensions unverified.** Licence and Act were verified
   against real tenant-29 data via the local-lab method before committing. Location, Departments,
   Users, BacklogAging share the exact same proven mechanism but have NOT been individually
   verified - low risk (the mechanism-level bugs are the ones already found and fixed), but verify
   after the next deploy rather than assuming.
3. **Entity dimension does not have the percentage hover-link feature at all.** Uses the
   fixed_holistic template, a structurally different render path from the other 9 dimensions -
   would need its own design, not a copy-paste of the freehand-dimension approach.
4. **No kill switch for the reasoning-trace feature.** Unlike cooldown, there is no
   `Reports:ReasoningTraceEnabled`-style flag - it is on wherever its DI dependencies happen to be
   registered. Worth asking the user whether that is intentional before this reaches a real
   production tenant, given it bills extra tokens on every freehand-dimension report.
5. **Reasoning-trace pipeline has never been proven through a live orchestrator run** (as of
   2026-09-27; not re-checked since). Needs a real end-to-end test: enqueue a real
   freehand-dimension run, confirm `BuildReasoningTraceActivity` actually executes in sequence,
   confirm the blob lands, confirm `/content` returns a working `reasoningContentUrl`.
6. **`GeneratedReport.ScopeDescriptor` is a label, not a snapshot** (category-axis re-auth gap) -
   flagged in `ReportContentService`'s own doc comment as a known, deliberate limitation. Still open.
7. Confirm `sql/32`/`sql/34` are deployed everywhere the reasoning-trace feature will actually run,
   before depending on the reasoning-log section of the generated trace document being populated.
8. **The Licence production SQL package deploys MORE than Licence** (section 2's 2026-09-30 entry) -
   `01_classification_dictionary_v2.sql` flips the overdue rule for every dimension, not just
   Licence. The user explicitly chose this scope; if that decision is ever revisited, a narrower
   Licence-only variant (create dictionary version 2's row without copying classification data or
   flipping `IsCurrent`) was discussed but never written - Licence's own proc doesn't care which
   version is current, since its status seed goes into both.

## 5. Where to look for more detail

- `CLAUDE.md` - architecture, the five non-negotiables, schema traps, build order. Read first,
  always current.
- `InsightsReportOrchestrator.cs`'s own `Version` doc comment - the authoritative, bump-by-bump
  changelog of every orchestrator call-sequence change, with the reasoning for each. Read the
  4.3->4.4 entry and every `[VERIFY BEFORE DEPLOY]` note before your own next version bump.
- `sql/v2/README.md` - the RegTrack-parity SQL migration (overdue rule, Licence status), full
  rule-by-rule diff and real proof numbers against RegTrack's own reports.
- `docs/superpowers/specs/` - design docs for tenant memory, narrative analyst agent, and other
  features that got a full written spec before implementation.
- Project memory (`memory/MEMORY.md` and its linked files, if this session has access to it) has a
  parallel, more granular log of this same work plus user preferences/feedback not appropriate to
  duplicate here.

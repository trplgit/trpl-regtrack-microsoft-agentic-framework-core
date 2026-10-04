# Five Dimensions Freehand Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring TimelinessFY, ForwardPipeline, EvidenceIntegrity, ForwardRisk, CoverageGaps up to
the same standalone freehand `dimension_selection` report capability (real LLM composition + a
dimension-specific render prompt) that the 7 already-live dimensions have, then prove it end to
end with a real report generation.

**Architecture:** No orchestrator logic changes — `InsightsReportOrchestrator.cs:608` already
routes any name in `FreehandDimensions.Names` into real composition, and the render-agent lookup
already falls back `"dimension_selection:{Name}"` → `"dimension_selection"`. 4 of 5 dimensions are
already fetched unconditionally every run (`FetchDimensionsActivity.cs:270-274`), so they need only
prompt files + two dictionary registrations. CoverageGaps additionally needs new DTOs, a repository
method, and one new fetch call, mirroring `ForwardRisk`'s existing pattern exactly (same per-branch
grain).

**Tech Stack:** C# / .NET 8, Dapper, xUnit, Markdown prompt files consumed by MAF agents (Azure
OpenAI `gpt-5.6-sol`).

**Spec:** `docs/superpowers/specs/2026-10-04-five-dimensions-freehand-design.md`

## Global Constraints

- Zero SQL file changes (confirmed by audit in the spec — all 5 already RegTrack-parity, no
  occurrence-split needed).
- No changes to `InsightsReportOrchestrator.cs`, `ReportNumberTracer`, `PublishGateActivity`, or
  any existing `Inject*Activity` — all already dimension-agnostic or out of scope.
- CoverageGaps error codes: **51200** scope-denied base, **51201**/**51202** reconciliation (CLAUDE.md
  §5b reserves this block for sql/27) — no dictionary-gap code.
- Every new prompt file follows the exact freehand contract already proven in the 10 existing
  pairs: composition decides section count/order/hero from real `dimension_rows`/
  `dimension_control_totals` only (never fabricated), render gets creative freedom under the
  shared theme/CSS contract. No vague umbrella labels without naming what's inside; say each fact
  once.
- Follow `CLAUDE.md`'s own testing discipline: never validate a new dimension on one tenant —
  every repository-level test uses `DimensionRepositoryTests.ValidatedTenants` (5 real tenants).

## Review Focus

- **CoverageGaps requested alongside a window-requiring dimension in the same call** — its own
  `GetCoverageGapsAsync` takes no window params (matches `ForwardRisk`'s signature), so a mixed
  multi-dimension request must not accidentally drop it or crash on a missing window.
- **A tenant with zero leaf branches in scope** — `sql/27` THROWs 51202 if rows don't cover every
  leaf branch; `ExecuteAsync`'s existing THROW-to-exception translation must surface this as a
  real failed-dimension entry, not crash the whole run (same pattern every other dimension already
  relies on — verify, don't reinvent).
- **A composition/render prompt silently falling back to the generic template** — if a dictionary
  key is misspelled (e.g. `"dimension_selection:Timelinessfy"` vs `"TimelinessFY"`), the lookup
  falls through to the generic prompt with no error. Each dimension's live test must assert the
  SPECIFIC prompt fired, not just that a report was produced.
- **ForwardPipeline/EvidenceIntegrity requested as the sole dimension with no real due-soon data**
  (a tenant with nothing in the forward window) — the composition prompt must handle an
  all-empty/all-zero row set gracefully (same "no scheduled work" framing the live Risk dimension
  already uses for this exact case — pattern-match that, don't invent new copy).
- **CoverageGaps's `#rows` table has nullable `PeerMedianObligations`/`PctOfPeerMedian`/`GapRank`**
  (branches with `InPeerSet = 0` never get a peer comparison) — the DTO and the render prompt must
  treat these as genuinely absent, not zero.

---

## Task 1: CoverageGaps DTOs

**Files:**
- Modify: `src/RegtrackInsights/Insights.Domain/DimensionShapes.cs` (append after `ForwardRiskRow`, ~line 763)

**Interfaces:**
- Produces: `CoverageGapsControlTotals` (sealed record), `CoverageGapsRow` (sealed record) — consumed by Task 2.

- [ ] **Step 1: Add the two records**

```csharp
// ── Coverage gaps (sql/27) ─────────────────────────────────────────────────────────────

/// <summary>
/// Peer-comparison review candidates, not violations - a branch is flagged when its labour
/// obligation count sits well below its peer group's median (peer key = state x establishment
/// class). Grain is the leaf branch. Deployed proc, live in prod - never modified from here.
/// </summary>
public sealed record CoverageGapsControlTotals
{
    public int LeafBranchesInScope { get; init; }
    public int PeerSetBranches { get; init; }
    public int PeerGroupsQualifying { get; init; }
    public int PeerGroupsTooSmall { get; init; }
    public int NearUniversalObligations { get; init; }
    public int Gaps { get; init; }
    public int SumOfRowGaps { get; init; }
    public bool Reconciled { get; init; }
    public int GapsFullConfidence { get; init; }
    public int GapsReducedConfidence { get; init; }
    public int BranchesWithGaps { get; init; }
    public int UnderConfiguredBranches { get; init; }
    public int UnknownNodeType { get; init; }
    public int ThresholdObligationsExcluded { get; init; }
    public decimal CoverageThreshold { get; init; }
    public int MinPeers { get; init; }
    public string? Method { get; init; }
}

public sealed record CoverageGapsRow
{
    public int BranchID { get; init; }
    public string? BranchName { get; init; }
    public int? StateID { get; init; }
    public int? NodeTypeId { get; init; }
    public string? Class { get; init; }
    public bool InPeerSet { get; init; }
    public int? PeerSetSize { get; init; }
    public int LabourObligations { get; init; }
    public decimal? PeerMedianObligations { get; init; }
    public decimal? PctOfPeerMedian { get; init; }
    public int Gaps { get; init; }
    public int GapsFullConfidence { get; init; }
    public int GapsReducedConfidence { get; init; }
    public bool UnderConfigured { get; init; }
    public int? GapRank { get; init; }
    public string? Flags { get; init; }
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/RegtrackInsights/RegtrackInsights.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/RegtrackInsights/Insights.Domain/DimensionShapes.cs
git commit -m "Add CoverageGaps DTOs (CoverageGapsControlTotals/CoverageGapsRow)"
```

---

## Task 2: CoverageGaps repository method + interface + fetch wiring

**Files:**
- Modify: `src/RegtrackInsights/Insights.Data/IDimensionRepository.cs`
- Modify: `src/RegtrackInsights/Insights.Data/SqlDimensionRepository.cs:27` (error base constant), `~296` (new method, after `GetForwardRiskAsync`)
- Modify: `src/RegtrackInsights/Insights.Worker/Orchestration/Activities/FetchDimensionsActivity.cs:274` (new unconditional fetch line)
- Test: `tests/Insights.IntegrationTests/DimensionRepositoryTests.cs` (new theory test)

**Interfaces:**
- Consumes: `CoverageGapsControlTotals`/`CoverageGapsRow` from Task 1.
- Produces: `IDimensionRepository.GetCoverageGapsAsync(int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default)` returning `Task<DimensionResult<CoverageGapsControlTotals, CoverageGapsRow>>` — consumed by `FetchDimensionsActivity`.

- [ ] **Step 1: Write the failing test**

Add to `DimensionRepositoryTests.cs`, after `GetLicenceAsync_ReturnsWellFormedResult` (~line 156):

```csharp
    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetCoverageGapsAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetCoverageGapsAsync(userId, customerId);
        AssertWellFormed(result, "CoverageGaps");
        Assert.True(result.ControlTotals.Reconciled);
    }
```

- [ ] **Step 2: Run it to verify it fails (method doesn't exist yet)**

Run: `dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~GetCoverageGapsAsync_ReturnsWellFormedResult"`
Expected: Build FAILS — `'IDimensionRepository' does not contain a definition for 'GetCoverageGapsAsync'`.

- [ ] **Step 3: Add the interface method**

In `IDimensionRepository.cs`, add after the existing `GetForwardRiskAsync` declaration:

```csharp
    /// <summary>
    /// Peer-comparison review candidates (sql/27) - a branch's labour obligation count against
    /// its peer group's median. No window parameter: this is a point-in-time configuration
    /// comparison, not a schedule/occurrence metric, same reasoning as ForwardRisk/BacklogAging.
    /// </summary>
    Task<DimensionResult<CoverageGapsControlTotals, CoverageGapsRow>> GetCoverageGapsAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Add the error base constant and implementation**

In `SqlDimensionRepository.cs`, add the constant after `LicenceErrorBase` (line 27):

```csharp
    private const int CoverageGapsErrorBase = 51200;
```

Add the method after `GetForwardRiskAsync` (~line 296):

```csharp
    /*  sql/27 owns block 51200-51209: 51200 SCOPE DENIED, 51201/51202 RECONCILIATION FAILED
        (per-branch gap counts not tying to the gap set; rows not covering every leaf branch). No
        dictionary-gap code of its own - EXEC dbo.usp_Insights_AssertStatusCoverage (sql/01) covers
        that path, same as every other dimension. Deployed and live in production; never modified
        from this repo.                                                                            */
    public Task<DimensionResult<CoverageGapsControlTotals, CoverageGapsRow>> GetCoverageGapsAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync<CoverageGapsControlTotals, CoverageGapsRow>(
            "CoverageGaps", "dbo.usp_Insights_Dimension_CoverageGaps",
            scopeDeniedCode: CoverageGapsErrorBase,
            reconciliationCodes: [CoverageGapsErrorBase + 1, CoverageGapsErrorBase + 2],
            dictionaryGapCodes: [],
            userId, customerId, new { UserID = userId, CustomerID = customerId, AsOf = asOf }, null, cancellationToken);
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~GetCoverageGapsAsync_ReturnsWellFormedResult"`
Expected: PASS for all 5 `ValidatedTenants` entries (requires a reachable `ConnectionStrings__RegTrack` — same env var every other test in this file already depends on).

- [ ] **Step 6: Wire the unconditional fetch into FetchDimensionsActivity**

In `FetchDimensionsActivity.cs`, add after the `ForwardRisk` line (274):

```csharp
        await TryFetchAsync("CoverageGaps", () => dimensionRepository.GetCoverageGapsAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None));
```

- [ ] **Step 7: Run the full test suite to confirm nothing else broke**

Run: `dotnet test`
Expected: All tests pass (existing + the new one).

- [ ] **Step 8: Commit**

```bash
git add src/RegtrackInsights/Insights.Data/IDimensionRepository.cs src/RegtrackInsights/Insights.Data/SqlDimensionRepository.cs src/RegtrackInsights/Insights.Worker/Orchestration/Activities/FetchDimensionsActivity.cs tests/Insights.IntegrationTests/DimensionRepositoryTests.cs
git commit -m "Wire CoverageGaps into the data-fetch layer (GetCoverageGapsAsync, unconditional fetch)"
```

---

## Task 3: FreehandDimensions.Names + registration scaffolding (prompts referenced, not yet written)

**Files:**
- Modify: `src/RegtrackInsights/Insights.Domain/FreehandDimensions.cs`
- Modify: `src/RegtrackInsights/Insights.Worker/Orchestration/PaidReportAgentsRegistration.cs` (two dictionaries)

**Interfaces:**
- Consumes: nothing new.
- Produces: `FreehandDimensions.Names` containing all 5 new names; both registration dictionaries containing entries that reference prompt files Tasks 4-8 will create. **This task runs before the prompt files exist — registration will fail to start the app until Task 4-8's files land. That's acceptable: Tasks 4-8 each land their own dimension's prompt pair immediately after, and the whole plan's final build/test task (Task 9) is the real gate.**

- [ ] **Step 1: Add the 5 names**

In `FreehandDimensions.cs`, replace the `Names` line:

```csharp
    public static readonly IReadOnlySet<string> Names = new HashSet<string>([
        "Act", "BacklogAging", "Departments", "Licence", "Location", "Risk", "Nature", "Internal", "Event", "Users",
        "TimelinessFY", "ForwardPipeline", "EvidenceIntegrity", "ForwardRisk", "CoverageGaps",
    ]);
```

- [ ] **Step 2: Add the 5 composition-agent dictionary entries**

In `PaidReportAgentsRegistration.cs`, inside the `IReadOnlyDictionary<string, IFreehandDimensionCompositionAgent>` factory (~line 221, right after `["Users"] = Build(...)`), add:

```csharp
                ["TimelinessFY"] = Build("TimelinessFY", "02_composition_freehand_timelinessfy.md"),
                ["ForwardPipeline"] = Build("ForwardPipeline", "02_composition_freehand_forwardpipeline.md"),
                ["EvidenceIntegrity"] = Build("EvidenceIntegrity", "02_composition_freehand_evidenceintegrity.md"),
                ["ForwardRisk"] = Build("ForwardRisk", "02_composition_freehand_forwardrisk.md"),
                ["CoverageGaps"] = Build("CoverageGaps", "02_composition_freehand_coveragegaps.md"),
```

- [ ] **Step 3: Add the 5 render-agent dictionary entries**

In the same file, inside the `IReadOnlyDictionary<string, IReportHtmlAgent>` factory (~line 438, right after the `["dimension_selection:Event"]` entry), add:

```csharp
                ["dimension_selection:TimelinessFY"] = Build(
                    "DimensionSelectionTimelinessFYReportHtmlAgent", "Renders a freehand-composed TimelinessFY insight as self-contained HTML.",
                    "05_report_html_dimension_selection_timelinessfy.md", freehandModel),
                ["dimension_selection:ForwardPipeline"] = Build(
                    "DimensionSelectionForwardPipelineReportHtmlAgent", "Renders a freehand-composed ForwardPipeline insight as self-contained HTML.",
                    "05_report_html_dimension_selection_forwardpipeline.md", freehandModel),
                ["dimension_selection:EvidenceIntegrity"] = Build(
                    "DimensionSelectionEvidenceIntegrityReportHtmlAgent", "Renders a freehand-composed EvidenceIntegrity insight as self-contained HTML.",
                    "05_report_html_dimension_selection_evidenceintegrity.md", freehandModel),
                ["dimension_selection:ForwardRisk"] = Build(
                    "DimensionSelectionForwardRiskReportHtmlAgent", "Renders a freehand-composed ForwardRisk insight as self-contained HTML.",
                    "05_report_html_dimension_selection_forwardrisk.md", freehandModel),
                ["dimension_selection:CoverageGaps"] = Build(
                    "DimensionSelectionCoverageGapsReportHtmlAgent", "Renders a freehand-composed CoverageGaps insight as self-contained HTML.",
                    "05_report_html_dimension_selection_coveragegaps.md", freehandModel),
```

- [ ] **Step 4: Commit (even though the app won't start yet — next 5 tasks fix that)**

```bash
git add src/RegtrackInsights/Insights.Domain/FreehandDimensions.cs src/RegtrackInsights/Insights.Worker/Orchestration/PaidReportAgentsRegistration.cs
git commit -m "Register 5 new freehand dimensions (prompt files land in following tasks)"
```

---

## Task 4: ForwardRisk prompt pair (pattern: Location, per-branch grain)

**Files:**
- Create: `src/RegtrackInsights/prompts/02_composition_freehand_forwardrisk.md`
- Create: `src/RegtrackInsights/prompts/05_report_html_dimension_selection_forwardrisk.md`

**Interfaces:**
- Consumes: `ForwardRiskControlTotals`/`ForwardRiskRow` field names (DimensionShapes.cs:727-762, already quoted in full in the Global Constraints / spec section 2.1 table).
- Produces: the two prompt files registration (Task 3) already references.

- [ ] **Step 1: Read the pattern files in full**

Read `src/RegtrackInsights/prompts/02_composition_freehand_location_v5.md` and
`src/RegtrackInsights/prompts/05_report_html_dimension_selection_location_v7.md` end to end —
these are the most-current Location freehand pair (per-branch grain, same as ForwardRisk).

- [ ] **Step 2: Write the composition prompt**

Write `02_composition_freehand_forwardrisk.md`, adapting Location's structure (same freehand
contract: decide section count/order/hero from real data, no fixed template) to ForwardRisk's own
real fields — the 90-day-window carried-forward/clean-at-risk/healthy segmentation
(`ForwardRiskControlTotals.CarriedForward`/`CleanAtRisk`/`Healthy`/`DueInWindow`), per-branch rows
with `CleanAtRiskRank`/`Flags`, the `BranchStressThresholdPct`/`TenantMedianBranchOverduePct`
comparators. Ground every instruction in these exact field names — copy Location's section
headers/structure (hero selection rules, block emphasis guidance, "How to read this chart"
requirement, data-quality grounding) but replace every data reference with ForwardRisk's own.

- [ ] **Step 3: Write the render prompt**

Write `05_report_html_dimension_selection_forwardrisk.md`, adapting Location's render prompt
(shared theme/CSS contract, chart craft rules, percentage hover-link mechanism per section 7b) to
ForwardRisk's field names.

- [ ] **Step 4: Commit**

```bash
git add src/RegtrackInsights/prompts/02_composition_freehand_forwardrisk.md src/RegtrackInsights/prompts/05_report_html_dimension_selection_forwardrisk.md
git commit -m "Add ForwardRisk freehand composition + render prompts"
```

---

## Task 5: CoverageGaps prompt pair (pattern: Location, per-branch grain)

**Files:**
- Create: `src/RegtrackInsights/prompts/02_composition_freehand_coveragegaps.md`
- Create: `src/RegtrackInsights/prompts/05_report_html_dimension_selection_coveragegaps.md`

**Interfaces:**
- Consumes: `CoverageGapsControlTotals`/`CoverageGapsRow` from Task 1.
- Produces: the two prompt files registration (Task 3) already references.

- [ ] **Step 1: Read the pattern files in full**

Same two Location files as Task 4, step 1 (already read if executing in order — re-read if this
task runs independently).

- [ ] **Step 2: Write the composition prompt**

Write `02_composition_freehand_coveragegaps.md`, grounded in: peer-comparison framing (`PeerSetSize`,
`PeerMedianObligations`, `PctOfPeerMedian`), the full/reduced-confidence gap split
(`GapsFullConfidence`/`GapsReducedConfidence`), `UnderConfigured` branches, and the proc's own
"review candidates, not violations" framing (`Method` field literal text, and the assertion caveat
language already in `sql/27`: "no applicability-rules table exists; a genuine exemption can explain
any single gap" — the composition/render prompts MUST preserve this caveat, never state a gap as a
confirmed compliance failure). `PeerMedianObligations`/`PctOfPeerMedian`/`GapRank` are nullable —
explicitly instruct: branches with `InPeerSet = false` have no peer comparison and must be
presented as "not comparable" or excluded from ranked views, never shown as a zero/blank value.

- [ ] **Step 3: Write the render prompt**

Write `05_report_html_dimension_selection_coveragegaps.md`, same shared theme/CSS/hover-link
contract as Task 4's ForwardRisk render prompt, CoverageGaps' own field names.

- [ ] **Step 4: Commit**

```bash
git add src/RegtrackInsights/prompts/02_composition_freehand_coveragegaps.md src/RegtrackInsights/prompts/05_report_html_dimension_selection_coveragegaps.md
git commit -m "Add CoverageGaps freehand composition + render prompts"
```

---

## Task 6: TimelinessFY prompt pair (pattern: BacklogAging, small bucket count)

**Files:**
- Create: `src/RegtrackInsights/prompts/02_composition_freehand_timelinessfy.md`
- Create: `src/RegtrackInsights/prompts/05_report_html_dimension_selection_timelinessfy.md`

**Interfaces:**
- Consumes: `TimelinessFYControlTotals`/`TimelinessFYRow` — read these exact field names from
  `src/RegtrackInsights/Insights.Domain/DimensionShapes.cs` (search `TimelinessFY`) before writing
  either prompt; the spec's section 1 confirms the row grain is 2 rows (current FY / previous FY)
  but does not enumerate every field — read the real DTO, don't guess.
- Produces: the two prompt files registration (Task 3) already references.

- [ ] **Step 1: Read the pattern files and the real DTO**

Read `src/RegtrackInsights/prompts/02_composition_freehand_backlogaging_v4.md` and
`src/RegtrackInsights/prompts/05_report_html_dimension_selection_backlogaging_v6.md` end to end.
Then read `TimelinessFYControlTotals`/`TimelinessFYRow` in `DimensionShapes.cs` end to end.

- [ ] **Step 2: Write the composition prompt**

Write `02_composition_freehand_timelinessfy.md`, adapting BacklogAging's structure (small,
countable bucket set; trend-over-time framing between the two FY buckets) to TimelinessFY's own
real fields from the DTO read in Step 1.

- [ ] **Step 3: Write the render prompt**

Write `05_report_html_dimension_selection_timelinessfy.md`, same shared contract as BacklogAging's
render prompt, TimelinessFY's own fields.

- [ ] **Step 4: Commit**

```bash
git add src/RegtrackInsights/prompts/02_composition_freehand_timelinessfy.md src/RegtrackInsights/prompts/05_report_html_dimension_selection_timelinessfy.md
git commit -m "Add TimelinessFY freehand composition + render prompts"
```

---

## Task 7: ForwardPipeline prompt pair (pattern: BacklogAging, day-window buckets)

**Files:**
- Create: `src/RegtrackInsights/prompts/02_composition_freehand_forwardpipeline.md`
- Create: `src/RegtrackInsights/prompts/05_report_html_dimension_selection_forwardpipeline.md`

**Interfaces:**
- Consumes: `ForwardPipelineControlTotals`/`ForwardPipelineRow` — read the exact field names from
  `DimensionShapes.cs` (search `ForwardPipeline`) before writing either prompt.
- Produces: the two prompt files registration (Task 3) already references.

- [ ] **Step 1: Read the pattern files and the real DTO**

Same two BacklogAging files as Task 6, step 1 (re-read if this task runs independently). Then read
`ForwardPipelineControlTotals`/`ForwardPipelineRow` in `DimensionShapes.cs` end to end.

- [ ] **Step 2: Write the composition prompt**

Write `02_composition_freehand_forwardpipeline.md`, adapting BacklogAging's bucket-framing to
ForwardPipeline's day-window buckets (due in next 30/60/90 days etc, per the spec's section 1).
Per Review Focus: handle the all-zero/nothing-due case gracefully, same pattern Risk's own prompt
already uses for a zero-scheduled-work tenant — read
`src/RegtrackInsights/prompts/02_composition_freehand_risk_v4.md`'s own empty-population handling
before writing this section.

- [ ] **Step 3: Write the render prompt**

Write `05_report_html_dimension_selection_forwardpipeline.md`, same shared contract, ForwardPipeline's fields.

- [ ] **Step 4: Commit**

```bash
git add src/RegtrackInsights/prompts/02_composition_freehand_forwardpipeline.md src/RegtrackInsights/prompts/05_report_html_dimension_selection_forwardpipeline.md
git commit -m "Add ForwardPipeline freehand composition + render prompts"
```

---

## Task 8: EvidenceIntegrity prompt pair (pattern: BacklogAging, trail buckets)

**Files:**
- Create: `src/RegtrackInsights/prompts/02_composition_freehand_evidenceintegrity.md`
- Create: `src/RegtrackInsights/prompts/05_report_html_dimension_selection_evidenceintegrity.md`

**Interfaces:**
- Consumes: `EvidenceIntegrityControlTotals`/`EvidenceIntegrityRow` — read the exact field names
  from `DimensionShapes.cs` (search `EvidenceIntegrity`) before writing either prompt.
- Produces: the two prompt files registration (Task 3) already references.

- [ ] **Step 1: Read the pattern files and the real DTO**

Same two BacklogAging files as Task 6, step 1 (re-read if this task runs independently). Then read
`EvidenceIntegrityControlTotals`/`EvidenceIntegrityRow` in `DimensionShapes.cs` end to end.

- [ ] **Step 2: Write the composition prompt**

Write `02_composition_freehand_evidenceintegrity.md`, adapting BacklogAging's small-bucket framing
to EvidenceIntegrity's `has_trail`/`single_row_only` bucket split (per the spec's section 1).

- [ ] **Step 3: Write the render prompt**

Write `05_report_html_dimension_selection_evidenceintegrity.md`, same shared contract, EvidenceIntegrity's fields.

- [ ] **Step 4: Commit**

```bash
git add src/RegtrackInsights/prompts/02_composition_freehand_evidenceintegrity.md src/RegtrackInsights/prompts/05_report_html_dimension_selection_evidenceintegrity.md
git commit -m "Add EvidenceIntegrity freehand composition + render prompts"
```

---

## Task 9: Full build/test + live end-to-end proof

**Files:** none created/modified — verification only.

**Interfaces:**
- Consumes: everything from Tasks 1-8.
- Produces: nothing further downstream — this is the plan's final gate.

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors, 0 new warnings beyond the pre-existing baseline.

- [ ] **Step 2: Full test suite**

Run: `dotnet test`
Expected: All tests pass, including the new `GetCoverageGapsAsync_ReturnsWellFormedResult` theory
across all 5 `ValidatedTenants`.

- [ ] **Step 3: Confirm the app starts (registration dictionaries resolve)**

Start the worker locally (or in whichever environment is being used for proof) and confirm no
`InvalidOperationException` at startup from `PaidReportAgentsRegistration` (a missing/misnamed
prompt file throws here, per `LoadPromptSync`'s `GetAwaiter().GetResult()` — this is a real,
immediate failure mode if any of Tasks 4-8's filenames don't exactly match what Task 3 registered).

- [ ] **Step 4: Real end-to-end report generation, one dimension as proof**

Pick one real tenant with genuine data in scope for the chosen dimension (per Review Focus: prefer
one that will exercise at least one non-empty bucket, not an all-zero tenant, for the first proof).
Fire a real `POST /api/insights/reports` request with `requestedDimensions: ["ForwardRisk"]` (or
whichever of the 5 is chosen first), wait for completion, download and visually inspect the
rendered HTML — confirm it used the dimension-specific render agent (check the composition output
genuinely reflects ForwardRisk's own data: carried-forward/clean-at-risk/healthy segmentation
language, not generic fallback copy) and that the gate pipeline (entitlement → scope → fetch →
compose → narrate → render → publish gate → persist) completed without `UNTRACEABLE_NUMBERS` or
other refusal.

- [ ] **Step 5: Repeat Step 4 for the remaining 4 dimensions**

Same process — one real generation per dimension (TimelinessFY, ForwardPipeline,
EvidenceIntegrity, CoverageGaps), confirming each one's own composition/render prompt fired and
produced real, data-grounded output.

- [ ] **Step 6: Final commit (if any fixes were needed during live verification)**

```bash
git add -A
git commit -m "Fix issues found during live verification of 5 new freehand dimensions"
```

(Skip this commit if Steps 4-5 passed clean with no fixes needed.)

# TimelinessFY/ForwardPipeline/EvidenceIntegrity/ForwardRisk/CoverageGaps — Freehand Design Spec

**Status:** Approved (design), ready for planning
**Scope:** Backend only. Bring these 5 data-source report units up to the same standalone
freehand `dimension_selection` capability the 7 live dimensions already have (Location,
Departments, Act, Entity, Users, BacklogAging, Licence) — real LLM composition
(`ComposeFreehandDimensionActivity`) and a dimension-specific render prompt, instead of
the generic zero-LLM-judgement fallback (`05_report_html_dimension_selection_v2.md`).

## 1. What already exists (verified against real code)

- `InsightsReportOrchestrator.cs:608` routes any dimension in `FreehandDimensions.Names`
  into real LLM composition automatically — no new orchestrator branching needed. Everything
  outside that membership check (`ReportNumberTracer`, `PublishGateActivity`, the render
  pipeline, the 9 `Inject*Activity` panes) is already dimension-agnostic.
- Render-agent lookup (`RenderHtmlActivity`, via the 2026-09-09 per-dimension-template spec)
  already does a two-step, most-specific-first lookup: `"dimension_selection:{Name}"` falling
  back to plain `"dimension_selection"`. Registering a new specific key is purely additive.
- **4 of 5 are already fetched on EVERY report run, unconditionally**
  (`FetchDimensionsActivity.cs:270-274` — `BacklogAging`, `TimelinessFY`, `ForwardPipeline`,
  `EvidenceIntegrity`, `ForwardRisk` all run outside any `IsRequested(...)` guard, because they
  already feed the always-on `Inject*Activity` panes in `fixed_holistic`). Their row/control-totals
  data is already real, already reconciled, already present in `dimensionResults` regardless of
  what the caller asked for.
- **CoverageGaps is the one true gap.** Confirmed: no `GetCoverageGapsAsync` method exists on
  `IDimensionRepository`/`SqlDimensionRepository.cs`, no DTOs exist, no `TryFetchAsync` call
  exists anywhere. `sql/27_dimension_coverage_gaps.sql` (proc `usp_Insights_Dimension_CoverageGaps`)
  is production-quality and fully reconciled (THROW 51201/51202) but nothing in the app calls it.
- **SQL audit (separate pass, already done): zero SQL changes needed for any of the 5.** All
  inherit RegTrack-parity automatically through shared infra (`vInsightsStatusCurrent`,
  `tvfInsightsOverdueSchedules`) rather than hardcoding status logic. ForwardPipeline and
  ForwardRisk already had the distinct-instance/occurrence split from original authoring —
  predates the September v3 effort on the other 7 dimensions. EvidenceIntegrity and CoverageGaps
  don't need the split (single-grain metrics by nature: a closure event; a branch's
  configured-obligation count).

## 2. Design

### 2.1 Four dimensions — prompts + registration only, zero data-layer changes

For `TimelinessFY`, `ForwardPipeline`, `EvidenceIntegrity`, `ForwardRisk`:

1. Add the name to `FreehandDimensions.Names` (`Insights.Domain/FreehandDimensions.cs:24`).
2. Write `02_composition_freehand_{name}.md` (composition prompt — grounded in that proc's own
   real `dimension_rows`/`dimension_control_totals`, same freehand contract as the 10 existing
   prompts: model decides section count/order/hero, never a fixed template).
3. Write `05_report_html_dimension_selection_{name}.md` (render prompt — shared theme/CSS
   contract, creative freedom under it, same as the others).
4. Register both in `PaidReportAgentsRegistration.cs`: one entry in the composition-agent
   dictionary (~line 221, keyed by plain name) and one in the render-agent dictionary (~line 438,
   keyed `"dimension_selection:{Name}"`, using `freehandModel`/`freehandEndpoint` per the
   established pattern every other freehand entry already uses).

That's it — because the data is already being fetched unconditionally, no `FetchDimensionsActivity.cs`
change, no new DTO, no new repository method.

### 2.2 CoverageGaps — needs the data-layer plumbing too

Same 4 steps as above, PLUS, mirroring `GetForwardRiskAsync`'s exact pattern
(`SqlDimensionRepository.cs:284-295`) since both are per-branch grain:

1. `CoverageGapsControlTotals`/`CoverageGapsRow` DTOs in `Insights.Domain` — fields read directly
   off `sql/27`'s real result sets: control_totals has `LeafBranchesInScope`, `PeerSetBranches`,
   `PeerGroupsQualifying`, `PeerGroupsTooSmall`, `NearUniversalObligations`, `Gaps`,
   `SumOfRowGaps`, `Reconciled`, `GapsFullConfidence`, `GapsReducedConfidence`,
   `BranchesWithGaps`, `UnderConfiguredBranches`, `UnknownNodeType`,
   `ThresholdObligationsExcluded`, `CoverageThreshold`, `MinPeers`, `Method`; rows have
   `BranchID`, `BranchName`, `StateID`, `NodeTypeId`, `Class`, `InPeerSet`, `PeerSetSize`,
   `LabourObligations`, `PeerMedianObligations`, `PctOfPeerMedian`, `Gaps`,
   `GapsFullConfidence`, `GapsReducedConfidence`, `UnderConfigured`, `GapRank`, `Flags`.
2. `GetCoverageGapsAsync` on `IDimensionRepository` + `SqlDimensionRepository` — error base
   **51200** (CLAUDE.md's own §5b table already reserves `51200-51209` for sql/27; THROW codes
   in the proc are 51201/51202, both reconciliation — no dictionary-gap code of its own, same as
   ForwardRisk).
3. One new unconditional `TryFetchAsync("CoverageGaps", ...)` line in `FetchDimensionsActivity.cs`,
   same unconditional pattern as `ForwardRisk`/`BacklogAging` (it already feeds the Coverage-grid
   pane the same way, so this isn't a new cost — it's formalizing a fetch that arguably should
   already exist for that pane, currently getting its grid data some other way — out of scope to
   change that existing pane wiring here, just add the dimension's own standalone fetch path).

### 2.3 What explicitly does NOT change

- `InsightsReportOrchestrator.cs` routing logic — already generic.
- `ReportNumberTracer`, `PublishGateActivity`, DOMPurify, the render/sanitize/structure/vision
  pipeline — all already dimension-agnostic.
- The existing `Inject*Activity` panes inside `fixed_holistic` — untouched, keep working exactly
  as today regardless of this change.
- No SQL files change (per the audit in section 1).

## 3. Prompt-authoring plan (pattern-matched by grain)

| New dimension | Row grain | Pattern off |
|---|---|---|
| ForwardRisk | per-branch | Location (same grain, mature freehand prompt pair) |
| CoverageGaps | per-branch | Location |
| TimelinessFY | 2 rows (current/previous FY) | BacklogAging (small bucket count, trend-shaped) |
| ForwardPipeline | per day-window bucket | BacklogAging |
| EvidenceIntegrity | per trail-bucket (has_trail/single_row_only) | BacklogAging |

Each new prompt pair follows the same structural contract already proven across the 10 existing
freehand dimensions (see any current `02_composition_freehand_*.md`/`05_report_html_dimension_
selection_*.md` for the shared conventions: real-data-only grounding, "How to read this chart"
panels, the shared theme/CSS contract, no vague umbrella labels, say each fact once).

## 4. Testing

- `dotnet build` + `dotnet test` after the DTO/repository/registration changes (CoverageGaps path).
- Per CLAUDE.md's own testing discipline — never validate a new dimension on one tenant. At
  minimum 2 real tenants per new dimension before calling any of the 5 done.
- Real end-to-end report generation for each of the 5 against a real tenant (prod or UAT), proving
  the full pipeline (gate → scope → fetch → freehand compose → narrate → render → publish gate →
  persist) end to end — the explicit deliverable the user asked for.

## 5. Error handling

Unchanged from the established pattern: `ExecuteAsync<TTotals,TRow>`'s existing THROW-to-typed-
exception translation handles CoverageGaps' 51200/51201/51202 the same way every other dimension's
codes are handled — no new error-handling code, just new constants passed into the existing
overload.

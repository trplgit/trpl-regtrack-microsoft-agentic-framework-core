# Period scoping for dimension procs - design

**[REWRITTEN 2026-09-25]** This spec's original 2026-09-24 version recommended a dual-mode
additive design (new `*InWindow`/`*InPriorWindow` fields alongside untouched cumulative fields,
with a duration-shifted prior-period comparator) for Event and Licence only, and explicitly kept
Location/Entity/Risk/Nature/Departments/Act/Users/Internal cumulative. **Both calls are reversed by
explicit user instruction the same day, after Act shipped live and proved a simpler pattern
works better.** This version documents what is actually being built. The original per-dimension
read-through and schema grounding below is unchanged and still accurate; the decision section is
rewritten.

## Goal (unchanged)

Today most dimension procs are a pure snapshot of right-now. A tenant running the same report
monthly sees almost the same numbers every time - a few weeks of new activity is a rounding error
against years of accumulated history. Recurring reports feel static and hide "what changed since
last time." Also: `sql/23`/`sql/25`'s existing window support was never reachable from the real
product - the public API's `period` field never resolved to a concrete date range.

## Real grounding - what every dimension proc did as of 2026-09-24

Read in full (`sql/05,07,08,09,10,11,12,13,14,21,22,23,24,25,26,27`):

| Proc | Window param (pre-2026-09-25) | Real date columns | Character |
|---|---|---|---|
| 05 Location | `@AsOf` only | none | stock |
| 07 Entity | same | none | stock |
| 08 Risk | same | none | stock |
| 09 Nature | same | none | stock |
| 10 Departments | same | none | stock |
| 11 Act | same | `Act.StartDate` (law-recency proxy only, never windowed) | stock |
| 12 Users | same | `MedianDaysEarlyLate` computed all-time | stock (all-time timing quality) |
| 13 Internal | same | none | stock |
| 14 Event | `@AsOf` + `@DormancyMonths` (12mo rolling) | `EarliestStart`/`LatestStart` | partially-windowed stock |
| 21 Licence | `@AsOf` only | `StartDate`/`EndDate` | stock (current lapse status only) |
| 22 BacklogAging | `@AsOf` only | - | already bucketed `current_fy`/`previous_fy`/`older` in ONE call |
| 23 TimelinessFY | **`@WindowStart`/`@WindowEnd` REQUIRED**, THROWs 51177 on NULL | - | flow, dual-mode, YoY comparator, **live in production** |
| 24 ForwardPipeline | `@AsOf` only | fixed 90-day forward histogram | forward-looking, different axis |
| 25 EvidenceIntegrity | **`@WindowStart`/`@WindowEnd` REQUIRED**, THROWs 51178 on NULL | closure date | flow, windowed-only, **live in production** |
| 26 ForwardRisk | `@AsOf` + `@HorizonDays` | - | forward-looking segment count, not a past-period question |
| 27 CoverageGaps | `@AsOf` only | - | **no C# wrapper at all** - SQL-only, never called from product code |

The real per-occurrence due-date column used for all new window work is `ComplianceScheduleOn.ScheduleOn`
(one row per recurring occurrence) - confirmed live via sql/23's own comment and re-proven by Act's
2026-09-25 implementation. `Act.StartDate` is the law's own enactment date, explicitly wrong for
scoping a population (would near-empty the dimension since most laws are old).

## Decision: which dimensions get windowed, and how (2026-09-25, supersedes 2026-09-24)

Direct user instruction, verbatim intent: give the user a real period picker (last 30/60/90 days,
or a fiscal-year quarter) and **hard-cut** every non-forward-looking, non-already-windowed
dimension to it - "no overlap just hardcut no other data." This is a population filter, not an
additive comparator field. A user picking "last 30 days" should see ONLY obligations with a real
scheduled occurrence in that window - nothing cumulative sneaking back in, and no prior-period
delta bolted on for this batch.

**Stay cumulative, no window param at all** (explicitly named by the user as the files that keep
feeding all data with no scoped gate):
- **BacklogAging** (`sql/22`) - its rows are `current_fy`/`previous_fy`/`older` buckets computed in
  one call; it already answers "how old is this backlog" across multiple periods simultaneously.
  Hard-cutting it to one picker window would destroy the exact distribution view that makes it
  useful.
- **TimelinessFY** (`sql/23`) - keeps its own existing, already-live `@WindowStart`/`@WindowEnd` +
  YoY-comparator design unchanged. This is the ONE dimension where "FY" framing is correct, per the
  user's own explicit clarification ("i only told fy for timeliness fy not to any dimns"). Not
  touched by this batch.
- **ForwardPipeline** (`sql/24`), **ForwardRisk** (`sql/26`) - forward-looking (due dates ahead of
  `@AsOf`), answering a different question than "what happened in a past/selected period." Not a
  fit for a backward-looking picker window.

**Get the hard `@WindowStart`/`@WindowEnd` gate** (required params, THROW on NULL, population
narrowed via the proven `#active`/`DELETE ... WHERE NOT EXISTS` pattern - see "The proven pattern"
below):
- **Act** (`sql/11`) - **DONE, deployed to UAT, live-tested 2026-09-25** (31 real scoped
  instances, reconciled).
- **Event** (`sql/14`) - **DONE, deployed to UAT, live-tested 2026-09-25.** Supersedes this spec's
  original dual-mode/`@DormancyMonths`-extension design entirely - Event now uses the same hard-cut
  population filter as Act, not an additive comparator field.
- **Location** (`sql/05`), **Entity** (`sql/07`), **Risk** (`sql/08`), **Nature** (`sql/09`),
  **Departments** (`sql/10`), **Users** (`sql/12`), **Internal** (`sql/13`) - **in progress
  2026-09-25**, same pattern. This reverses the 2026-09-24 draft's "stay cumulative" call for these
  seven specifically, per direct instruction: "the window @WindowStart/@WindowEnd at all dimension
  (Location/Entity/Risk/Nature/Departments/Users/Internal)."

**Separate track, not part of this hard-cut batch:**
- **Licence** (`sql/21`) - status intentionally left on the original 2026-09-24 additive-field
  design (`LapsedInWindow` as a new, additional metric - licences that lapsed DURING the window -
  alongside the untouched cumulative `TotalLicences`/`ActiveLicences`/`Lapsed`/`LapsedPct` fields).
  A hard population cut does not fit Licence the way it fits the other nine: "which licences exist"
  is not a question a picker window should filter down, since a licence's cumulative standing
  matters regardless of when it happens to lapse. Confirm this reading before implementing - the
  user's own instruction named the other seven explicitly and did not repeat Licence, but also did
  not explicitly re-confirm the additive design after the hard-cut instruction landed.
- **CoverageGaps** (`sql/27`) - not called from any C# code. Unrelated, separate work.

## The proven pattern (from Act, re-verified on Event, being applied to the remaining 7)

1. `@WindowStart DATETIME, @WindowEnd DATETIME` added as REQUIRED params (no default), positioned
   after `@CustomerID`.
2. Immediately after the proc's own base scoped population is materialized into a temp table and
   indexed (every dimension proc already does this as its first step):
   - `THROW` if either param is NULL, and a second `THROW` if `@WindowEnd <= @WindowStart`. One
     error code per dimension's own allocated block (CLAUDE.md Sec.5b) covers both checks.
   - Build `#active`: `SELECT DISTINCT cso.ComplianceInstanceID INTO #active FROM <base pop> x JOIN
     ComplianceScheduleOn cso ON cso.ComplianceInstanceID = x.ComplianceInstanceID WHERE
     cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd`, clustered-indexed.
   - `DELETE x FROM <base pop> x WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE
     a.ComplianceInstanceID = x.ComplianceInstanceID)`.
   - Every downstream step already reads from the same base-population temp table, so reconciliation
     ties out automatically with zero other code changes - proven on Act (31/31 reconciled) and
     Event (0, plausible, reconciled) live on UAT.
3. Add one `data_quality` entry, `Issue = 'window'`, with a real `Detail` string containing the
   actual scoped date range (`CONVERT(VARCHAR(10), @WindowStart, 23)` to `...@WindowEnd...`). This
   entry is **near-mandatory to surface** in the narrative layer - see the prompt-side fix below.
   It exists on Act and Event today; the remaining 7 need the same entry once they get windowed.
4. No prior-period comparator, no YoY shift, no additive field. This batch is a pure population
   filter - "no overlap just hardcut no other data" per the user's own instruction. This is a real,
   deliberate departure from `TimelinessFY`'s YoY-comparator design and from this spec's original
   dual-mode-additive design for Event - both of those remain correct for the dimensions that still
   use them (TimelinessFY itself; Licence, on its separate track).

### A prompt-layer trap already found and fixed (Act/Event only so far)

The freehand composition/render prompts (`02_composition_freehand_*.md`,
`05_report_html_dimension_selection_*.md`) hardcode a closed list of known `data_quality` issue
keys per dimension. The new `window` key wasn't in that list for Event, so the render agent fell
back to generic filler ("the supplied window data-quality flag applies...") instead of using the
real `detail` text - found live on a 2026-09-25 UAT test. Fixed for Act/Event via versioned `_v2`
prompt copies (commit `53eba4c`) - **the same fix is needed for each of the 7 remaining
dimensions' own composition/render prompts once their SQL/C# work lands**, not yet done.

## Non-negotiables carried over (CLAUDE.md)

- **#3 - reconciled in the same instant:** unchanged - every windowed figure is computed in the
  SAME query as its control totals.
- **#5 - comparatives computed in SQL, never phrased by the LLM:** N/A for this batch (no
  comparator exists), still applies to TimelinessFY's YoY figure and to Licence's planned
  `VsPriorWindowPP`.
- **CLAUDE.md Sec.4 (detector emission policy)** and the zero/empty/single-member boundary rules
  apply unchanged - a hard-cut window can produce a genuinely empty or single-member population for
  a real tenant, and that must fail closed / suppress comparatives the same as any other boundary
  case, not fabricate a result.
- **CLAUDE.md Sec.5b (error codes):** each dimension's new window-guard THROW takes one free code
  from its own already-allocated block. Watch the `SqlDimensionRepository` convenience-overload
  classification trap - Act's own `51092` (reconciliation) was found misclassified as
  dictionary-gap by the `errorBase+1`/`+2` convention before this work started, and Event's new
  window code would have collided with the same convention's dictionary-gap slot. Both moved to the
  explicit-overload form with corrected `reconciliationCodes`; check each of the 7 remaining
  dimensions for the same potential collision.

## C# threading (per dimension, mirrors Act/Event exactly)

- `IDimensionRepository.GetXAsync` gains required `DateTime windowStart, DateTime windowEnd`
  (not optional - matches the SQL proc's own required params).
- `SqlDimensionRepository.GetXAsync` passes them through; review/fix the convenience-vs-explicit
  overload classification per dimension (see above).
- `FetchDimensionsActivity.cs`: wrap the dimension's fetch call in `if (IsRequested("X")) { if
  (input is { WindowStart: {} ws, WindowEnd: {} we }) ... else { log + add to failedDimensions } }`
  - fails that ONE dimension loudly when no window resolved, never fabricates one or crashes the
  whole run. Same shape already live for Act/Event.
- The public API -> `ReportPeriodResolver` -> `InsightsReportOrchestrationInput.WindowStart/WindowEnd`
  -> orchestrator -> `FetchDimensionsInput` chain is already fully built and live (commit
  `b8baf11`) - the remaining 7 dimensions consume the exact same chain, no new plumbing needed
  above the repository layer.

## Deferred

- `sql/28` snapshot-based multi-point trending - real future work, unrelated to this batch.
- `CoverageGaps` C# wiring - unrelated, not part of this feature.
- Licence's window design - separate track, see above; needs final confirmation before
  implementation starts.
- The `window` data_quality prompt-layer fix (versioned `_v2` composition/render prompts) for each
  of the 7 remaining dimensions, once their SQL/C# work lands.

## Open questions for review

1. **Licence** - confirm it stays on the original additive-field design (not a hard population cut
   like the other 9), since the user's hard-cut instruction named the other seven explicitly and
   did not repeat Licence by name.
2. Exact error-code slot for each of the 7 remaining dimensions' new window-guard THROW, and
   whether any of them need the convenience-to-explicit overload move in
   `SqlDimensionRepository.cs` - confirm once that work is in progress/complete.

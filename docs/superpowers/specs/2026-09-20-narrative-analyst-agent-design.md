# Narrative Analyst Agent — design

**Status:** proposed, not built. Scope: the 5 freehand dimensions
(Departments, BacklogAging, Act, Licence, Location) only. Entity and Users
keep today's `NarrateActivity`/`ReflectOnNarrativeActivity` unchanged.

## 1. The problem, in one real example

Today's `NarrateActivity` is explicitly told: *"You are a writer, not an
analyst. The analysis is done."* (`prompts/03_narrative.md` line 12). It turns
already-computed Assertions into prose, one sentence per number, and stops
there. It never asks *why* a number is what it is.

Real consequence, already found live (`PROPOSAL_computed_severity.md`,
Minda Corporation Group pilot): the top-ranked finding was a 55-obligation
department with zero imprisonment exposure, while a department with 1,440
overdue obligations and 5,372 imprisonment-bearing items sat fifth. The
*ranking* bug is being fixed separately (Vinay's consequence-ranking
assertions, already live). But even with correct ranking, today's narrative
would still only say "HR & Admin runs 12.5% overdue, 1,440 obligations." It
would not say *who* those 1,440 belong to, whether it's 3 performers or 300,
or whether the same performers show up overdue in every department they
touch. That second sentence — root cause, not just the number — is what's
missing, and it's the actual ask here.

## 2. What "analyst, not stenographer" means, precisely

The new agent gets the same non-negotiables `NarrateActivity` already has —
CLAUDE.md #5 does not change: **every quantitative claim still traces to a
real, verifiable source.** What changes is *what* counts as a real source.

Today: only pre-computed `Assertion`/`Finding` objects.
New: pre-computed `Assertion`/`Finding` objects **plus** the raw per-member
`Rows` already fetched by `FetchDimensionsActivity` for this dimension (see
§4 — this data already exists, it's just not threaded to Narrate today).

This lets the agent write "this branch's overdue load traces to 3 performers,
none of whom appear on any other branch's overdue list" — a real,
row-level cross-reference — without ever running new SQL, resolving scope, or
inventing a number. It's still restating what determinism already computed;
it's just allowed to look at more of what was computed, and connect rows to
each other.

**Explicitly not in scope**: no new tool calls, no live DB access, no new SQL.
Confirmed and agreed in brainstorming — see decision log below.

## 3. Decision log (confirmed with the user before writing this)

| Question | Decision |
|---|---|
| New live data access, or reason over what's already fetched? | **Already-fetched only.** No new tool calls, no new SQL. Reasons over the full per-member `Rows` + `Assertions` + `Findings`, all already computed by `FetchDimensionsActivity`. |
| Which dimensions first? | **The 5 freehand dimensions only** (Departments, BacklogAging, Act, Licence, Location). Entity and Users keep fixed templates and are out of scope — their render contracts likely expect narrative slotted into specific named fields, and haven't been checked against a freer analyst-style output. |
| Fold Reflect into Narrate, or keep as a separate call? | **Fold into one call.** Cheaper, faster; the agent drafts, builds a private rubric, grades itself, revises once, emits final — all in one LLM round-trip, replacing two activities and two calls with one activity and one call. |

## 4. Real plumbing gap this requires closing

Checked directly (`InsightsReportOrchestrator.cs`): `dimensionRowsJson` (the
raw per-member rows, extracted from `dimensions.DimensionResults[name]`'s
`Rows` property) is computed at line 568 — **after** `NarrateActivity` is
scheduled at line 499. It's currently threaded only to `RenderHtmlActivity`
(line 644), never to Narrate.

The extraction itself is pure JSON parsing of an already-fetched activity
output — no I/O, the orchestrator's own existing comment confirms this is
"safe directly in the orchestrator body." Fix: compute `dimensionRowsJson`
(and `dimensionControlTotalsJson`, same shape) **before** the Narrate
schedule call instead of after, and pass both into the new activity's input.
Pure reordering of an existing, already-deterministic computation — no new
determinism risk, no new activity, no new non-negotiable violated.

## 5. New activity: `AnalyzeAndNarrateActivity`

Replaces `NarrateActivity` + `ReflectOnNarrativeActivity` for the 5 freehand
dimensions. One activity, one LLM call per (revision) attempt — same
`MaxReflectionIterations`-bounded retry shape the orchestrator already has,
just now retrying one activity instead of alternating between two.

```csharp
public sealed record AnalyzeAndNarrateInput(
    CompositionPlan Plan,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<Finding> Findings,
    string DimensionName,
    string DimensionRowsJson,          // NEW - raw per-member rows for this dimension
    string? DimensionControlTotalsJson, // NEW - tenant-level aggregates for this dimension
    NarrativeResult? PreviousNarrative,
    IReadOnlyList<NarrativeReflectionIssue>? Issues,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    string? ReqId = null);

public sealed record AnalyzeAndNarrateOutput(NarrativeResult Narrative, long TotalTokens);
```

Orchestrator wiring change (freehand dimensions only — Entity/Users keep the
existing two-activity path):

```csharp
// existing dimensionRowsJson computation moved above this point (see §4)
SetStage(InsightsRunStage.Narrating);
var result = await context.ScheduleTask<AnalyzeAndNarrateOutput>(
    typeof(AnalyzeAndNarrateActivity).Name, "1.0",
    new AnalyzeAndNarrateInput(
        plan, dimensions.Assertions, dimensions.Findings,
        requestedDimension, dimensionRowsJson[requestedDimension],
        dimensionControlTotalsJson?.GetValueOrDefault(requestedDimension),
        null, null, input.Priority, input.ReqId));
ChargeAndCheck(result.TotalTokens);
var narrative = result.Narrative;

// revision loop uses the SAME activity, PreviousNarrative/Issues populated -
// no separate reflection activity, no separate schedule call
```

## 6. Output contract — extended, not replaced

`NarrativeBlockResult` (`Insights.Domain/NarrativeContract.cs`) gains one
field:

```csharp
public sealed record NarrativeBlockResult(
    string Block, string Prose,
    IReadOnlyList<string> AssertionIdsUsed,
    IReadOnlyList<RowRef> RowRefsUsed);   // NEW

public sealed record RowRef(string Dimension, string MemberKey);
```

`RowRefsUsed` declares every row-level fact the prose cites that ISN'T backed
by a pre-existing `Assertion` (e.g. naming a specific performer, a specific
department's raw row). Verified the identical way `AssertionIdsUsed` already
is — **set-membership against the real, already-fetched rows for that
dimension, never text-parsing** — in the existing claim-checker (`PublishGate`
or wherever `AssertionIdsUsed` is currently validated; the new check is a
sibling to that one, same shape, new source array).

This is the one real extension to CLAUDE.md non-negotiable #5's mechanism,
not a violation of it: every claim, number OR row-level cross-reference,
still must trace to something real and checkable, just from a slightly wider
real pool than before.

## 7. Prompt structure — what's kept, what's added

**Kept from `03_narrative.md`, unchanged, because these are load-bearing and
correct today:**
- Hard rules 1-9 (every number in an assertion, every comparative maps to a
  field, no causation/inference/uncomputed severity, caveats travel with
  values, obey narrative_guard literally, delete unsupported sentences,
  never invent a metric's meaning, never blend disagreeing assertions, state
  every number you were given a block for).
- The "refuse to render" escape valve for self-evidently impossible data
  (>100%, subset larger than total).
- Plain-language rules (no jargon or explain once, professional tone, no
  hype words, every number gets "what it counts + where it comes from").
- All worked examples — these stay as the calibration bar for the new
  agent's OWN self-reflection rubric (see below).

**Removed:**
- Line 12: *"You are a writer, not an analyst. The analysis is done."* —
  the one sentence that has to go, since it's the literal opposite of the
  new job.

**Added, using the block vocabulary from the GPT-5 prompting guide
(structured XML sections a reasoning model measurably follows better than
prose — Cursor's own finding, cited in the guide):**

- `<role>` — senior compliance data analyst, writing for a CCO who is smart
  but not technical and not a compliance specialist. Replaces the old
  "you are a writer" framing directly.
- `<background_information>` — the dimension's real shape (grain, what a
  row represents), the metric definitions already in
  `METRIC_CALCULATION_REFERENCE.md` for this dimension, and the residual-field
  convention from CLAUDE.md §4a (never invent what a field counts).
- `<pattern_checklist>` — the explicit, closed list the agent must actively
  check against the row data before writing, not free-associate:
  single point of failure, ownerless (SPOF's inverse), category-defaulter
  (which law/Act, which location/branch), commonality across rows,
  consequence over rate (imprisonment/risk, matching Vinay's live fix),
  prolonged-period defaulters, repeated behaviour pattern for the same
  performer/reviewer across rows, recency-adoption-lag (new laws already
  struggling). Each is a real, defined pattern (not vibes) — the prompt
  states each one's definition so the agent isn't left to invent a taxonomy.
- `<grounding>` (extends hard rule 1) — explicit: numbers AND row-level
  cross-references both need a real source; state before presenting anything
  unsupported that it's illustrative (never actually applicable here, since
  refusal is the existing escape valve — but the guide's phrasing forces the
  prompt to be explicit that this extends to every figure, not just the
  ones hard rule 1 already names).
- `<self_reflection>` — replaces the separate `ReflectOnNarrativeActivity`
  call. Build a private rubric (grounding held, root-cause depth achieved
  where the data supports it, plain language, no jargon, no fabricated
  number, every finding rendered or declined per existing rule) before
  writing, grade the draft against it, revise once, emit final. Never shown
  to the caller — internal to the one call.
- `<escape_hatch>` — when the data does NOT support a root-cause trace (e.g.
  no per-performer field exists for this dimension, or the peer sample is
  too small), say "no root cause traceable from available data" rather than
  inventing one. This is CLAUDE.md #2 (fail closed) applied to the new
  root-cause-tracing capability specifically, not just to individual numbers.
- `<output_contract>` — the same JSON shape as today (`blocks`,
  `findings_declined`), extended with `row_refs_used` per block (see §6).

## 8. Real cost/latency check before this ships

Current measured baseline: 495s / 115,850 tokens for one clean single-dimension
report (established earlier this engagement). Folding two LLM calls into one
saves a round-trip, but the new call reasons over more input (raw rows, not
just assertions) and does its own internal self-reflection pass — net token
cost is not obviously lower. **Before this replaces anything live**: measure
the new agent's real token cost on tenant 29 in the lab (§9) and size a new,
explicit token cap for it (`Agents:AnalystNarrativeTokenCap` or similar),
the same way `InsightJsonTokenCap`/`FreeDigestTokenCap` were each individually
measured and justified — never inherit the old caps by default.

## 9. Temp lab plan — throwaway, removable

New method on the existing `ModelComparisonLabTests` harness (same pattern
already used for prior model/prompt comparisons this engagement):
`CaptureAnalystNarrativeSnapshotAsync`. Real tenant 29, real frozen
dimension data (same gather/fetch steps already working), one of the 5
freehand dimensions at a time.

Runs both paths against the *same* real fetched data and prints both
outputs side by side for manual comparison — does not touch the live
orchestrator or any real customer-facing path:
1. Today's `NarrateActivity` → `ReflectOnNarrativeActivity` (existing, unchanged).
2. New `AnalyzeAndNarrateActivity` prototype (new prompt, new input shape).

Nothing in this step is wired into `InsightsReportOrchestrator` — it's a
standalone test method, deletable in one PR once the comparison is done.

## 10. Acceptance bar before real pipeline integration

Per CLAUDE.md §11 ("never validate on one tenant"): the lab comparison runs
against **at least 3 tenant profiles with different shapes** (recommend 29,
1403, and one small single-branch tenant like 2480 — see CLAUDE.md's own
tenant-profile table), not just 29. For each:
- Every worked-example bar in the kept part of `03_narrative.md` still holds
  (no causation, no uncomputed severity, caveats travel with values).
- At least one genuine root-cause trace appears where the data supports it
  (a real performer/department/branch cross-reference, not just a restated
  number).
- The escape hatch fires cleanly (states "not traceable," doesn't invent)
  on at least one case where the data genuinely doesn't support tracing.
- Token cost is measured and a real cap set (§8) — not inherited by default.

Only after that: hand to `writing-plans` for the real orchestrator wiring
(§4-§6 above) as an implementation plan, executed against a git worktree,
not directly on `staging`.

## 11. Explicitly out of scope for this pass

- Entity and Users dimensions (fixed templates — separate decision later).
- Any new live SQL, tool access, or scope resolution by the agent.
- Changing `PublishGate`'s own reconciliation/claim-checking logic beyond
  adding the `RowRefsUsed` sibling check.
- The still-open token-ceiling TOCTOU race and priority-queue gaps
  (unrelated, flagged separately, not touched here).

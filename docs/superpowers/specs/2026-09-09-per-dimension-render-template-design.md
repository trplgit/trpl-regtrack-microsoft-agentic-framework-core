# Per-Dimension Render Template Selection — Design Spec

**Status:** Approved, ready for implementation
**Scope:** Backend only. The frontend (N calls per selected dimension, dropdown/iframe UI)
lives in `trpl-regtrack-angular-web`, a separate repo — out of scope here.

## 1. Problem

The product goal: a user picks a time window and checks N dimensions in a frontend
checklist; the frontend gets back N separate HTML files, one per dimension, switchable
via a dropdown inside one iframe. As each dimension's design gets reviewed and
finalized (an external, ongoing process — generate a real render, get feedback,
correct, repeat, per dimension), its file should start using that finalized template
instead of the current generic one.

## 2. What already exists (verified against real code, not assumed)

- `ReportType = "dimension_selection"` (`DimensionSelectionComposition`) already accepts
  `RequestedDimensions` and already renders correctly for exactly one requested
  dimension — the render prompt (`05_report_html_dimension_selection.md`) explicitly
  documents "the two approved single-dimension renders" as an already-handled case.
- The API endpoint (`POST /api/insights/reports`), cooldown (keyed per
  `(scope, reportType, effectivePeriod)`, where `effectivePeriod` already folds in the
  requested dimension name via `ReportDimensionKey`), and persistence all already work
  correctly for a single-dimension request today.
- `fixed_holistic` (the whole-tenant 6-tab report) is untouched by this spec — stays
  exactly as it is, a separate, still-useful report type.

**Conclusion:** N files for N dimensions is achieved by the frontend calling the
existing endpoint N times (once per selected dimension, `RequestedDimensions: [one]`
each) — not by any new batch/orchestration mechanism. This spec covers only the one
real backend gap: **which template renders a given single-dimension request.**

## 3. What's missing

Render-agent selection today is keyed on `ReportType` alone — one dictionary entry for
`"dimension_selection"`, used for every dimension regardless of which one was
requested. There is no way to register a different, polished template for a specific
dimension once its design is finalized, without that entry silently applying to every
other not-yet-finalized dimension too.

## 4. Design

### 4.1 Lookup key — additive, no new field, no breaking change

`RenderHtmlActivity` already has `input.Plan.Blocks` (the `CompositionPlan`).
`DimensionSelectionComposition.Build` names each block after its dimension exactly
(`new CompositionBlockPlan(dimensionName, ...)`), so for a single-dimension request,
`input.Plan.Blocks[0].Block` already *is* the dimension name — no new field needed on
`RenderHtmlInput`, same reasoning already used elsewhere in this codebase for avoiding
a value that could drift from the plan's own actual content.

Lookup becomes two-step, most-specific first:

```csharp
var specificKey = input.Plan.Blocks.Count == 1 ? $"{input.ReportType}:{input.Plan.Blocks[0].Block}" : null;

if ((specificKey is null || !htmlAgentsByReportType.TryGetValue(specificKey, out var htmlAgent))
    && !htmlAgentsByReportType.TryGetValue(input.ReportType, out htmlAgent))
{
    throw new InvalidOperationException(
        $"No render agent registered for ReportType '{input.ReportType}'. Registered: {string.Join(", ", htmlAgentsByReportType.Keys)}.");
}
```

When zero dimension-specific keys are registered (today's state), this is byte-identical
to current behavior — every lookup falls through to the existing `input.ReportType` key.
Adding a finalized dimension later is purely additive: drop in its prompt file, register
one more dictionary entry keyed `"dimension_selection:{DimensionName}"` in
`PaidReportAgentsRegistration.cs` — no other code changes, and every dimension without
an entry keeps using the current generic template automatically (the fallback).

### 4.2 Nothing else changes

Multi-dimension-in-one-call (`RequestedDimensions` with 2+ entries) keeps working exactly
as it does today, still using the plain `input.ReportType` key (the `specificKey` branch
only ever activates when `Blocks.Count == 1`) — this spec adds a capability, removes
nothing.

## 5. Error handling

Unchanged from today: an unrecognized `ReportType` (neither a dimension-specific nor a
generic key matches) still throws the same `InvalidOperationException`, fail-closed.

## 6. Testing

- Unit test: exactly one requested dimension, a dimension-specific agent registered for
  it → that agent is used, not the generic one.
- Unit test: exactly one requested dimension, **no** dimension-specific agent registered
  → falls back to the generic `dimension_selection` agent (today's exact behavior,
  regression guard).
- Unit test: 2+ requested dimensions → always uses the generic `input.ReportType` key,
  even if a dimension-specific entry happens to exist for one of them (a specific key is
  only ever considered when `Blocks.Count == 1`).
- Unit test: unrecognized `ReportType` entirely → still throws, unchanged.

## 7. Explicitly out of scope

- The frontend (N calls, dropdown, iframe) — separate repo, separate work.
- Any actual per-dimension finalized prompt file — none exists yet (only Location and
  Users have any proven design, and that proof lives inside `fixed_holistic`'s Coverage/
  Operations tabs, not as a standalone `dimension_selection` template). This spec builds
  the *mechanism* only; real dimension-specific entries get added as design work on each
  dimension actually finishes, one at a time.

# Interactive Tile QA — Design Spec

## Intent

Real generated reports have shipped with real visual defects that the two
existing automated QA checks (`VisionQaActivity`, `PlaywrightQaActivity`)
did not catch: elements bleeding into each other (a bar chart tile seen
live with two colors overlapping incorrectly), and interactive elements
(toggles, hover panels, scrollable registers) that either don't work, or
that break a *different, unrelated* part of the same page when opened — a
cross-component side-effect class neither existing check looks for at all,
because both operate on a single static screenshot of the whole page.

This spec adds a new, additive QA activity that opens the final HTML in a
real browser, tests every interactive element one tile at a time, and
checks — after each interaction — whether anything outside that tile
visibly changed. Findings are fixed with a targeted patch to the existing
HTML, not a full re-render, and re-verified before the report ships.

**Explicitly not in scope:** rewriting or changing the behaviour of
`VisionQaActivity.cs`, `PlaywrightQaActivity.cs`, or
`LayoutCollisionChecker.cs` — those stay exactly as they are. This is a
new, separate step in the same pipeline.

## Where this sits in the real pipeline

`InsightsReportOrchestrator.cs`'s render-retry loop (`maxRenderAttempts =
3`, see that file's own doc comment history) currently runs, per attempt,
in this order: `RenderHtmlActivity` → injectors → `NormalizeActivity` →
`SanitizeActivity` → `NormalizeActivity` →
`ValidateFixedHolisticStructureActivity` → (`VisionQaActivity`, if
`input.RunVisionQa`) → loop exits on success, or retries/refuses on a
caught `OrchestrationRefusedException`.

The new activity runs **after** `ValidateFixedHolisticStructureActivity`
succeeds and **after** `VisionQaActivity` (when enabled) returns clean —
both existing checks are cheaper/faster and should filter out structural
and single-screenshot visual defects first. Runs on every real report,
both `dimension_selection` and `fixed_holistic` — no report-type
exclusion; `section.card` is the universal structural unit across every
render prompt regardless of report type.

This activity's own output does **not** feed the existing
`renderAttempt` retry loop (full re-render). Its finding → fix path is a
separate, nested loop described below.

## 1. `InteractiveTileQaActivity` — what it does

New activity, new file:
`Insights.Worker/Orchestration/Activities/InteractiveTileQaActivity.cs`,
backed by a new class in `Insights.Presentation`,
`InteractiveTileChecker.cs` (same split `LayoutCollisionChecker` /
`ValidateFixedHolisticStructureActivity` already use — the activity is a
thin DTFx wrapper, the real logic is plain C# so it stays independently
unit-testable and callable from the local lab harness, see below).

```csharp
public interface IInteractiveTileChecker
{
    Task<IReadOnlyList<TileFinding>> FindIssuesAsync(string html, CancellationToken cancellationToken = default);
}

public sealed record TileFinding(
    string CardSelector,        // e.g. "section.card:nth-of-type(4)" - stable enough to re-locate the same card after a patch
    string CardTitle,           // the card's own heading text, for a human-readable finding
    string Interaction,         // e.g. "hover on .hr-toggle#hr-load", "click button.detail-button[data-row='12']"
    string TechnicalDescription,// Terra's own diagnosis, written for a render agent to act on - see section 3
    string BeforeScreenshotPath,
    string AfterScreenshotPath);
```

Uses `Microsoft.Playwright` directly (already a project dependency via
`LayoutCollisionChecker`), constructed the same way
(`LayoutCollisionChecker(IBrowser browser)` — reuse the SAME
`IBrowser` instance already registered in DI, do not spin up a second
browser).

**Algorithm:**

1. `page.SetContentAsync(html, WaitUntilState.NetworkIdle)` — same pattern
   `LayoutCollisionChecker` already uses.
2. `page.QuerySelectorAllAsync("section.card")` — every tile.
3. For each card, in DOM order:
   a. Scroll it into view, screenshot it (`clip` to its bounding box).
   b. Find every real interactive element inside it, by the fixed set of
      selectors in section 2.
   c. For each interactive element: full-page screenshot ("before"),
      trigger the interaction, wait for any CSS transition to settle
      (reuse the existing `.hr-panel` transition timing — see
      `02_composition_freehand_*`'s shared CSS, transitions are ~150-250ms
      everywhere, a fixed 400ms wait matches the `LayoutCollisionChecker`
      font-settle wait already in use), full-page screenshot ("after").
   d. Run the two-stage cross-tile check (section 3) on the before/after
      pair, excluding the interacted card's own bounding box from the
      diff region.
   e. Reset the interaction (Escape key, or click the same toggle again)
      before moving to the next element, so each check starts from a
      clean page state.
4. Return every `TileFinding` collected.

**Fails soft, same posture as every other QA activity in this pipeline**:
a Playwright crash, a timeout, or an unexpected page error on ANY single
tile is caught, logged, and treated as "no finding for that tile" — it
never aborts the whole report over tooling flakiness. This activity must
never be the reason a report that would otherwise have shipped clean gets
refused.

## 2. Interactive element selectors (fixed set, not inferred)

Matching exactly what every render prompt's shared CSS already defines —
copy this list verbatim into the implementation, do not try to detect
"anything clickable" generically:

| Selector | What it is | How to trigger | How to reset |
|---|---|---|---|
| `.hr-toggle` (and its `label.hr-i`/`.pf`) | "i" info panels, percentage hover-links | `hover` then `click` (covers both access paths) | `Escape` key |
| `button` (any, inside the card) | detail/filter/search buttons | `click` | re-click if it's a toggle button, otherwise none needed |
| elements with `overflow-y:auto` computed style | scrollable registers/lists | scroll to bottom, then back to top | none needed (scroll position doesn't persist findings) |
| SVG marks with a `<title>` child or `.tip` sibling | chart hover/focus details | `hover` | move mouse away |

## 3. Cross-tile glitch detection — two-stage

**Stage 1 — pixel diff (cheap, deterministic, every interaction).**
Compare the before/after full-page screenshots, masking out the
interacted card's own bounding box (expanded by a small margin to allow
for the panel/tooltip it legitimately opens). Any remaining pixel
difference above a small noise threshold (handles anti-aliasing/font
rendering jitter — same class of tolerance `LayoutCollisionChecker`'s own
`area(...) < 4` guards already use) flags that region's bounding box as
"changed, needs review."

**Stage 2 — Terra review (only on a flagged region).** Crop both
before/after screenshots to the flagged region's bounding box (plus
margin), send both crops through the SAME `IVisionQaAgent`
(`Llm:VisionQa:Endpoint`/`Model`/`ApiKey`, registered in
`PaidReportAgentsRegistration.cs`) `VisionQaActivity` already calls - the
real deployment this team calls "terra" - with a prompt asking
specifically: did this region change in a way
a reader would see as broken (overlapping text, bleeding colors, content
cut off, something disappearing that shouldn't), or is this expected
page content that legitimately updates (e.g. a live chart re-rendering
with the same data, a focus ring appearing)? A "broken" verdict becomes a
`TileFinding`; a "legitimate" verdict is discarded, no finding recorded.

This keeps vision-model calls bounded to regions the cheap pixel check
already flagged — not one call per interaction regardless of outcome.

## 4. Finding → patch → reverify

When `InteractiveTileQaActivity` returns one or more findings, the
orchestrator enters a **separate, bounded patch loop** (own counter,
`maxPatchAttempts = 3`, independent of the existing `maxRenderAttempts`
full-regenerate loop):

1. Build a patch request: the full current HTML plus the finding list
   (card selector, title, interaction, Terra's technical description).
2. New render-agent call, new prompt
   (`prompts/10_patch_render_defect.md` — new file, additive, does not
   touch any existing render prompt), explicitly scoped: "here is the
   current document and exactly what is broken in it; return the same
   document with ONLY the named region(s) changed; do not regenerate
   unrelated sections; preserve every other card byte-for-byte where
   possible."
3. Run the patched HTML back through `NormalizeActivity` →
   `SanitizeActivity` → `InteractiveTileQaActivity` again (not the whole
   render-retry loop — just re-verification of the same finding set, plus
   a fresh full pass in case the patch introduced something new).
4. If clean: proceed to `PersistActivity` as normal.
5. If still broken after `maxPatchAttempts`: per-finding severity decides
   the outcome —
   - **Cosmetic** (a pixel-diff-flagged color/overlap issue that doesn't
     affect any interactive element's function) ships anyway, logged —
     same posture `LAYOUT_OVERLAP`'s "ships on the last attempt" already
     has.
   - **Functional** (a button/toggle/scroll that doesn't do anything when
     triggered) refuses, new reason code `"INTERACTIVE_ELEMENT_BROKEN"`,
     same `OrchestrationRefusedException` mechanism every other refusal
     in this file already uses — not a numbered SQL `THROW` code, this is
     the string-keyed orchestrator-level refusal family
     (`UNTRACEABLE_NUMBERS`, `LAYOUT_OVERLAP`, `VISUAL_DEFECT_DETECTED`,
     `NOT_NORMALIZABLE`, `FIXED_HOLISTIC_STRUCTURE_INVALID` are the
     existing members).

## 5. Testing strategy

**Local lab harness** (new, throwaway-friendly, lives under
`tests/Insights.IntegrationTests/` or a dedicated `tools/` script per
whatever this repo's existing lab-test convention is — match
`FiveNewDimensionsFreehandLabTest.cs`'s own pattern): loads a saved HTML
file from disk (no live orchestration, no real tenant call needed),
constructs `InteractiveTileChecker` directly against the locally-running
`IBrowser` instance, runs `FindIssuesAsync`, prints findings to console.
This is the fast iteration loop while building/debugging the Playwright
interaction logic itself — point it at `Motul-Entity-Insights.html` or
`Motul-Act-Insights.html` (both already on disk from this session,
confirmed to render cleanly) and at a report known to have the real
reported glitch, to confirm the checker actually catches it.

**Real validation**, before this activity is wired into the live
render-retry loop for real customer traffic: run it end-to-end against a
real prod tenant (reuse this session's established pattern — mint a real
JWT, hit the real `/api/insights/reports` endpoint, confirm a real report
generates, confirm `InteractiveTileQaActivity` actually ran via its own
log output / `InsightsAgentReasoningLog` entries, same verification
technique used for every other real-tenant proof this session).

## Review Focus

- A card whose ONLY interactive element is a chart's native
  hover/focus (no `.hr-toggle`, no button) — confirm the selector list in
  section 2 covers bare SVG hover targets, not just explicit toggle
  markup.
- A report with zero `section.card` elements at all (a degraded/partial
  render) — the activity must return zero findings and exit cleanly, not
  throw on an empty NodeList.
- A finding whose patch succeeds on attempt 1 — confirm the loop doesn't
  burn all 3 patch attempts needlessly when the first one already worked.
- Pixel-diff noise from a chart library's OWN legitimate re-animation on
  every page load (not triggered by the user interaction at all) — must
  not get mistaken for a cross-tile glitch; confirm the "before" shot is
  taken only once the page has fully settled (`NetworkIdle` +
  `document.fonts.ready`, same as `LayoutCollisionChecker`), not
  immediately after `SetContentAsync` returns.
- The SAME card is both the interacted tile AND the one a diff flags
  (e.g. a toggle's own panel legitimately grows past the card's original
  bounding box) — confirm the exclusion margin in section 3 accounts for
  panels that are POSITIONED relative to the card but may render outside
  its original box (same `position:fixed` + script-computed `top`/`left`
  pattern the percentage hover-link mechanism already uses).

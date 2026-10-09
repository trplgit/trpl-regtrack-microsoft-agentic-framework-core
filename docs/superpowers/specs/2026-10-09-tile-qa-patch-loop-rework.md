# Tile QA Patch Loop: Bounded, Best-Effort Design

## Status

Accepted 2026-10-09. Supersedes the patch-loop section (Section 4) of the
2026-10-07 interactive-tile-qa design. **Orchestrator version remains 4.8** -
all changes are activity-internal, payload-only, or gated on new trailing-optional
output fields whose null values preserve the 4.8 replay path.

## Context - measured on production tenant 1271, 2026-10-09

A real validation run against that tenant found:

- **0 of 6 single-dimension reports converged.** Every one ran to patch-attempt
  exhaustion (3 attempts) and shipped on the last cycle regardless of outcome.
- **Act spent 4,516 seconds across 4 InteractiveTileQaActivity calls** - one call
  alone burned 3,417 s (57 minutes). Users ran to 526 s (8+ minutes).
- **LLM patch loss:** the whole-page LLM patch call (PatchRenderActivity) round-
  tripped the `#insights-data` script block through a "patch this finding, preserve
  everything else" prompt, and the LLM returned it with `"rows":[]` on Users, Act
  and Licence. Every row-driven chart rendered blank. Act also lost the entire
  `#timeline` container, producing a TypeError on load. All three shipped marked
  complete.
- **Queueing: pods are single-threaded per activity.** Playwright interactions
  (screenshots, waits, DOM queries - all slow) blocked every other work item.
  String-only activities queued for minutes behind Playwright's 4,500+ second
  calls.

## Decisions

### (a) PatchIntegrityGate - deterministic, inside PatchRenderActivity

Validates a patched HTML string BEFORE it leaves the LLM call boundary. Checks:

- Card count in `.section.card` unchanged
- Untouched cards: same element via ordinal + heading fingerprint, identical
- Page-level `<style>`/`<script>`: unchanged content
- Per patched card: one root `.section.card` element, same heading fingerprint,
  same visible text numbers (content unchanged but whitespace may vary),
  no `on*` attributes, no external/JSON scripts, no `<style>` block rewrite
- At most one card-local `<script>` tag

Failure: discard the patch attempt, keep the last known-good HTML, stop the loop.
Never proceed with a card that failed this check.

### (b) Checker cost: bounded Playwright budget

- **Element scope:** one interactive element per kind per card. Sampling (toggle,
  button, scroll container, SVG mark) - not exhaustive enumeration of 4,000 marks
  in a single chart.
- **Visibility rules:** skip hidden elements (css `display:none`, `visibility:hidden`,
  or outside viewport).
- **SVG hover:** a hover that produced no visible change is never a finding.
- **Timeouts:** Playwright default 3 s per interaction, per-element explicit
  20 s cap.
- **Budget:** 180 s per pass with Truncated partial results (stop sampling
  early, keep what you have).
- **Screenshots:** clipped to card bounds +/- 900 px. No full-page screenshot
  writes to disk unless `Presentation:TileQa:ScreenshotDirectory` is set
  (lab/debug only).
- **Vision calls:** max 4 Terra reviews per pass, max 40 cards total.

### (c) Card identity - static ordinal + heading fingerprint

Identity is `TileCards` ordinal + a 40-char fingerprint of the card's heading
element content (trim, lowercase, first 40 chars of text). Shared by the browser
JS and C# code.

Re-verify only patched cards on subsequent InteractiveTileQaActivity runs
(scoped via `InteractiveTileQaInput.OnlyCardOrdinals`). Merge open findings per
card.

### (d) Scoped patching - LLM patch payload reduced

The model receives:

- Only the failing card(s) - max 4 per call, extracted from the full HTML
- Page `<style>` and `<script>` blocks as read-only context
- Size caps: cards 60k chars, styles 40k, scripts 60k

Returns JSON `{cards:[{ordinal,html}], unfixable:[]}`. Returned cards are
validated (PatchIntegrityGate) and spliced back into the full HTML via
`AngleSharp 0.17.1` (pinned to `HtmlSanitizer`'s transitive version). **Page-
level style/script replacement is not allowed in v1.**

Prompt: `prompts/10_patch_render_defect_v2.md`.

### (e) Surfacing - TileQaSummary on PersistInput

A new `TileQaSummary` field on both `PersistInput` and `PersistOutput`. One
structured warning `EventId 5101 TileQaOpenFindings` carrying the ReportId.
Echoed on the orchestration's terminal `Output`. No schema change to
`GeneratedReport`.

### (f) Kill switches - config-driven and defaulted OFF

`Presentation:TileQa:*` bound to `IOptionsMonitor<TileQaOptions>`:

- `Enabled` (default false)
- `PatchEnabled` (default false)
- `BudgetSeconds` (default 180)
- `PerElementSeconds` (default 20)
- `MaxGlitchReviews` (default 4)
- `MaxCardsPerPass` (default 40)
- `PatchCallSeconds` (default 150)
- `ScreenshotDirectory` (default null)

Both OFF on all deployed hosts. UAT (tenant 1285) has both ON for controlled
validation. **Code defaults are the OFF state.**

### (g) Replay and versioning - trailing-optional fields

Every output change is a new trailing-optional field whose null value reproduces
the 4.8 path exactly:

- `InteractiveTileQaOutput`: `+PatchDisabled`, `+PageErrors`, `+Truncated`,
  `+TotalTokens`, `+Disabled`
- `PatchRenderOutput`: `+Outcome`, `+PatchedCardOrdinals`, `+UnpatchableCardOrdinals`,
  `+RejectReason`
- `PersistInput`/`PersistOutput`: `+TileQaSummary`

In-loop budget: charge-then-stop instead of ChargeAndCheck. In-loop
`InjectFont`/`Normalize`/`Sanitize` failures revert and stop instead of failing
the run. Both paths are terminal in the old 4.8 code (no replay risk).

**maxPatchAttempts stays 3. No ScheduleTask was added, moved, or removed.**

## Rollout

**Current state:** prod config is `Presentation:TileQa:Enabled=true,
PatchEnabled=false` (report-only; findings logged but no patch attempts).
UAT tenant 1285 has both ON.

**Criteria before enabling PatchEnabled in prod:**

- At least 20 real runs across at least 6 dimensions and 4 tenants
- **Zero `DATA_BLOCK_LOST` refusals** on any dimension (data-integrity gate)
- No increase in page errors vs baseline
- Tile-QA p95 runtime at most 240 s (bounded Playwright budget)
- At least one third of runs with findings converge (first-attempt patch success)

**Deployment:** use a rolling or Recreate rollout, because old (4.7) and new (4.8)
pods can alternate service episodes. Old 4.7 pods will refuse
`INTERACTIVE_ELEMENT_BROKEN` with patching disabled. Verify in-flight 4.7 count
via `DrainCheck` is zero before deploying.

## Accepted Risks

- **Sampling misses a second glitch.** Only one interactive element per kind per
  card is tested. A card with two broken toggles will flag the first; the second
  will not be sampled.
- **Bleed beyond 900 px undetected.** A pixel diff further than the card's box
  +/- 900 px margin is clipped and ignored. Real findings may live outside that
  window.
- **Card-local scripts block on DOMContentLoaded.** A script tag injected inside
  a card may stall interaction if it blocks on DOMContentLoaded. Production scripts
  use `defer` or `async` to avoid this; a patched card's new script does not.
- **AngleSharp version tied to HtmlSanitizer.** Version 0.17.1 is old and may have
  spec gaps relative to Chromium. Ordinals/fingerprints are verified lab-only.
- **Browser activities occupy the pod's single-activity slot.** Playwright
  screenshot/wait calls block other work. No parallelism per pod.

## Open Verification Items

- **AngleSharp ordinal+fingerprint parity.** Lab: compare AngleSharp's DOM element
  ordering and text extraction against Chromium on real saved 1271/1285 pages.
  Mismatch is safe (the card becomes unpatchable); exact match confirms the intent.
- **Two inferred root causes from pod logs.** (i) Playwright's 30 s timeout firing
  on Act's 4,500+ second run - was it timing out and retrying, or something else?
  (ii) SVG `<title>` finding rate. Did findings match the SVG-title selector in
  the spec, or were they something else?
- **Tile QA is a no-op for Entity.** Fixed Holistic reports have no `.section.card`
  elements (they use the fixed 6-tab template). Verify that InteractiveTileQaActivity
  returns zero findings on a real Entity page and exits cleanly.

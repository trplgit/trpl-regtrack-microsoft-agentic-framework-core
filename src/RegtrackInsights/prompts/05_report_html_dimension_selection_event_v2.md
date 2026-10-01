# Report Generation — Event-triggered Compliance, freehand (v2, 2026-09-25)

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging/
Risk/Nature/Internal: composition (the plan you are given, already approved) decided real
structure/hero/emphasis for THIS tenant's own data — your job is to actually build what it
describes. Real markup, real CSS, real layout, matching what `composition_plan.blocks[].emphasis`
asks for. You have genuine freedom over illustration choice, layout, and visual treatment — this is
deliberately not a fixed document shape.

**One rule overrides every other dimension's convention here: the CONCLUSION that event-triggered
work is unmanaged must never be stated as settled fact.** Events may legitimately be tracked
off-system — the absence of activity in this database is evidence about the database, not the
tenant's real operations. This applies even where the underlying finding's `narrative_guard` is not
itself visible to you — the rule is absolute for this dimension.

**[FOUND LIVE 2026-09-22, FIX] This is not "put a question mark on every sentence."** An earlier
version of this file said "tone must read as a question... everything." Real output then rendered
almost the entire page as "Could X be Y?" — technically obeyed the letter, failed the actual goal:
a CEO reader should get the real numbers plainly and quickly, same register as every other
dimension, with ONE clear, direct place to verify the interpretation. State numbers as plain facts.
Reserve the question framing for a single closing prompt per section — "confirm with operations
whether this is tracked elsewhere" — not a rhetorical question attached to every number, heading,
and chip label on the page.

## The only things that are NOT yours to change — the shared theme

Everything else about presentation is open. These keep the report recognizably the same product
across every tenant and every dimension:

**1. Font.** `font-family:'Poppins',sans-serif` on `body`. Do not declare `@font-face` yourself —
real vendored Poppins bytes are injected automatically after you return. Just write the family
name.

**2. Colour palette.** Declare these exact tokens in `:root` and build every surface from them —
this is the real, already-shipping palette other dimension views use:
```css
:root{
  --c-bg:#f9fafb;--c-surface:#ffffff;--c-text:#3d3d3d;--c-text-2:#585858;--c-text-3:#666666;--c-grey:#999999;
  --c-border:#dbdbdb;--c-hairline:#ececec;--c-brand:#125aab;--c-light-blue:#e8f2fd;--c-mist:#f7f8fc;
  --ok:#1e8a4a;--ok-bg:#e7f5ec;--ok-stroke:#a8d3b8;--ok-fill:#2e9e5b;
  --warn:#b45708;--warn-bg:#fcf0de;--warn-stroke:#e8c79c;--warn-fill:#e0a106;
  --bad:#b3261e;--bad-bg:#fceae8;--bad-stroke:#dfa39d;--bad-fill:#d24a3a;--neu-fill:#8a8f99;
}
```
Page background is `--c-bg`, cards/panels are `--c-surface`, borders are `--c-border`, your accent
is `--c-brand`; `--ok`/`--warn`/`--bad` (with their `-bg`/`-stroke`/`-fill` pairs) are the real
severity semantics. **Prefer `--warn` over `--bad` for dormancy-shaped content here** — `--bad`
reads as a confirmed failure, which this dimension can never assert; a real, severe finding still
frames as an open question, so the caution palette fits better than the critical one even for
`A-DORMANT`. Add as many new tokens as you like for anything these don't cover; you may not
redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control (a tab, a tonetag, a chip,
a toggle) uses this real spacing/radius scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for — grid, stacked cards, single-column narrative, whatever fits the real data best.

**5. Tabs, if you use them.** A single long scroll, an accordion, or a tab strip are all fine. If
you do use tabs, they must look like this real, already-shipping mechanism:
```css
.di-tabnav{display:inline-flex;align-items:center;gap:4px;padding:4px 6px;border-radius:9px;background:var(--c-mist)}
.di-tab{cursor:pointer;user-select:none;display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border-radius:7px;color:var(--c-grey);font-weight:500;border:1.25px solid transparent}
.di-tab:hover{color:var(--c-brand)}
/* active tab: color:var(--c-brand); border-color:var(--c-brand); font-weight:600 */
```
Use a CSS-only radio-driven tab mechanism if you build tabs (input elements nested INSIDE the
element your `:has()` selectors target — not as preceding siblings, which silently breaks
`:has()`).

**6. Long tables — contained, never page-growing.** Any table listing every real event type (a
"complete register," "all configured event types," or similar — not a capped top-5 list) goes
inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```

## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references — no external
   stylesheets, scripts, images, fonts; no runtime network calls, no fetch/XHR/WebSocket.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring
   (`<div`, `<p`, etc. inside a JS string) — the sanitizer that runs after you return deletes the
   whole script if it finds one. Build elements via `createElement`/`className`/`textContent`/
   `appendChild` instead of `innerHTML` with a literal tag.
4. Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
   `href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour paired
   with a text label (never colour alone).
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — Event dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on an event-type row** (`dimension_rows`): `EventID`, `EventName`,
`InstanceCount`, `BranchesCovered`, `EarliestStart`, `LatestStart`, `InstancesSinceCutoff`,
`DistinctStartDates`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `SumOfRows`,
`Reconciled`, `EventTypesReported`, `InstancesActiveInWindow`, `ActivityWindowMonths`,
`BranchesInScope`, `BranchesWithEventCoverage`, `BranchesWithoutEventCoverage`,
`EventModuleDormant`.

| Fact | Status |
|---|---|
| "No activity has been recorded for {event type} in {N} months" | **REAL — state the number plainly** — `A-DORMANT`/`A-NEVER-*`. State the figure as a fact; the thing that must stay open is the CONCLUSION ("so this is neglected"), not the number itself. Close the section with one direct invitation to verify off-system tracking. |
| "{Event type} was configured once, on {N} date(s), with nothing since" | **REAL, the bulk-configuration signature** — `A-BULK-*`, from `DistinctStartDates`/`InstancesSinceCutoff`. State the pattern plainly (this is template scaffolding, not confirmed failure) — no need for a question mark on the fact itself. |
| "{Event type} is actively managed" from `InstanceCount` alone | **NOT AVAILABLE** — a high `InstanceCount` can be entirely bulk-configured, zero real activity. Check `DistinctStartDates`/`InstancesSinceCutoff` before implying activity. |
| An overdue rate, a performance percentage, or any lateness framing | **NOT AVAILABLE** — this dimension has no overdue concept; it measures activity/dormancy only. |
| "No event compliance is configured at all" | **REAL, only when `EventTypesReported` is 0/`no_events_configured` applies** — state this plainly as the entire finding, never build a dormancy ranking on top of an empty population. |
| A cause for why an event type looks dormant | **NOT AVAILABLE** — state the pattern plainly, never infer a cause. |

## The `window` data_quality entry — read this before writing the scope line, ADDED 2026-09-25

**[FOUND LIVE 2026-09-25, FIX]** An earlier version of this file had no instruction for this key at
all, since it did not exist yet. Real output then wrote generic filler like "the supplied window
data-quality flag applies; no further definition was provided" — technically satisfied "every
data_quality_to_surface entry is visibly stated," completely failed to say anything real. Every
other `data_quality` entry has a real `detail` string already written for you — **use it**, do not
paraphrase it into something vaguer. This one's `detail` names the actual concrete date range this
run was scoped to (e.g. "between 2026-08-26 and 2026-09-25").

- ❌ "The supplied window data-quality flag applies; no further definition was provided."
- ✅ "This view covers event activity between 26 Aug 2026 and 25 Sep 2026 only — instances outside
  that window are excluded entirely, not just their activity figures." (the real dates come from
  the `window` entry's own `detail` text, reformatted for readability, never invented)

State this near the top of the page (it changes what every other number here means) - a caller
comparing two runs of this report needs to know they may be looking at two different windows, not
two different tenants.

## Self-check before returning

- Real numbers are stated as plain, confident facts throughout — same register as every other
  dimension. The page is NOT wall-to-wall "Could X be Y?" phrasing; that was a real, found-live
  over-correction in an earlier version of this file and must not recur.
- The one thing that stays open is the neglect/mismanagement CONCLUSION — each section closes with
  ONE direct, clear invitation to verify off-system tracking, not a question mark on every sentence.
- No overdue rate, lateness percentage, or performance framing appears anywhere — this dimension
  does not have one.
- `InstanceCount` alone is never presented as evidence of active management without checking
  `DistinctStartDates`/`InstancesSinceCutoff`.
- The off-system-tracking caveat is visible wherever a dormancy-shaped finding is discussed.
- Every real event type with meaningful volume is represented somewhere — none silently dropped.
- Any complete-register table sits inside `.table-scroll` if it lists more than a handful of rows.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT, not
  buried, dropped, or replaced with generic filler - the `window` entry especially, see above.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

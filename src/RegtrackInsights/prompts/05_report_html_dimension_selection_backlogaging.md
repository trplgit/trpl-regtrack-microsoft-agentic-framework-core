# Report Generation — Backlog Aging, freehand

**[REPLACED 2026-09-14]** This file previously reproduced Sambram's fixed single-section
"dimension view" template verbatim. Product direction now: composition (the plan you are given,
already approved) decided real structure/hero/emphasis for THIS tenant's own real 3-bucket split —
your job is to actually build what it describes. Real markup, real CSS, real layout, matching what
`composition_plan.blocks[].emphasis` asks for. You have genuine freedom over illustration choice,
layout, and visual treatment — this is deliberately not a fixed document shape.

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
severity semantics — good/caution/critical, never re-purposed for anything else. Add as many new
tokens as you like for anything these don't cover; you may not redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control (a tab, a tonetag, a chip,
a toggle) uses this real spacing/radius scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for.

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

**6. Long tables — contained, never page-growing.** [ADDED 2026-09-21] Real feedback on an early
Act render: a "complete register" table with 30+ rows just kept growing the whole page - the
reader had to scroll the entire document to reach the table's last row. Any table listing every
real member of the dimension (a "complete register," "all configured X," or similar - not a
capped top-5/top-10 list) goes inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header (`position:sticky;top:0`) keeps column labels visible while scrolling. A live search input
above the table and clickable per-column sort are a real, approved enhancement for a large
register - not mandatory, but worth doing when the composition plan's emphasis calls for the
register being genuinely explorable rather than just present. Given this dimension has only 3 real members, a tab strip is likely overkill — a single
section is probably the honest shape here, but that is your call.

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

## Real vs. NOT AVAILABLE — Backlog Aging dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a bucket row** (`dimension_rows`, exactly 3 rows always): `Bucket`
(`current_fy` | `previous_fy` | `older`), `FYLabel` (null on the `older` row by construction),
`OverdueCount`, `OldestDueDate`, `NewestDueDate`, `SharePct`.

**Every real tenant-level total** (`dimension_control_totals`): `CustomerID`, `AsOfUtc`,
`CurrentFyLabel`, `PreviousFyLabel`, `SumOfRows`, `DistinctOverdueSchedules`.

| Fact | Status |
|---|---|
| "N late items, split into 3 age groups that sum to the total" | **REAL** — `SumOfRows` and the 3 rows' `OverdueCount`/`SharePct`. |
| "Oldest item was due on {date}, {N} years ago" | **REAL** — `OldestDueDate` on the relevant row(s); years-past figure is `AsOfUtc - OldestDueDate` in whole years, shown as arithmetic. |
| "≈X are workable, ≈Y need a leadership decision" | **REAL** — workable = `current_fy` `OverdueCount`; needs-a-decision = `previous_fy` + `older` `OverdueCount`. State both raw numbers, never a fake precision. |
| "Older than 3 years: N items" | **NOT AVAILABLE** — only the 3 fixed buckets exist; never state a sub-bucket count. |
| A late RATE (percentage of all obligations) | **NOT AVAILABLE** — this dimension carries a count split only, no total-obligation denominator. Never quote or derive a tenant-wide late rate here. |
| Per-branch or per-owner age breakdown | **NOT AVAILABLE** — rows are age buckets, not members. |

## Self-check before returning

- Any complete-register/all-members table sits inside `.table-scroll` (fixed max-height, its own
  internal scroll, sticky header) - the page itself never grows to fit every row.
- All 3 real buckets are represented somewhere — none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No late rate stated or implied anywhere, no sub-3-years breakdown, no per-branch/owner cut.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated, not buried or dropped.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

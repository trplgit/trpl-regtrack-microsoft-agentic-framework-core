# Report Generation — Departments, freehand

**[REPLACED 2026-09-14]** This file previously reproduced Sambram's fixed single-section
"dimension view" template verbatim. Product direction now: composition (the plan you are given,
already approved) decided real structure/hero/emphasis for THIS tenant's own data — your job is to
actually build what it describes. Real markup, real CSS, real layout, matching what
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
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document — a customer
   reading their own report about their own company never sees "Tenant 1008."

## Real vs. NOT AVAILABLE — Departments dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a department row** (`dimension_rows`): `DepartmentID`, `DepartmentName`,
`Instances`, `Overdue`, `OverduePct`, `NoInstanceOwner`, `NoInstanceOwnerPct`,
`ImprisonmentInstances`, `CriticalInstances`, `DistinctUsers`, `BranchesCovered`, `OverdueRank`,
`Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`,
`AssignedInstances`, `UnassignedInstances`, `UnassignedPct`, `DepartmentsReported` (defined),
`DepartmentsWithObligations` (active — dormant = defined minus active), `OverdueInstances`,
`TenantOverduePct`, `TenantNoInstanceOwnerPct`.

| Fact | Status |
|---|---|
| A synthetic "UNASSIGNED" department row with its own overdue/ownerless breakdown | **NOT real** — never invent one. The untagged slice's own overdue rate IS derivable by subtraction (below), but it has no `NoInstanceOwner`/store-count of its own. |
| "{Department} is N% of all tagged obligations" | **REAL** — that row's `Instances` / `AssignedInstances`. |
| Per-department "store reach %" | **NOT AVAILABLE as a %** — `BranchesCovered` is a real raw count only. Show the raw count, or a bar scaled to the largest `BranchesCovered` among real rows. |
| Per-department "top-owner load %" | **NOT AVAILABLE** — `DistinctUsers == 1` is the real, checkable substitute: "this is a single-person department," never a load percentage. |
| Concentration (which accounts anchor multiple departments) | **NOT AVAILABLE** — needs a Users x Departments cross-reference this data does not have. |
| Closure-status breakdown | **NOT AVAILABLE from this dimension** — different grain entirely. |
| Ownership figures | **RegTrack tracks ownership TWO ways** — an instance-level assignment and a per-occurrence performer (99.8% populated). Never present a no-instance-owner figure as "nobody is doing this work" — say "no owner on the obligation itself," and surface `ownership_has_two_mechanisms` wherever an ownerless figure is the emphasis of a section. |

**One legitimate derivation, always shown as arithmetic, never as if it came from its own field:**
the untagged slice's own overdue rate = `(dimension_control_totals.OverdueInstances - SUM(row.Overdue
for every real row)) / dimension_control_totals.UnassignedInstances * 100`, one decimal place,
cited as "derived from the tenant total minus every tagged department's own overdue count."

## Self-check before returning

- Every real department row is represented somewhere on the page — none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No "top-owner load %" and no cross-department "Concentration" section.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated, not buried or dropped.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

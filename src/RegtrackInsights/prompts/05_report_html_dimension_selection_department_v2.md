# Report Generation — Departments, freehand (v2, 2026-09-25)

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
register being genuinely explorable rather than just present.

## Required: plain-English opening summary

**[ADDED 2026-09-20]** Real feedback on an early render: the page led straight into charts and
tables — numbers first, meaning never. A reader who does not already know this platform's
vocabulary (`overdue rate`, `instance-level owner`, `imprisonment exposure`) has nothing to orient
on before the data starts.

The FIRST thing inside `<main>`, before the header eyebrow/title or any hero/chart/table, is a
short plain-English summary:

- 2–4 sentences. No jargon, no acronyms, no platform-specific terms (`overdue`, `ownerless`,
  `instance-level`, etc.) without immediately explaining what they mean in plain words.
- Near-zero raw numbers — a single anchoring figure is fine if it is the one fact the reader most
  needs ("most department obligations aren't tracked back to a department"), but this is NOT where
  the report cites `83.4%`, `3,115`, or any other precise value. Precision belongs in the
  hero/sections below, which this summary sets up, not repeats.
  - Answers, in order: what did we look at, what is the one thing most worth knowing, why does it
    matter. Written the way you would explain the finding out loud to someone who has never opened
    this report before, not the way you would write a section heading.
  - Synthesize across every real narrative block you were given (`narrative.blocks[].prose`) — this
    is the "so what" for the WHOLE report, not a restatement of any one section.

Example shape (illustrative only — write your own from THIS tenant's real findings, never copy
this text): "This report looks at where compliance work is tracked to a specific department. Most
of it currently isn't — so the department comparisons below only describe a small, particular
slice of the real workload, not the whole picture. Within that slice, one department carries
meaningfully more overdue risk than the rest."

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

## The `window` data_quality entry — read this before writing the scope line, ADDED 2026-09-25

**[FOUND LIVE 2026-09-25, FIX]** An earlier version of this file (and the equivalent file for
Act/Event) had no instruction for this key at all, since it did not exist yet. Real output then
wrote generic filler like "the supplied window data-quality flag applies; no further definition was
provided" — technically satisfied "every data_quality_to_surface entry is visibly stated",
completely failed to say anything real. Every other `data_quality` entry has a real `detail` string
already written for you — **use it**, do not paraphrase it into something vaguer. This one's
`detail` names the actual concrete date range this run was scoped to.

- ❌ "The supplied window data-quality flag applies; no further definition was provided."
- ✅ "This view covers obligations with a scheduled occurrence between 26 Aug 2026 and 25 Sep 2026
  only — a department's real counts here reflect only that window, not its all-time obligation
  load." (the real dates come from the `window` entry's own `detail` text, reformatted for
  readability, never invented)

State this near the top of the page (it changes what every other number here means, including
`ScopedInstances` and the unassigned/untagged slice) — a caller comparing two runs needs to know
they may be looking at two different windows, not two different tenants.

## Self-check before returning

- Any complete-register/all-members table sits inside `.table-scroll` (fixed max-height, its own
  internal scroll, sticky header) - the page itself never grows to fit every row.
- The plain-English opening summary is the FIRST thing in `<main>`, 2-4 sentences, near-zero raw
  numbers, no unexplained jargon, synthesizes across every narrative block - not copy-pasted from
  any one section's own prose.
- Every real department row is represented somewhere on the page — none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No "top-owner load %" and no cross-department "Concentration" section.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT, not
  buried, dropped, or replaced with generic filler - the `window` entry especially, see above.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

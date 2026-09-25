# Report Generation — Statutory vs Internal Governance, freehand (v2, 2026-09-25)

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging/
Risk/Nature: composition (the plan you are given, already approved) decided real structure/hero/
emphasis for THIS tenant's own data — your job is to actually build what it describes. Real markup,
real CSS, real layout, matching what `composition_plan.blocks[].emphasis` asks for. You have
genuine freedom over illustration choice, layout, and visual treatment — this is deliberately not a
fixed document shape.

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

**6. Long tables — contained, never page-growing.** Any table listing every real branch (a
"complete register," "all branches," or similar — not a capped top-5 list) goes inside a
fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header (`position:sticky;top:0`) keeps column labels visible while scrolling.

**7. Two populations, one row.** Every branch row carries BOTH a statutory and an internal figure.
Use a paired/side-by-side visual treatment (two columns, two small bars, two chips) so a reader can
see both at a glance per branch — never merge them into one combined number.

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

## Real vs. NOT AVAILABLE — Internal dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a branch row** (`dimension_rows`): `BranchID`, `BranchName`, `ApexName`,
`StatutoryInstances`, `StatutoryOverdue`, `StatutoryNoInstanceOwner`, `InternalInstances`,
`InternalOverdue`, `InternalNoInstanceOwner`, `StatutoryNoInstanceOwnerPct`, `InternalNoInstanceOwnerPct`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `SumOfRows`,
`Reconciled`, `InternalInstances`, `SumOfInternalRows`, `StatutoryOverdueInstances`,
`InternalOverdueInstances`, `StatutoryNoInstanceOwnerPct`, `InternalNoInstanceOwnerPct`,
`BranchesWithStatutory`, `BranchesWithInternal`, `InternalAbsentEntirely`,
`InternalUnmappedStatusRows`.

| Fact | Status |
|---|---|
| "{N} branches carry real statutory exposure with no internal governance configured" | **REAL** — `A-GAP-*`/`A-GAP-AGG`, `BranchesWithStatutory` minus `BranchesWithInternal`. |
| "This tenant has an overall obligation rate of X%" combining statutory and internal | **NOT AVAILABLE** — the two populations are reconciled independently and never blend into one combined rate. |
| "No internal compliance exists for this tenant" | **REAL, only when `InternalAbsentEntirely` is true** — if so, this is the ENTIRE finding for this dimension; do not also build a coverage-gap ranking on top of zero data. |
| "Internal figures cover the same scope as statutory figures" | **NOT AVAILABLE as an unqualified claim** — internal is branch-only scoped (no category axis), genuinely wider than statutory. State this wherever both appear together. |
| A root cause for why a branch/division lacks internal governance | **NOT AVAILABLE** — state the structural pattern, never infer why. |
| "{X}% ownerless" for either population, without the two-mechanisms caveat | **NOT AVAILABLE as a bare figure** — the same ownership-has-two-mechanisms caveat that applies everywhere else applies to `StatutoryNoInstanceOwnerPct`/`InternalNoInstanceOwnerPct` too. |

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
  only — both the statutory and internal figures here reflect only that window, not the tenant's
  all-time governance picture." (the real dates come from the `window` entry's own `detail` text,
  reformatted for readability, never invented)

State this near the top of the page (it changes what every other number here means, including
both `StatutoryNoInstanceOwnerPct` and `InternalNoInstanceOwnerPct`) — a caller comparing two runs needs to know
they may be looking at two different windows, not two different tenants.

## Self-check before returning

- Statutory and internal figures are never summed, averaged, or blended into one combined number
  anywhere on the page.
- If `InternalAbsentEntirely` is true, the page states that plainly as the finding, not as a
  side-note under a fabricated coverage-gap ranking.
- The branch-only-scope caveat for internal figures appears wherever statutory and internal are
  shown together.
- Every real branch with meaningful statutory volume is represented somewhere in the coverage
  picture — none silently dropped.
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

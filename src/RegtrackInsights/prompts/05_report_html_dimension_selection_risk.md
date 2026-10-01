# Report Generation — Risk, freehand

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging:
composition (the plan you are given, already approved) decided real structure/hero/emphasis for
THIS tenant's own data — your job is to actually build what it describes. Real markup, real CSS,
real layout, matching what `composition_plan.blocks[].emphasis` asks for. You have genuine freedom
over illustration choice, layout, and visual treatment — this is deliberately not a fixed document
shape.

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
severity semantics — good/caution/critical, never re-purposed for anything else. **This dimension's
own headline fact can be `--ok`-coloured**: when `A-CRIT`'s direction is `better`, that is real good
news (Critical tier outperforming the tenant average) and should read as such, not be forced into a
warning colour because "Critical" sounds severe. Add as many new tokens as you like for anything
these don't cover; you may not redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control (a tab, a tonetag, a chip,
a toggle) uses this real spacing/radius scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for — grid, stacked cards, single-column narrative, whatever fits the real data best.
With only 4 real rows, a full data-table register is rarely the right shape here — a card or tile
per risk level, sized/ordered by real materiality, usually serves this dimension better than a
scrolling table built for a much larger population.

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

**6. Long tables — contained, never page-growing.** If you do build a table (e.g. a 4-row risk
summary table is fine at full length — this rule matters only if you add a longer per-branch or
per-instance breakdown this dimension doesn't otherwise have), any table listing more than a
handful of real members goes inside a fixed-height container with its OWN internal scroll:
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

## Real vs. NOT AVAILABLE — Risk dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a risk row** (`dimension_rows`, always exactly 4 rows — Critical/High/
Medium/Low, present even at zero obligations): `RiskType` (raw enum — never state this number or
imply it orders severity), `RiskLabel` (the real name — always use this), `Instances`, `Overdue`,
`OverduePct`, `Ownerless`, `ImprisonmentInstances`, `ImprisonmentOverdue`, `BranchesCovered`,
`VsTenantPP`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `SumOfRows`,
`Reconciled`, `OverdueInstances`, `TenantOverduePct`, `RiskLevelsReported`,
`RiskLevelsWithObligations`, `CriticalRiskType`, `ImprisonmentInstances`,
`ImprisonmentOnCriticalPct`.

| Fact | Status |
|---|---|
| "The Critical tier runs {better/worse} than the tenant average" | **REAL, with a computed direction** — `A-CRIT`'s `Direction` field. Never assume "Critical" means "worst-performing" — state whichever way the real number points. |
| "Critical items and imprisonment exposure are basically the same thing here" | **REAL** — `A-IMP-OVERLAP`'s `ImprisonmentOnCriticalPct`. State it ONCE as one fact; never present Critical-tier volume and imprisonment exposure as two separate findings when this assertion is present — see its `narrative_guard`. |
| "The ownership gap is worst in the middle/lower tiers, not Critical" | **REAL, only when `A-OWNGAP-*` assertions are present** — name the specific tier(s) (individual mode) or state the aggregate count (aggregate mode), per whichever the composition plan was given. |
| "Risk level 3 is the most severe" or any severity claim from the raw `RiskType` number | **NOT AVAILABLE as a numeric ordering** — `RiskType` values are 3=Critical, 0=High, 1=Medium, 2=Low, not severity-ordered. Use `RiskLabel` only, never the raw integer. |
| A root cause for why a tier is better/worse-managed | **NOT AVAILABLE** — state the pattern, never infer why. |
| An industry/cross-tenant risk benchmark | **NOT AVAILABLE** — every comparative here is this tenant's own `TenantOverduePct`, nothing external. |

## Self-check before returning

- Every one of the 4 real risk levels is represented somewhere — none silently dropped, including
  a level with zero obligations.
- `RiskLabel` is used throughout; the raw `RiskType` integer never appears as if it meant severity.
- If `A-IMP-OVERLAP` is present, Critical-tier volume and imprisonment exposure are stated as ONE
  fact, not two separate findings.
- The `A-CRIT` direction is stated exactly as computed — a `better` direction reads as real good
  news, not forced into a warning tone.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated, not buried or dropped.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

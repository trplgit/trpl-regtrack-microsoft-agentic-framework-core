# Report Generation — Nature of Compliance, freehand (v2, 2026-09-25)

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging/Risk:
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

**6. Long tables — contained, never page-growing.** Any table listing every real member of the
dimension (a "complete register," "all configured natures," or similar — not a capped top-5 list)
goes inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header (`position:sticky;top:0`) keeps column labels visible while scrolling.

**7. The uncategorised gap needs a visually distinct treatment.** This dimension is structurally
half-blind (`UncategorisedPct` typically ~50%) — this fact deserves a clearly separate visual
treatment from the real, named-nature ranking (e.g. its own callout card, a visually distinct
segment in a chart, or a dedicated banner), never mixed into a ranked list of real natures as if it
were one more entry competing on the same axis.

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

## Real vs. NOT AVAILABLE — Nature dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a nature row** (`dimension_rows`): `NatureId`, `NatureName`, `IsRetired`,
`Instances`, `Overdue`, `OverduePct`, `Ownerless`, `ImprisonmentInstances`, `ImprisonmentOverdue`,
`CriticalInstances`, `BranchesCovered`, `PenaltyBearingInstances`, `FinancialPenaltyInstances`,
`ClosureRiskInstances`, `ImprisonmentSharePct`, `OverdueRank`, `Flags`. A real "Others" row is
always present — see below.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `CategorisedInstances`,
`Reconciled`, `OverdueInstances`, `TenantOverduePct`, `TenantImprisonmentSharePct`,
`NaturesReported`, `NaturesWithObligations`, `RetiredNaturesStillInUse`, `OthersBucketInstances`,
`UntaggedInstances`, `UncategorisedInstances`, `UncategorisedPct`.

| Fact | Status |
|---|---|
| "{N} of {M} instances are properly categorised by nature" | **REAL** — but must be paired with `UncategorisedPct`/`UncategorisedInstances` in the SAME breath, never stated alone as if categorisation were complete. |
| "This report covers the full estate by nature" | **NOT AVAILABLE as an unqualified claim** — roughly half the estate is typically uncategorised (`UncategorisedPct`). State the real coverage fraction instead. |
| "{Nature} has the highest overdue rate" | **REAL, only for a real named nature** — `A-WORST-NATURE`. The "Others" row is NEVER eligible for this claim even if its own `OverduePct` is numerically highest — it is excluded from the ranking by construction; never override that by computing your own rank from the raw rows. |
| "{Nature} carries personal liability on {X}% of its obligations" | **REAL** — `ImprisonmentSharePct` (a real per-nature rate) or `A-IMPLIN-*`. Do not present this as a SEPARATE exposure from Critical risk — this run has no Risk-dimension data to check that overlap; state the real percentage without an independence claim either way. |
| A root cause for why a nature runs worse, or why the estate is uncategorised | **NOT AVAILABLE** — state the pattern, never infer why. |
| "Untagged" as a nature with its own name/identity | **NOT AVAILABLE** — untagged instances are a real control-total figure (`UntaggedInstances`), not a member with a name. Never invent a row for it. |

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
  only — a nature's real counts here reflect only that window, not the tenant's all-time nature
  breakdown." (the real dates come from the `window` entry's own `detail` text, reformatted for
  readability, never invented)

State this near the top of the page (it changes what every other number here means, including
`ScopedInstances` and the uncategorised gap) — a caller comparing two runs needs to know they may be
looking at two different windows, not two different tenants.

## Self-check before returning

- The uncategorised gap (`UncategorisedPct`) is stated plainly, prominently, and visually distinct
  from the ranked real-nature content — never buried, never implied away.
- The "Others" row (if shown) is never presented as a ranked or "worst" nature.
- No claim of full/complete nature coverage anywhere.
- If personal-liability exposure is discussed, it is never framed as independent from Critical
  risk exposure.
- Every real nature row with meaningful volume is represented somewhere — none silently dropped.
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

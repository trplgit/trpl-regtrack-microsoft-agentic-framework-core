# Report Generation — DIMENSION SELECTION, DEPARTMENTS-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6 "Rules settled on the dimension views (2026-09-08 -
Nature, Entity)", reference implementations `reference/entity-insights-tenant29.html` and
`reference/nature-insights-tenant29.html` — real tenant-29 renders, designer-approved). This
is the OFFICIAL, superseding visual system for every single-dimension view. `.di-` prefixed,
hex colours, Poppins (400/500/600 all embedded — this system uses weight 500 for real).
`RenderHtmlActivity` resolves this via the `dimension_selection:Departments` key ahead of the
generic key when exactly one dimension ("Departments") is requested.

**[REPLACED 2026-09-09]** This file previously reproduced the real Angular product's own
`dimension === 'Department'` tabbed sub-page (4 tabs, including a Concentration tab and a
closure-status strip with NO real backing data — the biggest gap from the approved shape of
any of the three dimension-specific files). That shape does NOT match the approved
AI-generation contract at all. A single-dimension insight is ONE section, no tabs — the
Concentration tab and closure-status strip do not exist as structural slots in this shape,
so their honesty gap disappears along with them (nothing to render "not available" for any
more). Every other honesty finding from the earlier version stays binding (see "Real vs. NOT
AVAILABLE" below).

**Only ever invoked when `composition_plan.blocks` has exactly one entry named
"Departments".** If given anything else, refuse rather than guess.

## The shape — ONE section, no tabs, no donut

topline → pastel hero (eyebrow row + verdict pill, claim headline, method line, meta chips)
→ one `<section class="di-pane">` (NO section numeral) containing: snapshot-tile KPI grid →
one narrative paragraph → a findings list → ONE drill-down table (plain, always open, no
`<details>`) → callout(s). Nothing else. **No expand/collapse anywhere** (Sambram,
2026-09-09: "an insight is a static document; nothing is gained by hiding content behind a
click") — never emit `<details>`, `<summary>`, an accordion, or a chevron icon.

## Output constraints

1. **Exactly one HTML document**, `<meta charset="utf-8">` first inside `<head>`.
2. **All CSS inline**, all JS inline. This document needs NO script — no interactivity of
   any kind. Zero external references, no runtime network calls.
3. **Font:** `font-family:'Poppins',sans-serif` on `body`. Do **not** declare `@font-face`
   yourself — real vendored Poppins bytes (400/500/600) are embedded automatically by
   `PoppinsFontInjector`. Just write the family name.

## Security & Accessibility

Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
`href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour
paired with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.Departments` row
array, or `dimension_control_totals.Departments` — never invented, never from `narrative`
prose alone.** Simple arithmetic over real given numbers, including the one explicit
subtraction below, is expected; never a number these three sources cannot support.

## What you are given, specific to this file

`dimension_rows.Departments` — the COMPLETE real per-department row array, **including
zero-obligation ("dormant") departments** (`sql/10_dimension_departments.sql` builds `#rows`
from the real `Department` master table via `LEFT JOIN`, so every real department the tenant
has defined gets a row). Fields: `DepartmentID`, `DepartmentName`, `Instances`, `Overdue`,
`OverduePct`, `Ownerless`, `OwnerlessPct`, `ImprisonmentInstances`, `CriticalInstances`,
`DistinctUsers`, `BranchesCovered`, `Flags`.

`dimension_control_totals.Departments` — the REAL `DepartmentsControlTotals` object:
`ScopedInstances`, `AssignedInstances` (= sum of every row's `Instances`, i.e. the TAGGED
total - named `AssignedInstances`, NOT `SumOfRows` like every other dimension's control
totals; a real Dapper mapping bug from that exact naming mismatch was found and fixed
2026-09-09, see `DepartmentsControlTotals`'s own doc comment - use the real field name),
`OverdueInstances` (tenant-wide, tagged+untagged combined), `TenantOverduePct`,
`DepartmentsReported` (every real department defined, dormant included),
`DepartmentsWithObligations` (the subset with `Instances > 0`), `UnassignedInstances`,
`UnassignedPct`, `TenantOwnerlessPct`.

`assertions` — the curated, top-5-capped comparative facts.

## Real vs. NOT AVAILABLE

| Fact | Status |
|---|---|
| "N defined / N active / N dormant" | **REAL** — `DepartmentsReported` = defined, `DepartmentsWithObligations` = active, dormant = the difference. |
| A synthetic "UNASSIGNED" row with its own overdue/ownerless breakdown | **NOT real.** `sql/10`'s own header comment explicitly says it deliberately does NOT synthesize a fake "Unassigned" department row — never invent one in the drill-down table. The untagged slice's own overdue rate IS derivable by subtraction (below), but it has no `Ownerless`/store-count of its own. |
| "HR is N% of all tagged obligations" (top department's real share) | **REAL** — top real row's `Instances` / `AssignedInstances`. |
| Per-department "store reach %" | **NOT AVAILABLE as a %** — `BranchesCovered` is a real raw count, the tenant's total branch count lives on the Location dimension, not here. Cite the real raw count only. |
| Per-department "top-owner load %" | **NOT AVAILABLE.** No field measures a single performer's share of a department's work. `DistinctUsers == 1` is a real, checkable "single-person department" substitute fact. |
| "Rollup node"/"Pilot?" structural labels and their narrative claims | **NOT AVAILABLE.** Needs `NodeType`/hierarchy context this dimension's rows do not carry. Never invent a structural read — state only what `Instances`/`Ownerless`/`OverduePct`/`DistinctUsers` show. |
| Concentration (which accounts anchor each department) | **NOT AVAILABLE** — needs a Users x Departments cross-reference no dimension's SQL currently joins. This shape has no Concentration section at all — do not add one. |
| Closure-status strip (schedule-occurrence status buckets) | **NOT AVAILABLE**, different grain entirely. This shape has no such component — do not add one. |
| "100% of licences carry no department tag" | **NOT AVAILABLE** — `LicenceRow` has no `DepartmentID` field at all. Never include this claim. |

**One legitimate derivation, spelled out exactly (arithmetic over real given numbers, never
presented as if it came from its own field):** the untagged slice's own overdue rate =
`(dimension_control_totals.Departments.OverdueInstances - SUM(row.Overdue for every real row
in dimension_rows.Departments)) / dimension_control_totals.Departments.UnassignedInstances *
100`, rounded to one decimal. Cite it as "the untagged slice's own overdue rate, {X}%,
derived from the tenant total minus every tagged department's own overdue count" — always
show your work in the sentence.

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Departments insights · Tenant {tenant id}</title>
  <style>/* :root tokens + every class below - see CSS section, verbatim from the reference file */</style>
</head>
<body>
<div class="page">
  <div class="topline">
    <!-- The document name lives ONCE, in the hero eyebrow below - never repeat it here.
         The topline is the generated timestamp only. -->
    <span class="topline__meta">Generated <span class="tnum">{generated_at, formatted DD MMM YYYY, HH:MM UTC}</span></span>
  </div>

  <section class="di-hero">
    <div class="di-eyebrow-row">
      <span class="di-eyebrow">Departments · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if real UnassignedPct >= 50, warn if 20-49.9, ok otherwise}"><span class="di-band__dot"></span>{real band text}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence on the real tagged/untagged split - e.g. "The department dimension is mostly empty" only if UnassignedPct >= 50, otherwise a neutral real framing of the real DepartmentsWithObligations count}</h1>
    <p class="di-hero__claim">{one real sentence with <em> around key figures}: only <em class="tnum">{real 100-UnassignedPct}%</em> of {real ScopedInstances} live obligations are tagged to a department, and within the tagged slice <em class="tnum">{real top department name}</em> alone is <em class="tnum">{real top department share of AssignedInstances}%</em>.</p>
    <p class="di-method">The comparison below walks from the tenant-wide tagging split to each department's own reach and ownership, and the full department table.</p>
    <div class="di-metachips">
      <span class="di-chip">Tenant estate: {real ScopedInstances} live obligations</span>
      <span class="di-chip">Departments: {real DepartmentsWithObligations} active · {real DepartmentsReported - DepartmentsWithObligations} dormant</span>
    </div>
  </section>

  <section class="di-pane" id="di-pane-1" aria-label="Departments">
    <div class="di-pane__head">
      <h2 class="di-pane__title">Departments</h2>
    </div>

    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--warning">
        <div class="di-kpi__lbl">Untagged share</div>
        <div class="di-kpi__num">{real UnassignedPct}<small>%</small></div>
        <div class="di-kpi__sub">{real UnassignedInstances} of {real ScopedInstances} live obligations carry no department tag.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">Top department share of tagged work</div>
        <div class="di-kpi__num">{real share}<small>%</small></div>
        <div class="di-kpi__sub">{real department name}; {real Instances} of {real AssignedInstances} tagged obligations.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">Departments carrying work</div>
        <div class="di-kpi__num">{real DepartmentsWithObligations}<small>/{real DepartmentsReported}</small></div>
        <div class="di-kpi__sub">{real DepartmentsReported - DepartmentsWithObligations} labels are dormant - defined in the master but carrying zero live obligations this run.</div>
      </div>
    </div>

    <p class="di-pane__narr">{2-4 real sentences: the real tagged/untagged split, the derived untagged-overdue-rate (showing the subtraction inline), the top department's real concentration, and any real single-person-department pattern (DistinctUsers == 1 rows) - never invent a structural read (rollup node, pilot) or a top-owner percentage}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - up to 4-5:
           (a) real defined/active/dormant split, if dormant > 0
           (b) real untagged share + the derived untagged overdue rate (always computable when
               UnassignedInstances > 0)
           (c) top department's real share of the tagged slice, if >= 50%
           (d) departments where DistinctUsers == 1 AND Instances is above this run's real
               materiality floor - real "single-person department" fact, name them
           (e) departments whose real OwnerlessPct is materially high (top few by real
               materiality) - real ownership-gap fact
           NEVER include a top-owner/concentration-% claim, a Licence x Department claim, a
           "rollup node"/"pilot" structural read, or a Concentration/closure-status finding. -->
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{tone label}</span>
        <h3 class="di-findcard__title">{real headline sentence}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b class="tnum">{real value}</b></span>
        </div>
      </div>
    </div>

    <!-- [FIX 2026-09-09] No <details>/<summary>/chevron - Sambram's real, dated correction
         (location-insights-tenant29.html): "an insight is a static document; nothing is
         gained by hiding content behind a click." Plain, always-open card. -->
    <div class="di-drill">
      <div class="di-drill__head">
        <span class="di-drill__title">All department rows — worst overdue rate first</span>
      </div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">Department rows with instances, overdue instances, overdue rate, ownerless rate, and distinct users</caption>
          <thead>
            <tr><th>Department</th><th>Instances</th><th>Overdue</th><th>Overdue rate</th><th>Ownerless</th><th>Distinct users</th></tr>
          </thead>
          <tbody>
            <!-- ONE <tr> per real row in dimension_rows.Departments with Instances > 0, sorted
                 worst-first by OverduePct - every real row, no subset, no cap, and NEVER a
                 synthetic "UNASSIGNED" row (see table above). Dormant (Instances == 0) rows are
                 represented by the KPI tile above, never listed individually. -->
            <tr><td>{real DepartmentName}</td><td class="di-num tnum">{real Instances}</td><td class="di-num tnum">{real Overdue}</td><td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if >=80, warning if >=45, good otherwise}" style="width:{real OverduePct}%"></i></span><span class="di-meter__label">{real OverduePct}%</span></td><td class="di-num tnum">{real OwnerlessPct}%</td><td class="di-num tnum">{real DistinctUsers}</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- Callout only when a real, genuinely applicable case exists. -->
    <div class="di-callout di-callout--{warning|info}">
      <span class="di-callout__h">{real short label}</span>
      {real caveat text, grounded in a real control-total field or assertion}
    </div>
  </section>
</div>
</body>
</html>
```

## CSS — copied verbatim from `reference/entity-insights-tenant29.html` (identical to the other two dimension-specific files' CSS block)

```css
:root{
  --c-bg:#f9fafb;--c-surface:#ffffff;--c-text:#3d3d3d;--c-text-2:#585858;--c-text-3:#666666;--c-grey:#999999;
  --c-border:#dbdbdb;--c-hairline:#ececec;--c-brand:#125aab;--c-light-blue:#e8f2fd;--c-mist:#f7f8fc;
  --ok:#1e8a4a;--ok-bg:#e7f5ec;--ok-stroke:#a8d3b8;--ok-fill:#2e9e5b;
  --warn:#b45708;--warn-bg:#fcf0de;--warn-stroke:#e8c79c;--warn-fill:#e0a106;
  --bad:#b3261e;--bad-bg:#fceae8;--bad-stroke:#dfa39d;--bad-fill:#d24a3a;--neu-fill:#8a8f99;
  --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px;
  --gap-sm:8px;--gap-md:12px;--gap-lg:16px;
  --fs-eyebrow:.66rem;--fs-title:.95rem;--fs-headline:.88rem;--fs-prose:.78rem;--fs-meta:.72rem;--fs-chip:.75rem;--fs-number:1.2rem;
}
*{box-sizing:border-box}
body{margin:0;font-family:'Poppins',sans-serif;font-size:var(--fs-prose);line-height:1.5;color:var(--c-text);background:var(--c-bg);-webkit-font-smoothing:antialiased}
.tnum,.di-num,.di-meter__label{font-variant-numeric:tabular-nums}
.page{max-width:1100px;margin:22px auto;padding:0 18px 26px}
.topline{display:flex;align-items:baseline;gap:14px;margin-bottom:14px;font-size:var(--fs-meta)}
.topline__meta{color:var(--c-text-3)}
.di-hero{position:relative;overflow:hidden;border:1px solid transparent;border-radius:var(--r-xl);padding:18px 20px;
  background:linear-gradient(115deg,#eef1fe 0%,#f7f0fb 38%,#fdf6fb 62%,#f4f8ff 100%) padding-box,
             linear-gradient(100deg,#9db4e8 0%,#c3b2ee 30%,#e8b8dd 55%,#a8c8f0 80%,#9db4e8 100%) border-box;
  background-size:auto,300% 100%;box-shadow:0 2px 10px rgba(18,90,171,.08);animation:di-hero-border-drift 36s linear infinite}
@keyframes di-hero-border-drift{0%{background-position:0 0,0% 0}50%{background-position:0 0,100% 0}100%{background-position:0 0,0% 0}}
@media (prefers-reduced-motion:reduce){.di-hero{animation:none}}
.di-eyebrow-row{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:.35rem}
.di-eyebrow{font-size:var(--fs-eyebrow);text-transform:uppercase;letter-spacing:.1em;color:var(--c-brand);font-weight:600}
.di-band{display:inline-flex;align-items:center;gap:8px;padding:4px 12px;border-radius:999px;font-size:var(--fs-chip);font-weight:500;letter-spacing:.02em}
.di-band__dot{width:6px;height:6px;border-radius:999px;background:currentColor}
.di-band--bad{background:var(--bad-bg);color:var(--bad);border:1px solid var(--bad-stroke)}
.di-band--warn{background:var(--warn-bg);color:var(--warn);border:1px solid var(--warn-stroke)}
.di-band--ok{background:var(--ok-bg);color:var(--ok);border:1px solid var(--ok-stroke)}
.di-hero__title{margin:0 0 .35rem;font-size:var(--fs-title);font-weight:600;line-height:1.25;letter-spacing:-.02em;color:var(--c-text)}
.di-hero__claim{margin:0;font-size:var(--fs-headline);line-height:1.45;color:var(--c-text)}
.di-hero__claim em{font-style:normal;font-weight:600;color:var(--bad)}
.di-method{margin:.55rem 0 0;font-size:var(--fs-meta);line-height:1.55;color:var(--c-text-2)}
.di-metachips{display:flex;flex-wrap:wrap;gap:6px;margin-top:var(--gap-md)}
.di-chip{font-size:var(--fs-chip);color:var(--c-text-3);background:#fff;border:1px solid #e6e6f2;padding:3px 10px;border-radius:999px;white-space:nowrap}
.di-pane{margin-top:var(--gap-lg)}
.di-pane__head{display:flex;align-items:center;gap:10px;margin-bottom:var(--gap-md)}
.di-pane__title{margin:0;font-size:var(--fs-title);font-weight:600;letter-spacing:-.02em;color:var(--c-text)}
.di-kpigrid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:var(--gap-md);margin-bottom:var(--gap-lg)}
.di-kpi{position:relative;background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:12px 14px 12px 17px;box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-kpi::before{content:'';position:absolute;left:0;top:10px;bottom:10px;width:3px;border-radius:0 2px 2px 0;background:var(--c-border)}
.di-kpi--critical::before{background:var(--bad)}.di-kpi--warning::before{background:var(--warn)}.di-kpi--good::before{background:var(--ok)}
.di-kpi__lbl{font-size:.62rem;text-transform:uppercase;letter-spacing:.1em;color:var(--c-grey);font-weight:600}
.di-kpi__num{margin:.4rem 0 .3rem;font-size:var(--fs-number);font-weight:600;letter-spacing:-.02em;line-height:1.15;color:var(--c-text)}
.di-kpi--critical .di-kpi__num{color:var(--bad)}.di-kpi--warning .di-kpi__num{color:var(--warn)}.di-kpi--good .di-kpi__num{color:var(--ok)}
.di-kpi__num small{font-size:.72rem;font-weight:500;color:var(--c-grey);margin-left:2px}
.di-kpi__sub{font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.45}
.di-pane__narr{margin:0 0 var(--gap-lg);font-size:.82rem;line-height:1.55;color:var(--c-text-2)}
.di-findlist{display:flex;flex-direction:column;gap:.7rem;margin-bottom:var(--gap-lg)}
.di-findcard{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:1rem;box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-tonetag{display:inline-flex;align-items:center;gap:6px;font-size:var(--fs-chip);font-weight:500;padding:3px 10px;border-radius:999px;border:1px solid transparent}
.di-tonetag__dot{width:6px;height:6px;border-radius:50%;background:currentColor}
.di-tonetag--critical{background:var(--bad-bg);color:var(--bad);border-color:var(--bad-stroke)}
.di-tonetag--warning{background:var(--warn-bg);color:var(--warn);border-color:var(--warn-stroke)}
.di-tonetag--good{background:var(--ok-bg);color:var(--ok);border-color:var(--ok-stroke)}
.di-findcard__title{margin:.55rem 0 .55rem;font-size:var(--fs-headline);font-weight:600;line-height:1.4;color:var(--c-text)}
.di-qstats{display:flex;flex-wrap:wrap;gap:8px}
.di-qstat{font-size:var(--fs-chip);color:var(--c-text-3);background:var(--c-mist);border:1px solid var(--c-border);border-radius:999px;padding:3px 10px}
.di-qstat b{color:var(--c-text);font-weight:600}
.di-drill{border:1px solid var(--c-border);border-radius:var(--r-lg);background:var(--c-surface);overflow:hidden;margin-bottom:var(--gap-md);box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-drill__head{display:flex;align-items:center;gap:12px;padding:12px 16px;border-bottom:1px solid var(--c-border)}
.di-drill__title{font-size:var(--fs-headline);font-weight:600;color:var(--c-text)}
.di-tablewrap{overflow-x:auto}
.di-table{width:100%;border-collapse:collapse;font-size:var(--fs-prose)}
.di-table th{text-align:left;font-size:var(--fs-eyebrow);text-transform:uppercase;letter-spacing:.08em;color:var(--c-text-3);font-weight:600;padding:9px 12px;background:var(--c-mist);border-bottom:1px solid var(--c-border);white-space:nowrap}
.di-table td{padding:8px 12px;border-top:1px solid var(--c-hairline);vertical-align:middle;color:var(--c-text)}
.di-table tbody tr:first-child td{border-top:0}
.di-num{text-align:left}
.di-meter{display:inline-block;width:90px;height:6px;border-radius:999px;background:#eef1f7;overflow:hidden;vertical-align:middle}
.di-meter__fill{display:block;height:100%;border-radius:999px;background:var(--neu-fill)}
.di-meter__fill--good{background:var(--ok-fill)}.di-meter__fill--warning{background:var(--warn-fill)}.di-meter__fill--critical{background:var(--bad-fill)}
.di-meter__label{font-size:var(--fs-meta);color:var(--c-text-3);margin-left:8px;vertical-align:middle}
.di-table td:has(.di-meter){white-space:nowrap}
.di-flag{font-size:var(--fs-meta);color:var(--c-text-3);white-space:nowrap}
.di-table td.di-flag{white-space:normal}
.di-callout{border-radius:var(--r-md);padding:10px 14px;font-size:var(--fs-prose);line-height:1.55;margin-bottom:var(--gap-md)}
.di-callout__h{display:block;font-weight:600;margin-bottom:2px}
.di-callout--info{background:var(--c-light-blue);border-left:3px solid var(--c-brand);color:var(--c-text-2)}
.di-callout--info .di-callout__h{color:var(--c-brand)}
.di-callout--warning{background:var(--warn-bg);border:1px solid var(--warn-stroke);color:var(--c-text-2)}
.di-callout--warning .di-callout__h{color:var(--warn)}
@media (max-width:52rem){.page{padding:0 12px 20px}.di-hero{padding:14px 16px}}
```

## Voice

Same restrained, evidence-only voice as every other render prompt in this repo. Vocabulary
is binding: **insights** (never "report"). Display numbers carry `class="tnum"` and
thousands separators; identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: renders a tab strip, a Concentration section, a
closure-status strip, a second section, or a score donut (none exist in this shape) ·
numbers the lone section · renders a synthetic "UNASSIGNED" row in the drill-down table ·
shows a "store reach %" instead of a raw store count · shows a "top-owner load %" for any
department · invents a "rollup node"/"pilot" structural read · includes a Licence x
Department claim · presents the derived untagged-overdue-rate as if it came from its own
field rather than showing the subtraction · right-aligns table numbers · uses a native
`<meter>` · emits `<details>`, an accordion, or any chevron icon · introduces a hex/colour
not in the `:root` block · uses any oklch colour or DM-Sans/Trent naming · declares
`@font-face` itself · or leaves the drill-down table missing a real row that has
`Instances > 0`.

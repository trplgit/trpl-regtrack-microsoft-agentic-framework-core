# Report Generation — DIMENSION SELECTION, ACT-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6, reference implementations `reference/entity-insights-tenant29.html`
and `reference/nature-insights-tenant29.html`). `.di-` prefixed, hex colours, Poppins (400/500/600
embedded). `RenderHtmlActivity` resolves this via the `dimension_selection:Act` key ahead of the
generic key when exactly one dimension ("Act") is requested.

**Content and analysis mimicked from Trent's `05 - Acts and Regulators` report** (which laws the
late work comes from — the "most late work" and "worst late rate" cuts, the jail-risk column, the
"dangerous tasks are often the best-managed" observation). Rendered in Sambram's single-section
visual system, NOT Trent's own oklch/DM-Sans look.

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "Act".** If given
anything else, refuse rather than guess.

## The shape — ONE section, no tabs, no donut

topline → pastel hero (eyebrow row + verdict pill, claim headline, method line, meta chips)
→ one `<section class="di-pane">` (NO section numeral) containing: snapshot-tile KPI grid →
one narrative paragraph → a findings list → ONE drill-down table (plain, always open, no
`<details>`) → callout(s). Nothing else. **No expand/collapse anywhere** — never emit
`<details>`, `<summary>`, an accordion, or a chevron icon.

## Output constraints

1. **Exactly one HTML document**, `<meta charset="utf-8">` first inside `<head>`.
2. **All CSS inline**, all JS inline. This document needs NO script. Zero external references.
3. **Font:** `font-family:'Poppins',sans-serif` on `body`. Do **not** declare `@font-face` yourself
   — real vendored Poppins bytes (400/500/600) are embedded by `PoppinsFontInjector`.

## Security & Accessibility

Escape all tenant-entered text — **act names and state names are tenant-adjacent free text, always
escape them.** Never place any of it in a `<script>`, `onclick=`, `style=`, or `href`/`src`.
Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour paired with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.Act` row array, or
`dimension_control_totals.Act` — never invented, never from `narrative` prose alone.** A share, a
rank, "N of the top M acts also carry jail risk" — all fine as arithmetic over the real rows.
Never a number these sources cannot support.

## What you are given, specific to this file

`dimension_rows.Act` — the COMPLETE real per-act row array (`sql/11_dimension_act.sql` builds
`#rows` from the tenant's real linked acts). Fields: `ActID`, `ActName` (the real statute name —
tenant-adjacent free text, escape it), `State` (`Central` or a real state name; may be null),
`RegulatorID` (an ID only — **no regulator name is available**), `CategoryId`, `Instances` (tasks
under this act), `Overdue`, `OverduePct` (this act's own real late rate), `ImprisonmentInstances`
(tasks under this act that carry imprisonment exposure — the "jail risk" column), `BranchesCovered`,
`StartDate`, `OverdueRank` (rank by `OverduePct` among acts above the run's real materiality floor;
null for acts below it), `Flags`.

`dimension_control_totals.Act` — the real object: `ScopedInstances`, `SumOfRows` (== sum of every
row's `Instances`; the proc reconciles), `Reconciled`, `OverdueInstances`, `TenantOverduePct` (the
tenant-wide late rate — this act dimension's own real comparator baseline), `ActsReported`,
`DistinctActNames`, `StatesCovered`, `ActsSpanningMultipleStates`, `UnlinkedInstances`,
`UnlinkedPct` (share of scoped tasks NOT linked to any act), `LargestRegulatorId`,
`LargestRegulatorSharePct` (the biggest single regulator's share of all tasks).

`assertions` — curated, top-5-capped comparative facts (per-act late rate vs `TenantOverduePct`,
spread, etc.).

## Real vs. NOT AVAILABLE

| Fact | Status |
|---|---|
| "Tasks come from {N} laws across {M} states" | **REAL** — `DistinctActNames` (or `ActsReported`) and `StatesCovered`. |
| "Every task is correctly linked to its law" | **REAL only when `UnlinkedPct` == 0** — otherwise state the real `UnlinkedInstances` / `UnlinkedPct` plainly ("{N} tasks ({X}%) are not linked to any act"). Never claim full linkage when `UnlinkedInstances > 0`. |
| "The single largest regulator accounts for {X}% of all tasks" | **REAL as a percentage** — `LargestRegulatorSharePct`. **The regulator's NAME is NOT AVAILABLE** (`LargestRegulatorId` is an id only, and rows carry `RegulatorID`, not a name). Say "the single largest regulator" — never name it. |
| The "laws with the most late work" table (Act, State, Tasks, Late, Late rate, Jail risk) | **REAL** — the rows sorted by `Overdue` desc; `ImprisonmentInstances` is the jail-risk column. |
| The "laws with the worst late rates" cut | **REAL** — rows sorted by `OverduePct` desc, restricted to `Instances >= the run's materiality floor` (use `OverdueRank IS NOT NULL` as the "is this rank meaningful" test). Surface these as finding cards, not a second table. |
| "This act is better/worse managed than the company average" | **REAL** — compare the row's `OverduePct` to `TenantOverduePct`; state both numbers. |
| "The most dangerous tasks are the best managed" (high jail risk, low late rate) | **REAL as a pattern** — only when a real row (or a few) has high `ImprisonmentInstances` AND `OverduePct` below `TenantOverduePct`; name the act(s) and quote both real numbers. Never assert it as a general law without the specific rows. |
| "Fire safety stands out twice" / any subject-clustering claim ("one buying-and-inspection problem") | **NOT AVAILABLE as an analytic claim.** You may note, factually, that the worst-rate acts' real `ActName`s appear to concern a common subject **only if that is literally visible in the escaped names** — never infer a root cause, an owner, or a "one problem not N problems" reading. |
| A per-regulator or per-category rollup | **NOT AVAILABLE** — rows are per-act; `RegulatorID`/`CategoryId` are ids with no names or aggregates supplied beyond `LargestRegulatorSharePct`. |
| "Store reach %" for an act | **NOT AVAILABLE as a %** — `BranchesCovered` is a real raw count; the tenant branch total lives on the Location dimension. Cite the raw count only. |

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Acts &amp; regulators insights · Tenant {tenant id}</title>
  <style>/* :root tokens + every class below - verbatim from the CSS section */</style>
</head>
<body>
<div class="page">
  <div class="topline">
    <!-- The document name lives ONCE, in the hero eyebrow below - never repeat it here.
         The topline is the generated timestamp only. -->
    <span class="topline__meta">Generated <span class="tnum">{generated_at, DD MMM YYYY, HH:MM UTC}</span></span>
  </div>

  <section class="di-hero">
    <div class="di-eyebrow-row">
      <span class="di-eyebrow">Acts &amp; regulators · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if the top act's Overdue share of OverdueInstances >= 25, warn if 10-24.9, ok otherwise}"><span class="di-band__dot"></span>{real band text, e.g. "Concentrated" / "Spread" }</span>
    </div>
    <h1 class="di-hero__title">{one real sentence: which single act produces the most late work and its real share of all overdue tasks — e.g. "One act produces {X}% of all late work"}</h1>
    <p class="di-hero__claim">Tasks come from <em class="tnum">{real DistinctActNames}</em> laws across <em class="tnum">{real StatesCovered}</em> states; the single largest regulator accounts for <em class="tnum">{real LargestRegulatorSharePct}%</em> of all tasks{, and <em class="tnum">{real UnlinkedInstances}</em> ({real UnlinkedPct}%) are not linked to any act — include this clause ONLY when UnlinkedInstances > 0}.</p>
    <p class="di-method">The comparison below ranks each law by how much late work it produces, then by its own late rate against the company-wide rate, and flags the tasks that carry imprisonment exposure.</p>
    <div class="di-metachips">
      <span class="di-chip">Tenant estate: {real ScopedInstances} tasks</span>
      <span class="di-chip">Company-wide late rate: {real TenantOverduePct}%</span>
      <span class="di-chip">Acts reported: {real ActsReported}</span>
    </div>
  </section>

  <section class="di-pane" id="di-pane-1" aria-label="Acts and regulators">
    <div class="di-pane__head"><h2 class="di-pane__title">Acts &amp; regulators</h2></div>

    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--{critical if top act's share of OverdueInstances >= 25, warning if >= 10, good otherwise}">
        <div class="di-kpi__lbl">Biggest source of late work</div>
        <div class="di-kpi__num">{real top-by-Overdue act's Overdue}<small> late</small></div>
        <div class="di-kpi__sub">{real escaped ActName}: {real Overdue} of {real OverdueInstances} overdue tasks ({real share}%), {real OverduePct}% of its own {real Instances} tasks.</div>
      </div>
      <div class="di-kpi di-kpi--warning">
        <div class="di-kpi__lbl">Worst late rate (material acts)</div>
        <div class="di-kpi__num">{real worst material act's OverduePct}<small>%</small></div>
        <div class="di-kpi__sub">{real escaped ActName}; {real Overdue} of {real Instances} tasks late — vs {real TenantOverduePct}% company-wide.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">Tasks carrying jail risk</div>
        <div class="di-kpi__num">{real SUM of ImprisonmentInstances across all rows}</div>
        <div class="di-kpi__sub">across {real count of rows with ImprisonmentInstances > 0} acts; {real count where ImprisonmentInstances > 0 AND OverduePct < TenantOverduePct} of them run below the company late rate.</div>
      </div>
    </div>

    <p class="di-pane__narr">{2-4 real sentences: the concentration of late work on the top act(s); the worst-rate acts vs the company rate (name them, both numbers); and — only when real rows support it — the "high jail risk, low late rate" pattern with the specific acts and numbers. Never name the largest regulator, never infer a subject-level root cause or single owner.}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - up to 4-5:
           (a) the top act's real share of all late work, if >= 20%
           (b) each of the top 2-3 worst-rate MATERIAL acts (OverdueRank IS NOT NULL), with the
               act name, its OverduePct, and TenantOverduePct
           (c) the "most dangerous, best managed" pattern - ONLY with real rows: high
               ImprisonmentInstances AND OverduePct < TenantOverduePct
           (d) acts spanning multiple states, if ActsSpanningMultipleStates > 0 - a real
               "same law, many jurisdictions" fact
           NEVER a regulator-name finding, a subject/root-cause finding, or a per-category rollup. -->
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{tone label}</span>
        <h3 class="di-findcard__title">{real headline sentence}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b class="tnum">{real value}</b></span>
        </div>
      </div>
    </div>

    <div class="di-drill">
      <div class="di-drill__head"><span class="di-drill__title">Laws with the most late work — most late tasks first</span></div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">Acts with jurisdiction, task count, overdue count, overdue rate, and tasks carrying imprisonment exposure, sorted by overdue count</caption>
          <thead>
            <tr><th>Law</th><th>Applies</th><th>Tasks</th><th>Late</th><th>Late rate</th><th>Jail risk</th></tr>
          </thead>
          <tbody>
            <!-- ONE <tr> per real row with Instances > 0, sorted by Overdue DESC. Cap at the top
                 20 by Overdue (the tail is long and immaterial); if you cap, add a final caption
                 line in the drill__head noting "top 20 of {real ActsReported}". Every displayed
                 row is a real row - no synthetic totals row. -->
            <tr>
              <td>{real escaped ActName}</td>
              <td>{real State, or "—" if null}</td>
              <td class="di-num tnum">{real Instances}</td>
              <td class="di-num tnum">{real Overdue}</td>
              <td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if OverduePct>=45, warning if OverduePct>=TenantOverduePct, good otherwise}" style="width:{real OverduePct}%"></i></span><span class="di-meter__label">{real OverduePct}%</span></td>
              <td class="di-num tnum">{real ImprisonmentInstances}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="di-callout di-callout--info">
      <span class="di-callout__h">On linkage</span>
      {when UnlinkedInstances == 0: "Every scoped task is linked to an act — the counts above are complete."} {when UnlinkedInstances > 0: "{real UnlinkedInstances} tasks ({real UnlinkedPct}%) are not linked to any act and sit outside every row above."}
    </div>
  </section>
</div>
</body>
</html>
```

## CSS — copied verbatim from `reference/entity-insights-tenant29.html` (identical to the other dimension-specific files' CSS block)

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

Restrained, evidence-only. Vocabulary is binding: **insights** (never "report"). Display numbers
carry `class="tnum"` and thousands separators; act names, state names and identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: renders a tab strip, a second section, or a score donut ·
numbers the lone section · names the largest regulator · claims full act linkage while
`UnlinkedInstances > 0` · asserts a subject-level root cause ("fire safety", "one buying problem")
or a single owner · shows a per-regulator or per-category rollup · shows a "store reach %" instead
of a raw branch count · fails to escape an act or state name · right-aligns table numbers · uses a
native `<meter>` · emits `<details>`, an accordion, or any chevron · introduces a hex/colour not in
the `:root` block · uses any oklch colour or DM-Sans/Trent naming · declares `@font-face` itself ·
or leaves a real `Instances > 0` act out of the drill table when it is inside the displayed cap.

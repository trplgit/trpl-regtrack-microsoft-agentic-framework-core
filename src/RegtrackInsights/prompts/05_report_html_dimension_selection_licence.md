# Report Generation — DIMENSION SELECTION, LICENCE-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6, reference implementations `reference/entity-insights-tenant29.html`
and `reference/nature-insights-tenant29.html`). `.di-` prefixed, hex colours, Poppins (400/500/600
embedded). `RenderHtmlActivity` resolves this via the `dimension_selection:Licence` key ahead of
the generic key when exactly one dimension ("Licence") is requested.

**Content and analysis mimicked from Trent's `06 - Licences` report** (the expired-share headline,
the "what counts as expired" definition note, the worst-by-type table, the expiring-soon renewal
callout). Rendered in Sambram's single-section visual system, NOT Trent's own oklch/DM-Sans look.

**[BUG FOUND LIVE, 2026-09-10]** The deployed `usp_Insights_Dimension_Licence` proc (`sql/21`)
emits `TenantLapsedPct` / `Lapsed` / `LapsedPct` / `ExcludedTerminalState[Licences]`. The C#
`LicenceControlTotals` / `LicenceRow` records had drifted to a `*Corroborated*` model that no
result-set column carries, so Dapper silently returned `0` for every lapse figure in
control_totals and rows while the assertions still carried the real rate. Confirmed when a
Licence-only view refused to render on the contradiction; traced to `CompositeScoreCalculator`
scoring the licence component off a hardcoded-0 lapse rate. C# records renamed to match the SQL;
this file uses the real column names below.

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "Licence".** If
given anything else, refuse rather than guess.

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

Escape all tenant-adjacent text — **licence type names are free text, always escape them.** Never
place any of it in a `<script>`, `onclick=`, `style=`, or `href`/`src`. Semantic HTML, real
`<table>`/`<th>`/`<caption>`, ordered headings, colour paired with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.Licence` row array, or
`dimension_control_totals.Licence` — never invented, never from `narrative` prose alone.** A total
summed from the real rows, a share, a rank — all fine. **If a figure you want is not in the fields
listed below, omit that sentence — never substitute a near-neighbour field or an estimate.**

## What you are given, specific to this file — EXACT field names

`dimension_rows.Licence` — the COMPLETE real per-licence-type row array
(`sql/21_dimension_licence.sql` builds `#rows` from every active licence type plus any retired
type still carrying licences). Fields, verbatim:
`LicenseTypeID`, `LicenseTypeName` (real type name — free text, escape it), `IsRetired` (bit — the
type is deleted in the master but still carries licences), `TotalLicences`, `ActiveLicences` (end
date in the future), `Lapsed` (end date has passed AND the licence did **not** end another way —
**a not-updated status still counts as lapsed**), `ExcludedTerminalState` (end date passed but the
licence was renewed / terminated / rejected / marked not-applicable — **NOT a lapse**, given its
own bucket per the BA ruling), `LapsingNext30` (end date within the next 30 days),
`BranchesCovered`, `LapsedPct` (this type's own expiry rate over `TotalLicences`), `OverdueRank`
(rank by `LapsedPct` among types above the run's materiality floor; null below it), `Flags`.

`dimension_control_totals.Licence` — the real object. Fields, verbatim: `ScopedLicences` (total in
scope), `TypedLicences` (== sum of every row's `TotalLicences`), `Reconciled`, `TenantLapsedPct`
(tenant-wide expiry rate — the dimension's comparator baseline), `LicenceTypesReported`,
`LicenceTypesWithLicences`, `UntypedLicences` (`ScopedLicences − TypedLicences`),
`ExcludedTerminalStateLicences` (tenant-wide count of licences past their end date that ended by
renewal / termination / rejection / not-applicable — the agreed exclusions).

`assertions` — curated comparative facts: `A-TENANT` (`lapsed_pct`, tenant), `A-WORST-LICTYPE`
(`lapsed_pct` of the single worst type vs the tenant rate), and either `A-LAPSE-1..5` (individual
high-lapse types) or `A-LAPSE-AGG` (aggregate) depending on distribution. Use each assertion's
`Value` / `ComparatorValue` verbatim.

## Real vs. NOT AVAILABLE

| Fact | Status |
|---|---|
| "{X}% of licences have expired: {N} of {M}" | **REAL** — `TenantLapsedPct` = X; N = `SUM(row.Lapsed)` across every real row; M = `ScopedLicences`. Cross-check against the `A-TENANT` assertion's `Value` — if they disagree, use the assertion value and add a data-quality callout that control totals and the assertion disagree. |
| "{E} licences ended for a legitimate reason (renewed / terminated / rejected / not-applicable) and are NOT counted as expired" | **REAL** — `ExcludedTerminalStateLicences` = E (== `SUM(row.ExcludedTerminalState)`). Reproduce the agreed definition in a callout. Never add these back into the expired count. |
| "All {M} licences are categorised across {T} types" — say ONLY when `UntypedLicences == 0` | **REAL when true** — `LicenceTypesReported` = T. When `UntypedLicences > 0`, state the real untyped count instead. |
| The "worst by type" table (Type, Total, Expired, Expiry rate, Excluded) | **REAL** — rows sorted by `LapsedPct` desc; columns are `TotalLicences`, `Lapsed`, `LapsedPct`, `ExcludedTerminalState`. |
| "This type is worse / better managed than the tenant average" | **REAL** — the row's `LapsedPct` vs `TenantLapsedPct`; state both. |
| "{N} permits expire within 30 days" | **REAL** — `SUM(row.LapsingNext30)`. |
| "{N} expire within 60 / 90 days", "{N} within 7 days" | **NOT AVAILABLE** — only a **30-day** forward window exists (`LapsingNext30`). Never quote any other horizon. |
| "Expiries cluster in building/safety permits, not employment paperwork" / any subject-grouping analytic claim / "matches Report 05" | **NOT AVAILABLE as an analytic claim.** You may note factually that the worst-rate types' real escaped names appear to concern a common subject **only if that is literally visible in the names** — never infer a facilities-vs-HR root cause, an owner, or a cross-report link. |
| A per-branch or per-state licence breakdown | **NOT AVAILABLE** — rows are per-type; `BranchesCovered` is a real raw count only. |
| Severity ranking of licence types ("lift certificates: the most serious to let expire") | **NOT AVAILABLE** — no field ranks types by consequence. Rank only by the real rate / count. |

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Licences insights · Tenant {tenant id}</title>
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
      <span class="di-eyebrow">Licences · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if TenantLapsedPct >= 25, warn if 10-24.9, ok otherwise}"><span class="di-band__dot"></span>{real band text, e.g. "1 in 4 expired" / "Elevated expiry" / "Mostly current"}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence from TenantLapsedPct and the lapsed count — e.g. "{fraction, e.g. 1 in 4} licences has expired: {real total lapsed} of {real ScopedLicences}"}</h1>
    <p class="di-hero__claim">Across <em class="tnum">{real LicenceTypesReported}</em> licence types, <em class="tnum">{real total lapsed}</em> licences ({real TenantLapsedPct}%) have expired and are not held open by a legitimate reason; <em class="tnum">{real ExcludedTerminalStateLicences}</em> that ended by renewal, termination, rejection or not-applicable are excluded from that count.</p>
    <p class="di-method">The comparison below ranks each licence type by its own expiry rate against the tenant-wide rate, and flags the licences due to expire in the next 30 days.</p>
    <div class="di-metachips">
      <span class="di-chip">Licences in scope: {real ScopedLicences}</span>
      <span class="di-chip">Tenant-wide expiry rate: {real TenantLapsedPct}%</span>
      <span class="di-chip">Types with licences: {real LicenceTypesWithLicences}</span>
    </div>
  </section>

  <section class="di-pane" id="di-pane-1" aria-label="Licences">
    <div class="di-pane__head"><h2 class="di-pane__title">Licences</h2></div>

    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--{critical if TenantLapsedPct >= 25, warning if >= 10, good otherwise}">
        <div class="di-kpi__lbl">Expired</div>
        <div class="di-kpi__num">{real TenantLapsedPct}<small>%</small></div>
        <div class="di-kpi__sub">{real total lapsed} of {real ScopedLicences} licences past their end date with no legitimate reason to be.</div>
      </div>
      <div class="di-kpi di-kpi--warning">
        <div class="di-kpi__lbl">Worst type (material)</div>
        <div class="di-kpi__num">{real worst material type's LapsedPct}<small>%</small></div>
        <div class="di-kpi__sub">{real escaped LicenseTypeName}; {real Lapsed} of {real TotalLicences} expired — vs {real TenantLapsedPct}% tenant-wide.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">Expiring in the next 30 days</div>
        <div class="di-kpi__num">{real SUM of LapsingNext30 across all rows}</div>
        <div class="di-kpi__sub">Renew these before their end date to stop the expired count rising.</div>
      </div>
    </div>

    <p class="di-pane__narr">{2-4 real sentences: the tenant expiry rate and total lapsed; which types carry the highest rate (name the top 2-3, both numbers, vs TenantLapsedPct); the real 30-day expiring count; and the excluded-terminal-state count with a one-line reminder that those are not lapses. Never quote a 60/90-day figure, never infer a facilities-vs-HR or cross-report reading.}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - up to 4-5:
           (a) the tenant expiry rate itself, if TenantLapsedPct >= 20
           (b) each of the top 2-3 worst-rate MATERIAL types (OverdueRank IS NOT NULL): type
               name, its LapsedPct, TenantLapsedPct, and its raw Lapsed/TotalLicences
           (c) A-LAPSE-AGG if present - the aggregate "elevated across many types" fact
           (d) the 30-day expiring total, if SUM(LapsingNext30) > 0
           (e) any IsRetired == 1 type that still carries licences - a real "retired type, live
               licences" data-hygiene fact
           NEVER a 60/90-day finding, a subject/root-cause finding, a severity ranking of types,
           or a per-branch finding. -->
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{tone label}</span>
        <h3 class="di-findcard__title">{real headline sentence}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b class="tnum">{real value}</b></span>
        </div>
      </div>
    </div>

    <div class="di-drill">
      <div class="di-drill__head"><span class="di-drill__title">Expiry rate by licence type — worst first</span></div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">Licence types with total count, expired count, expiry rate, excluded (ended another way), and licences expiring within 30 days, sorted by expiry rate</caption>
          <thead>
            <tr><th>Licence type</th><th>Total</th><th>Expired</th><th>Expiry rate</th><th>Excluded</th><th>Due in 30 days</th></tr>
          </thead>
          <tbody>
            <!-- ONE <tr> per real row with TotalLicences > 0, sorted by LapsedPct DESC (ties
                 broken by TotalLicences DESC). Every such row - no cap, licence types are few.
                 A retired type (IsRetired == 1) still carrying licences: suffix the escaped name
                 with " (retired type)". No synthetic totals row. -->
            <tr>
              <td>{real escaped LicenseTypeName}{ " (retired type)" if IsRetired == 1}</td>
              <td class="di-num tnum">{real TotalLicences}</td>
              <td class="di-num tnum">{real Lapsed}</td>
              <td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if LapsedPct>=50, warning if LapsedPct>=TenantLapsedPct, good otherwise}" style="width:{real LapsedPct}%"></i></span><span class="di-meter__label">{real LapsedPct}%</span></td>
              <td class="di-num tnum">{real ExcludedTerminalState}</td>
              <td class="di-num tnum">{real LapsingNext30}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="di-callout di-callout--info">
      <span class="di-callout__h">What counts as expired</span>
      A licence is expired when its end date has passed and it did not end for a legitimate reason. A licence whose status simply was not updated still counts as expired. Renewed, terminated, rejected and not-applicable licences ({real ExcludedTerminalStateLicences} in total) are excluded and must not be added back.
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
carry `class="tnum"` and thousands separators; licence type names and identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: renders a tab strip, a second section, or a score donut ·
numbers the lone section · uses any field name not in the "EXACT field names" list · quotes a 60-
or 90-day expiring count · adds `ExcludedTerminalState` licences back into the expired count ·
asserts a subject/facilities-vs-HR root cause or a cross-report link · ranks licence types by
severity rather than by the real rate · shows a per-branch or per-state breakdown · fails to
escape a licence type name · right-aligns table numbers · uses a native `<meter>` · emits
`<details>`, an accordion, or any chevron · introduces a hex/colour not in the `:root` block ·
uses any oklch colour or DM-Sans/Trent naming · declares `@font-face` itself · or leaves a real
`TotalLicences > 0` type out of the drill table.

# Report Generation — DIMENSION SELECTION, BACKLOG-AGING-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6, reference implementations `reference/entity-insights-tenant29.html`
and `reference/nature-insights-tenant29.html` — real designer-approved tenant-29 renders). `.di-`
prefixed, hex colours, Poppins (400/500/600 embedded). `RenderHtmlActivity` resolves this via the
`dimension_selection:BacklogAging` key ahead of the generic key when exactly one dimension
("BacklogAging") is requested.

**Content and analysis mimicked from Trent's `03 - Backlog Aging` report** (how old is the late
work — three age groups that sum exactly to the total; the chase-vs-decide-vs-write-off framing).
Rendered in Sambram's single-section visual system, NOT Trent's own oklch/DM-Sans look.

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "BacklogAging".**
If given anything else, refuse rather than guess.

## The shape — ONE section, no tabs, no donut

topline → pastel hero (eyebrow row + verdict pill, claim headline, method line, meta chips)
→ one `<section class="di-pane">` (NO section numeral) containing: snapshot-tile KPI grid →
one narrative paragraph → a findings list → ONE drill-down table (plain, always open, no
`<details>`) → callout(s). Nothing else. **No expand/collapse anywhere** — never emit
`<details>`, `<summary>`, an accordion, or a chevron icon.

## Output constraints

1. **Exactly one HTML document**, `<meta charset="utf-8">` first inside `<head>`.
2. **All CSS inline**, all JS inline. This document needs NO script. Zero external references.
3. **Font:** `font-family:'Poppins',sans-serif` on `body`. Do **not** declare `@font-face`
   yourself — real vendored Poppins bytes (400/500/600) are embedded by `PoppinsFontInjector`.

## Security & Accessibility

Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
`href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour paired
with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.BacklogAging` row array
(exactly 3 rows), or `dimension_control_totals.BacklogAging` — never invented, never from
`narrative` prose alone.** Simple arithmetic over those real numbers (a share, a subtraction, the
age of the oldest item in whole years) is expected; never a number these sources cannot support.

## What you are given, specific to this file

`dimension_rows.BacklogAging` — **exactly 3 real rows**, one per age bucket
(`sql/22_dimension_backlog_aging.sql` builds them from a fixed `VALUES` list, so all 3 always
appear even at zero). Fields: `Bucket` (`current_fy` | `previous_fy` | `older`), `FYLabel` (the
real FY string, e.g. `FY2026-27`; **null on the `older` row** by construction), `OverdueCount`,
`OldestDueDate`, `NewestDueDate`, `SharePct` (that bucket's real share of `SumOfRows`).

`dimension_control_totals.BacklogAging` — the real `BacklogAgingControlTotals`: `CustomerID`,
`AsOfUtc`, `CurrentFyLabel`, `PreviousFyLabel`, `SumOfRows` (total overdue schedules — every
bucket sums to this exactly, the proc THROWs otherwise), `DistinctOverdueSchedules` (== `SumOfRows`
by reconciliation).

`assertions` — curated comparative facts: one `A-CURRENT_FY` / `A-PREVIOUS_FY` / `A-OLDER`
(`backlog_share_pct`) per bucket, plus `A-OLDER-AGG` (`backlog_older_than_previous_fy`) only when
the `older` bucket dominates the distribution.

## Real vs. NOT AVAILABLE

| Fact | Status |
|---|---|
| "N late items, split into 3 age groups that sum to the total" | **REAL** — `SumOfRows` and the 3 rows' `OverdueCount` / `SharePct`. |
| "Oldest item was due on {date}" / "{N} years past its due date" | **REAL** — `OldestDueDate` on the `older` row (or the earliest of the 3 rows' `OldestDueDate`); the years-past figure is `AsOfUtc − OldestDueDate` in whole years, shown as arithmetic ("due {date}, {N} years ago"). |
| "This year's group / last year's group / never-dealt-with group" wording | **REAL framing** of the 3 real buckets — `current_fy` = fell behind this year, `previous_fy` = has waited a full year, `older` = predates the previous FY. |
| "≈44k are workable, ≈38k need a leadership decision" chase-vs-write-off split | **REAL** — "workable" = `current_fy` `OverdueCount`; "needs a decision / write-off" = `previous_fy` + `older` `OverdueCount` (state both raw numbers; never round to a fake precision). |
| "Older than 3 years: {N} items" | **NOT AVAILABLE** — this dimension has no sub-3-years bucket; the only real ages are the 3 fixed buckets and each bucket's `OldestDueDate` / `NewestDueDate`. Never state a 3-year count. |
| "The headline X% late rate overstates the workable problem by ≈40%" | **NOT AVAILABLE as a rate** — BacklogAging carries the overdue **count** split only, never a late **rate** (that needs the total obligation count, which lives on the Location / Risk dimensions). You may say the workable slice is a real fraction of the overdue total (`current_fy` `SharePct`); never quote or derive a tenant late-rate here. |
| Per-branch or per-owner age breakdown | **NOT AVAILABLE** — the rows are age buckets, not members. No branch, category, or owner dimension is present. |

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Backlog aging insights · Tenant {tenant id}</title>
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
      <span class="di-eyebrow">Backlog aging · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if older SharePct >= 40, warn if 20-39.9, ok otherwise}"><span class="di-band__dot"></span>{real band text, e.g. "Structurally old" / "Ageing" / "Mostly recent"}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence built from the real bucket split - e.g. "{older SharePct}% of the backlog predates the previous fiscal year" when older is largest, otherwise a neutral real framing of where the mass sits}</h1>
    <p class="di-hero__claim">Of <em class="tnum">{real SumOfRows}</em> overdue schedules, <em class="tnum">{real current_fy OverdueCount}</em> ({real current_fy SharePct}%) fell behind this year and <em class="tnum">{real previous_fy OverdueCount + older OverdueCount}</em> have waited a year or more, the oldest since <em class="tnum">{real earliest OldestDueDate, DD MMM YYYY}</em>.</p>
    <p class="di-method">The three groups below are cut by when each schedule was originally due — this fiscal year, the previous one, and everything older — and always sum to the overdue total.</p>
    <div class="di-metachips">
      <span class="di-chip">Overdue schedules: {real SumOfRows}</span>
      <span class="di-chip">Current FY: {real CurrentFyLabel}</span>
      <span class="di-chip">Snapshot: {real AsOfUtc, DD MMM YYYY HH:MM UTC}</span>
    </div>
  </section>

  <section class="di-pane" id="di-pane-1" aria-label="Backlog aging">
    <div class="di-pane__head"><h2 class="di-pane__title">Backlog aging</h2></div>

    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--{critical if older SharePct >= 40, warning if >= 20, good otherwise}">
        <div class="di-kpi__lbl">Older than the previous FY</div>
        <div class="di-kpi__num">{real older SharePct}<small>%</small></div>
        <div class="di-kpi__sub">{real older OverdueCount} of {real SumOfRows} overdue schedules predate {real PreviousFyLabel} — never dealt with, not "behind this year".</div>
      </div>
      <div class="di-kpi di-kpi--good">
        <div class="di-kpi__lbl">Fell behind this year</div>
        <div class="di-kpi__num">{real current_fy SharePct}<small>%</small></div>
        <div class="di-kpi__sub">{real current_fy OverdueCount} schedules — recent enough that reminders and follow-up will work.</div>
      </div>
      <div class="di-kpi di-kpi--warning">
        <div class="di-kpi__lbl">Oldest overdue schedule</div>
        <div class="di-kpi__num">{real whole-years between earliest OldestDueDate and AsOfUtc}<small> yrs</small></div>
        <div class="di-kpi__sub">Originally due {real earliest OldestDueDate, DD MMM YYYY} and still open at this snapshot.</div>
      </div>
    </div>

    <p class="di-pane__narr">{2-4 real sentences: where the mass of the backlog sits (which bucket is largest, by its real SharePct); the chase-vs-decide split stated with both raw numbers (workable = current_fy OverdueCount; needs-a-decision = previous_fy + older OverdueCount); the real age of the oldest item as arithmetic; and — only if A-OLDER-AGG is present — its real headline. Never quote a tenant late-rate, never invent a 3-year count.}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - up to 3-4:
           (a) older bucket dominates - ONLY if A-OLDER-AGG is present; use its real Value / FlaggedPct
           (b) the chase-vs-decide split - always emit when previous_fy + older OverdueCount > 0
           (c) oldest-item age - the real OldestDueDate and its whole-year age, when >= 2 years
           NEVER a late-rate finding, a 3-year-bucket finding, or a per-member finding. -->
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{tone label}</span>
        <h3 class="di-findcard__title">{real headline sentence}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b class="tnum">{real value}</b></span>
        </div>
      </div>
    </div>

    <div class="di-drill">
      <div class="di-drill__head"><span class="di-drill__title">The three age groups</span></div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">Overdue schedules by age group with count, share of the overdue total, oldest due date, and what the group means</caption>
          <thead>
            <tr><th>Age group</th><th>Overdue</th><th>Share</th><th>Oldest due</th><th>What it means</th></tr>
          </thead>
          <tbody>
            <!-- EXACTLY 3 <tr>, in this order: current_fy, previous_fy, older. Every row, always,
                 even at zero. The "what it means" cell is a fixed real phrase per bucket:
                 current_fy -> "Fell behind this year. Reminders and follow-up will work."
                 previous_fy -> "Has waited a full year. Needs a decision, not another reminder."
                 older -> "Predates the previous fiscal year. Close it or write it off." -->
            <tr>
              <td>{real FYLabel, or "Before {PreviousFyLabel}" for the older row}</td>
              <td class="di-num tnum">{real OverdueCount}</td>
              <td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if this is the older row and its SharePct>=40, warning if SharePct>=20, good otherwise}" style="width:{real SharePct}%"></i></span><span class="di-meter__label">{real SharePct}%</span></td>
              <td class="di-num tnum">{real OldestDueDate, DD MMM YYYY, or "—" if OverdueCount is 0}</td>
              <td class="di-flag">{the fixed real phrase for this bucket}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="di-callout di-callout--info">
      <span class="di-callout__h">A note on the numbers</span>
      This page is a snapshot taken at one moment. The overdue count moves as people work, so it should never be charted against an earlier report.
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
carry `class="tnum"` and thousands separators; identifiers and dates never do (dates are plain).

## Self-check before returning

Reject your own draft and fix it if it: renders a tab strip, a second section, or a score donut ·
numbers the lone section · shows fewer or more than 3 rows in the drill table, or in an order
other than current_fy → previous_fy → older · quotes or derives a tenant late-rate · invents an
"older than 3 years" count · shows a per-branch/per-owner age breakdown · presents the oldest-item
age as anything other than arithmetic over `OldestDueDate` and `AsOfUtc` · right-aligns table
numbers · uses a native `<meter>` · emits `<details>`, an accordion, or any chevron · introduces a
hex/colour not in the `:root` block · uses any oklch colour or DM-Sans/Trent naming · declares
`@font-face` itself.

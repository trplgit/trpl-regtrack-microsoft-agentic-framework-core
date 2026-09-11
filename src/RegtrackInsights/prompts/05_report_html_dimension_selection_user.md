# Report Generation — DIMENSION SELECTION, USERS-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6 "Rules settled on the dimension views (2026-09-08 -
Nature, Entity)", reference implementations `reference/entity-insights-tenant29.html` and
`reference/nature-insights-tenant29.html` — real tenant-29 renders, designer-approved). This
is the OFFICIAL, superseding visual system for every single-dimension view. `.di-` prefixed,
hex colours, Poppins (400/500/600 all embedded — this system uses weight 500 for real).
`RenderHtmlActivity` resolves this via the `dimension_selection:Users` key ahead of the
generic key when exactly one dimension ("Users") is requested.

**[REPLACED 2026-09-09]** This file previously reproduced the real Angular product's own
`dimension === 'User'` tabbed sub-page (4 tabs: Overview/Priority load/Standouts/What this
means) — that shape does NOT match the approved AI-generation contract. A single-dimension
insight is ONE section, no tabs, matching `entity-insights-tenant29.html` exactly. This
rewrite keeps every honesty finding from that earlier version (see "Real vs. NOT AVAILABLE"
below, unchanged) but reshapes the document entirely.

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "Users".** If
given anything else, refuse rather than guess.

## The shape — ONE section, no tabs, no donut

topline → pastel hero (eyebrow row + verdict pill, claim headline, method line, meta chips)
→ one `<section class="di-pane">` (NO section numeral) containing: snapshot-tile KPI grid →
one narrative paragraph → a findings list → ONE drill-down table (plain, always open, no
`<details>`) → callout(s). Nothing else — no tabs, no lens toggle, no role-strip component.
**No expand/collapse anywhere** (Sambram, 2026-09-09: "an insight is a static document;
nothing is gained by hiding content behind a click") — never emit `<details>`, `<summary>`,
an accordion, or a chevron icon.

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

**Every number comes from `assertions`, the complete real `dimension_rows.Users` row array,
or `dimension_control_totals.Users` — never invented, never from `narrative` prose alone.**
Simple arithmetic over real given numbers (sums, counts, sorts) is expected; never a number
these three sources cannot support.

## What you are given, specific to this file

`dimension_rows.Users` — the COMPLETE real per-user row array: `UserID`, `UserName`,
`IsActive`, `Instances`, `PerformerInstances`, `ReviewerInstances`, `OtherRoleInstances`,
`Overdue`, `OverduePct`, `ImprisonmentInstances`, `BranchesCovered`, `Logins12m`,
`EngagementBand`, `CompletedEvents`, `OnTimeEvents`, `OnTimePct`, `Flags`.

`dimension_control_totals.Users` — the REAL `UsersControlTotals` object: `ScopedInstances`,
`AssignedInstancesDistinct`, `UnassignedInstances`, `OverdueInstances`, `TenantOverduePct`,
`UsersReported`, `SumOfPerUserInstances`, `TenantMedianOnTimePct`,
`TenantMedianPerformerLoad`, `InstancesWithSoleReviewer`.

`assertions` — the curated, top-5-capped comparative facts.

## Real vs. NOT AVAILABLE — unchanged from the earlier tabbed version, still binding

| Real page had (the earlier Angular-mirroring version) | Status |
|---|---|
| "Approvers, role 6, unused" | No RoleID 6 concept anywhere in our schema. `OtherRoleInstances` lumps EVERY role outside {3,4} together, undifferentiated — never call it "Approver". |
| Imprisonment-**overdue** lens (jailable AND late, combined) | `UsersRow` has `ImprisonmentInstances` and `Overdue` as SEPARATE fields — no combined field. Never claim a combined figure. |
| Per-user risk mix (Critical/High/Medium/Low %) | Not in `UsersRow` at all. Never invent a risk split. |
| "Heads N depts" (dept-head fan-out) | Not in this dimension's data — `BranchesCovered` is branches, a different real number. |
| "Two-person pipeline, 99.5% overlap" (a performer and a reviewer share the same instances) | No real field measures whether two specific accounts' instance sets overlap. Never claim a pairing or an overlap percentage. |
| Standout narrative detail ("100% licence-renewal batch") | Category-level detail is not in `UsersRow`. Cite only real generic fields. |

**`EngagementBand`/`OnTimePct`/`Logins12m` are real fields this dimension has that Entity's
own reference does not show** — use them in the narrative/findings where they add a real,
checkable signal (e.g. a real correlation between engagement band and overdue rate, computed
directly from the given rows).

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Users insights · Tenant {tenant id}</title>
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
      <span class="di-eyebrow">Users · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if top real user's share of SumOfPerUserInstances >= 30%, warn if 15-29.9%, ok otherwise}"><span class="di-band__dot"></span>{real band text}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence: total real UsersReported, and the top real account's real share - e.g. "{UsersReported} users carry the estate — the top account holds {share}% of it"}</h1>
    <p class="di-hero__claim">{one real sentence with <em> around key figures}: the top account is <em class="tnum">{real UserName}</em> at <em class="tnum">{real share}%</em> of all live obligations. {a second real clause only if a real, materially high imprisonment concentration or reviewer-shortage fact applies - never a pipeline/overlap claim}</p>
    <p class="di-method">The comparison below walks from the estate's role structure to each account's real load, its detector flags, and the full user table.</p>
    <div class="di-metachips">
      <span class="di-chip">Estate: {real SumOfPerUserInstances} live obligations</span>
      <span class="di-chip">Users reported: {real UsersReported}</span>
      <span class="di-chip">Reviewer cover: {real performerUserCount}:{real reviewerUserCount, as a ratio, e.g. "1:7.7" - real division of real counted rows, never rounded to a suspiciously clean number}</span>
    </div>
  </section>

  <section class="di-pane" id="di-pane-1" aria-label="Users">
    <div class="di-pane__head">
      <h2 class="di-pane__title">Users</h2>
    </div>

    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--critical">
        <div class="di-kpi__lbl">Top account share</div>
        <div class="di-kpi__num">{real share}<small>%</small></div>
        <div class="di-kpi__sub">{real UserName}; {real Instances} of {real SumOfPerUserInstances} live obligations.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">Reviewer coverage</div>
        <div class="di-kpi__num">{real reviewerUserCount}<small>/{real performerUserCount} performers</small></div>
        <div class="di-kpi__sub">{real ratio} reviewer-to-performer.</div>
      </div>
      <!-- 3rd/4th tile only when real data supports it: imprisonment concentration share (sum
           of ImprisonmentInstances across all real rows, top account's real share of it), or
           other-roles count (real OtherRoleInstances sum > 0). Never force a tile with no real
           backing. -->
    </div>

    <p class="di-pane__narr">{2-4 real sentences: role structure (real performer/reviewer counts), imprisonment concentration if materially real, engagement/overdue pattern if a real correlation exists in the given rows - never invent a pipeline claim or a risk-mix breakdown}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - up to 4-5:
           (a) reviewer:performer ratio, if real reviewerUserCount < real performerUserCount
           (b) imprisonment concentration on the top account, if its real share of the real
               tenant-wide ImprisonmentInstances sum is >= 30%
           (c) other-roles gap, if real OtherRoleInstances sum > 0 - state only that it exists
               and is undifferentiated, never guess the role
           (d) a real standout (Instances >= this run's real materiality floor AND OverduePct
               very low/zero) - "clears real volume with a real low overdue rate"
           NEVER include a pipeline/overlap finding, an Approver/role-6 finding, a risk-mix
           finding, or a dept-head finding. -->
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
        <span class="di-drill__title">All user rows — worst overdue rate first</span>
      </div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">User rows with instances, overdue instances, and overdue rate</caption>
          <thead>
            <tr><th>User</th><th>Role</th><th>Instances</th><th>Overdue</th><th>Overdue rate</th><th>Engagement</th></tr>
          </thead>
          <tbody>
            <!-- ONE <tr> per real row in dimension_rows.Users with Instances > 0, sorted
                 worst-first by OverduePct - every real row, no subset, no cap. Role column =
                 "Performer" if real PerformerInstances > ReviewerInstances, "Reviewer" if the
                 reverse, "Mixed" if equal and both > 0, "Other" if only OtherRoleInstances > 0. -->
            <tr><td>{real UserName}</td><td>{Performer|Reviewer|Mixed|Other}</td><td class="di-num tnum">{real Instances}</td><td class="di-num tnum">{real Overdue}</td><td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if >=80, warning if >=45, good otherwise}" style="width:{real OverduePct}%"></i></span><span class="di-meter__label">{real OverduePct}%</span></td><td class="di-flag">{real EngagementBand}</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- Callout only when a real, genuinely applicable case exists. -->
    <div class="di-callout di-callout--{warning|info}">
      <span class="di-callout__h">{real short label}</span>
      {real caveat text, grounded in a real control-total field or assertion - e.g. account
      identifiers are anonymized within this run, or a real reviewer-shortage note}
    </div>
  </section>
</div>
</body>
</html>
```

## CSS — copied verbatim from `reference/entity-insights-tenant29.html` (identical to the Location-specific file's own CSS block, this shape's tokens are shared across every dimension)

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
is binding: **insights** (never "report"), exact role names (**Performer, Reviewer**).
Display numbers carry `class="tnum"` and thousands separators; identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: renders a tab strip, a lens toggle, a role-strip
component, a second section, or a score donut · numbers the lone section · claims a
"two-person pipeline"/overlap between any two accounts · labels any row/card "Approver"/
"role 6" · shows a risk-mix breakdown per user · shows an imprisonment-**overdue** combined
figure · says "heads N depts" · right-aligns table numbers · uses a native `<meter>` ·
emits `<details>`, an accordion, or any chevron icon · introduces a hex/colour not in the
`:root` block · uses any oklch colour or DM-Sans/Trent naming · declares `@font-face` itself
· or leaves the drill-down table missing a real row that has `Instances > 0`.

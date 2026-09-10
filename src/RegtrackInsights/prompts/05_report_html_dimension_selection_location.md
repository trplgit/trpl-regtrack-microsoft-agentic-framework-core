# Report Generation — DIMENSION SELECTION, LOCATION-SPECIFIC TEMPLATE

**Reproduces Sambram's real, approved "dimension view" design system**
(`AI-INSIGHTS-BRAND-HANDOFF.md` Sec.6, reference implementation
`reference/location-insights-tenant29.html` — a real tenant-29 render, designer-corrected
2026-09-09). `.di-` prefixed, hex colours, Poppins (400/500/600 all embedded — this system
uses weight 500 for real). `RenderHtmlActivity` resolves this via the
`dimension_selection:Location` key ahead of the generic key when exactly one dimension
("Location") is requested.

**[REPLACED 2026-09-09, TWICE]** First replaced Trent/oklch/DM-Sans with Sambram's house
system per the Entity/Nature reference. Replaced AGAIN the same day once Sambram reviewed a
real Location run and corrected several skin decisions - see "Corrections from the real
Location run" below. `location-insights-tenant29.html` is now the most current, most
detailed reference of the three dimension-view examples (it shows two components
Entity/Nature's own references never needed: state peer-rate rows, ranked action cards).

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "Location".**
If given anything else, refuse rather than guess.

## The shape — ONE section, no tabs, no donut, NO expand/collapse anywhere

topline → pastel hero → one `<section class="di-pane">` (NO section numeral) containing, in
this order: snapshot-tile KPI grid → one narrative paragraph → a findings list → **state
peer-rate rows** (only when real peer-state data exists) → **the complete location table,
always open, no `<details>`** → **priority action cards** (when real findings support at
least one concrete action) → a closing callout. **An insight is a static document - nothing
is ever hidden behind a click.** Never emit `<details>`, `<summary>`, an accordion, or a
chevron icon anywhere in this document.

## Corrections from the real Location run (2026-09-09) — read before writing anything

A real run got the CONTENT right and several SKIN decisions wrong. Every one of these is now
settled, not a judgement call:

1. **Poppins is the only font, embedded.** The run declared DM Sans and IBM Plex Mono
   (neither self-hosted, so both rendered as the system font) and set numbers in a mono
   face. Every number is Poppins with `font-variant-numeric: tabular-nums` (class `.tnum`) -
   never a monospace font-family anywhere in this document.
2. **Hex tokens from the `:root` block below - never oklch, never a private palette.**
3. **No sticky topbar, no breadcrumbs, no "as of" chip.** The generated timestamp lives ONLY
   in the plain `.topline` at the very top of the document. Never a sticky/fixed header,
   never a separate "as of" chip with its own status colour (a date is not a status).
4. **Snapshot tiles come from real figures the narrative also states** (tenant rate, the
   worst location's rate + rank, a single-dependency count, an unconfigured-obligations
   count) - never bury these figures in prose only with no tile, and never put a tile number
   the narrative cannot also support.
5. **State peer-rate comparisons are name · meter · value ROWS** in one hairline grid
   (`.di-rows`/`.di-row`, see CSS) - never a bespoke component with a different shape, never
   a right-aligned mono label.
6. **Priority actions are the house ranked action cards** (`.di-actions`/`.di-action`) - rank
   chip is ALWAYS `#b3261e` regardless of rank number (a rank is an ordinal, not a severity -
   never a 3-colour ladder on the rank chips themselves), title, owner line (a real role name
   from CLAUDE.md's sanctioned vocabulary - **Performer, Reviewer, Compliance Officer,
   Compliance Owner** - never an invented person or team name), stat pills, and a green
   "Done when" outcome strip. Always fully visible, never collapsed.
7. **A short caveat is the blue informational callout**, never a dashed grey box.
8. **Never assert a count for one meaning as if it were a count for a different meaning** -
   a real run's prose said "99 locations" (the rankable population) while its table actually
   listed 37 rows out of a real 177 (a mismatched SUBSET, not the real complete population).
   That was the bug - not that these numbers can differ in general. **Real Location data
   legitimately has THREE different, all-correct population counts, never expected to be
   equal:**
   - **All real branches** (`dimension_rows.Location.length`, `BranchesReported`) - the
     complete table always shows every one of these.
   - **Rankable branches** (`Instances > 0`) - the SMALLER population `OverdueRank`/a
     worst-branch assertion's `of_n` is computed over. The rank badge ("Rank N of M")
     legitimately cites this smaller M, not the table's total row count - never conflate the
     two into a single number, and never force them to match.
   - **True ghost leaves** (`GhostEntities`, `Flags` containing `no_obligations_configured`)
     vs **every zero-instance row** (`BranchesWithNoObligations`) - the first is a STRICT
     SUBSET of the second (`sql/05`'s own real distinction: a true ghost leaf has zero
     instances AND zero children; a "grouping/holding" node also has zero instances but real
     children below it, and legitimately holds zero - it is not a ghost). Cite
     `BranchesWithNoObligations` for a general "no obligations configured" claim (the KPI
     tile); cite `GhostEntities`/a real `A-GHOST-AGG`-style assertion only when specifically
     calling out true ghost leaves, and never present one as if it were the other.
   The actual bug-class to avoid: citing the SAME real quantity inconsistently (e.g. writing
   "99" in the hero and "37" in the table caption when both claim to mean the exact same
   rankable population) - not citing two DIFFERENT, both-real quantities that happen to be
   different sizes.

## Output constraints

1. **Exactly one HTML document**, `<meta charset="utf-8">` first inside `<head>`.
2. **All CSS inline**, all JS inline. This document needs NO script at all - no interactivity
   of any kind (no expand/collapse, no filtering). Zero external references, no runtime
   network calls.
3. **Font:** `font-family:'Poppins',sans-serif` on `body`, and on every numeric element via
   `.tnum` (`font-variant-numeric: tabular-nums` - NOT a different font-family). Do **not**
   declare `@font-face` yourself — real vendored Poppins bytes (400/500/600) are embedded
   automatically by `PoppinsFontInjector`. Just write the family name.

## Security & Accessibility

Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
`href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour
paired with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.Location` row
array, or `dimension_control_totals.Location` — never invented, never from `narrative` prose
alone.** Simple arithmetic over real given numbers (sums, counts, sorts) is expected; never a
number these three sources cannot support, and never a count the table and the narrative
disagree on (see correction 8 above).

## What you are given, specific to this file

`dimension_rows.Location` — the COMPLETE real per-branch row array (every real `LocationRow`,
including rows with `Instances == 0`): `BranchID`, `BranchName`, `NodeType`, `RootKind`,
`ApexName`, `Instances`, `Overdue`, `Ownerless`, `ImprisonmentInstances`, `CriticalInstances`,
`DistinctPerformers`, `DistinctReviewers`, `ClosureEventsLifetime`, `ActiveChildren`,
`OverduePct`, `OwnerlessPct`, `ClosureRatio`, `OverdueRank`, `StateID`, `StateName`,
`PeerStateOverduePct`, `VsPeerStateNormPP`, `Flags`.

`dimension_control_totals.Location` — the REAL `LocationControlTotals` object:
`ScopedInstances`, `SumOfRows`, `OverdueInstances`, `TenantOverduePct`, `BranchesReported`,
`ActiveBranchesInTenant`, `BranchesWithNoObligations`, `GhostEntities`,
`TenantMedianClosureRatio`, `TenantIsOnboarding`, `TenantCompletedEvents`,
`TenantOnTimeEvents`, `TenantOnTimePct`.

`assertions` — the curated, top-5-capped comparative facts.

**`Flags`** is the real detector-tag field — the ONLY 5 real detector types that can ever
appear: `onboarding_artifact`, `ghost_entity`, `single_point_of_failure`, `high_ownerless`,
`peer_coverage_gap`. "Single-dependency" (a real branch that depends on one performer or
reviewer) = `Flags` containing `single_point_of_failure`, or directly
`DistinctPerformers == 1 || DistinctReviewers == 1` on a row with `Instances > 0` - use
whichever the real `Flags`/detector output already gives you, never re-derive a different
threshold yourself.

## Real vs. NOT AVAILABLE

- **State/peer comparison fields** (`StateName`, `PeerStateOverduePct`, `VsPeerStateNormPP`)
  are real but **not every row has them populated**. Only build a state peer-rate row for a
  state where at least one real row carries a non-null `PeerStateOverduePct`. A state with
  branches but no real peer-rate basis goes in a `.di-callout--info` "Too small to rank
  fairly" note instead (see document shape below) - never invent a rate for it.
- **`ApexName`/`RootKind`** are real hierarchy facts — never invent what the entity IS beyond
  its real name.
- **Action-card owners** are real ROLE names only (CLAUDE.md's sanctioned vocabulary:
  Performer, Reviewer, Compliance Officer, Compliance Owner) — never a specific person's name
  (none is given to you) and never an invented team/department name.

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Location insights · Tenant {tenant id}</title>
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
      <span class="di-eyebrow">Location · Tenant {tenant id}</span>
      <span class="di-band di-band--{bad if worst branch's real OverduePct is >=30pp above TenantOverduePct, warn if 10-29.9pp}"><span class="di-band__dot"></span>{real band text, e.g. "Action required"}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence: the worst real branch by OverduePct, and the real rankable-population count (rows with Instances > 0) - the same M a worst-branch assertion's own of_n gives you, NOT the table's total row count (the table separately shows every real row, rankable or not)}</h1>
    <p class="di-hero__claim">{real branch name} is <em class="tnum">{real OverduePct}%</em> overdue — <em class="tnum">{real pp difference}pp</em> above the tenant rate of {real TenantOverduePct}%. {a second real clause only if a real tenant-wide pattern (single-dependency share, unassigned-ownership share) genuinely applies - never invented}</p>
    <p class="di-method">The comparison below walks from the tenant overdue rate to the worst location, state peer rates and the full location table.</p>
    <div class="di-metachips">
      <span class="di-chip">Tenant overdue rate: {real TenantOverduePct}%</span>
    </div>
  </section>

  <section class="di-pane" aria-label="Location">
    <div class="di-pane__head">
      <h2 class="di-pane__title">Location</h2>
    </div>

    <div class="di-kpigrid">
      <div class="di-kpi">
        <div class="di-kpi__lbl">Tenant overdue rate</div>
        <div class="di-kpi__num tnum">{real TenantOverduePct}<small>%</small></div>
        <div class="di-kpi__sub">Tenant-scoped overdue rate.</div>
      </div>
      <div class="di-kpi di-kpi--critical">
        <div class="di-kpi__lbl">{real worst branch name} overdue rate</div>
        <div class="di-kpi__num tnum">{real OverduePct}<small>%</small></div>
        <div class="di-kpi__sub">Rank {real OverdueRank} of {real rankable-population count - same M as the hero above, from the worst-branch assertion's own of_n, NOT the table's total row count}; {real pp} percentage points above the tenant rate.</div>
      </div>
      <div class="di-kpi di-kpi--warning">
        <div class="di-kpi__lbl">Single-dependency locations</div>
        <div class="di-kpi__num tnum">{real count}<small> of {real N}</small></div>
        <div class="di-kpi__sub">{real pct}% depend on a single performer or reviewer.</div>
      </div>
      <div class="di-kpi">
        <div class="di-kpi__lbl">No obligations configured</div>
        <div class="di-kpi__num tnum">{real BranchesWithNoObligations}<small> of {real ActiveBranchesInTenant}</small></div>
        <div class="di-kpi__sub">{real pct}%; likely a location master — confirm before treating as a gap.</div>
      </div>
    </div>

    <p class="di-pane__narr">{2-4 real sentences: the worst branch's peer-state comparison if it has one, the tenant-wide single-dependency pattern, the tenant-wide unassigned-ownership pattern if real, and the no-obligations pattern - every figure real, every count matching the table below}</p>

    <div class="di-findlist">
      <!-- 1 finding card per real, materially notable pattern - typically just the worst
           branch, plus any real detector-flagged group. Never force a fixed count. -->
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{tone label}</span>
        <h3 class="di-findcard__title">{real headline sentence}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b class="tnum">{real value}</b></span>
        </div>
      </div>
    </div>

    <!-- State peer-rate rows - ONLY when 2+ real rows carry a non-null PeerStateOverduePct.
         Omit this whole block entirely (no header, no rows, no callout) if fewer than 2 real
         states qualify - never force it. -->
    <div class="di-block">
      <h3 class="di-block__title">State peer rates<small>worst first</small></h3>
      <div class="di-rows">
        <!-- ONE di-row per real state with a non-null PeerStateOverduePct on at least one
             row, worst-first by that real rate. -->
        <div class="di-row"><span class="di-row__name">{real StateName}</span><div class="di-row__meter"><i class="di-meter__fill di-meter__fill--{critical if >=80, warning if >=45, good otherwise}" style="width:{real PeerStateOverduePct}%"></i></div><span class="di-row__val tnum">{real PeerStateOverduePct}%</span></div>
      </div>
      <!-- Only if at least one real state has branches but no real peer-rate basis: -->
      <div class="di-callout di-callout--info"><span class="di-callout__h">Too small to rank fairly</span>{real state name(s)} {has|have} a location in the data but no available peer-state overdue percentage.</div>
    </div>

    <!-- The complete location table - EVERY real row in dimension_rows.Location, including
         rows with Instances == 0 (matches the real reference's own caption: "Complete
         location comparison including locations with no configured obligations"). No
         <details>, no chevron, no click-to-expand - a plain, always-visible card. -->
    <div class="di-drill">
      <div class="di-drill__head">
        <span class="di-drill__title">Every location — worst first by overdue rate</span>
      </div>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">Complete location comparison including locations with no configured obligations.</caption>
          <thead><tr><th>Location</th><th>State</th><th>Overdue / instances</th><th>Overdue rate</th></tr></thead>
          <tbody>
            <!-- ONE <tr> per real row, sorted worst-first by OverduePct (rows with
                 Instances == 0 sort last, at 0%) - every real row, no subset, no cap. -->
            <tr><td>{real BranchName}</td><td>{real StateName, or empty if null}</td><td class="di-num tnum">{real Overdue} / {real Instances}</td><td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{critical if >=80, warning if >=45, good otherwise, empty class for a 0/0 row}" style="width:{real OverduePct or 0}%"></i></span><span class="di-meter__label">{real OverduePct or 0.00}%</span></td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- Priority actions - only when real findings/tiles above genuinely imply a concrete
         next step (typically: the worst branch's own overdue backlog, and/or the
         no-obligations-configured confirmation). Omit entirely if nothing real is
         actionable - never force a card. Rank chips are ALWAYS #b3261e (see CSS), never a
         severity ladder. -->
    <div class="di-block">
      <h3 class="di-block__title">Priority actions<small>in priority order</small></h3>
      <div class="di-actions">
        <article class="di-action">
          <div class="di-action__head">
            <span class="di-action__rank" aria-hidden="true">01</span>
            <span class="di-action__main"><span class="di-action__what">{real, concrete action tied to a real finding above}</span><span class="di-action__owner">Owner: {real sanctioned role name}</span></span>
          </div>
          <div class="di-action__body">
            <div class="di-qstats"><span class="di-qstat">{real stat} <b class="tnum">{real value}</b></span></div>
            <div class="di-action__outcome"><b>Done when:</b> {a real, checkable measure of completion - never vague}</div>
          </div>
        </article>
      </div>
    </div>

    <div class="di-callout di-callout--info">
      <span class="di-callout__h">Comparison basis</span>
      {real caveat text - e.g. peer comparisons are within-tenant only, no-obligations locations are a structural pattern to confirm, never invented}
    </div>
  </section>
</div>
</body>
</html>
```

## CSS — copied verbatim from `reference/location-insights-tenant29.html` (do not re-derive)

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
.di-rows{display:grid;grid-template-columns:1fr;gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden;margin-bottom:var(--gap-lg)}
.di-row{background:var(--c-surface);display:grid;grid-template-columns:150px minmax(0,1fr) 88px;align-items:center;gap:12px;padding:9px 14px}
.di-row__name{font-size:var(--fs-prose);font-weight:500;color:var(--c-text)}
.di-row__meter{height:6px;border-radius:999px;background:#eef1f7;overflow:hidden}
.di-row__meter i{display:block;height:100%;border-radius:999px;background:var(--neu-fill)}
.di-row__meter i.di-meter__fill--critical{background:var(--bad-fill)}.di-row__meter i.di-meter__fill--warning{background:var(--warn-fill)}.di-row__meter i.di-meter__fill--good{background:var(--ok-fill)}
.di-row__val{font-size:var(--fs-meta);color:var(--c-text-3);text-align:left}
.di-block{margin-bottom:var(--gap-lg)}
.di-block__title{margin:0 0 .55rem;font-size:var(--fs-headline);font-weight:600;color:var(--c-text)}
.di-block__title small{font-weight:400;color:var(--c-grey);font-size:var(--fs-meta);margin-left:6px}
.di-actions{display:flex;flex-direction:column;gap:.7rem;margin-bottom:var(--gap-lg)}
.di-action{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);overflow:hidden;box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-action__head{display:grid;grid-template-columns:auto 1fr;gap:12px;align-items:flex-start;padding:14px 16px;border-bottom:1px solid var(--c-border)}
.di-action__rank{width:2rem;height:2rem;border-radius:var(--r-md);display:flex;align-items:center;justify-content:center;font-weight:600;font-size:.8rem;color:#fff;background:#b3261e;font-variant-numeric:tabular-nums;flex-shrink:0}
.di-action__main{display:flex;flex-direction:column;gap:4px;min-width:0}
.di-action__what{font-size:var(--fs-headline);font-weight:600;line-height:1.4;color:var(--c-text)}
.di-action__owner{font-size:var(--fs-meta);color:var(--c-text-3)}
.di-action__body{padding:12px 16px 14px calc(16px + 2rem + 12px);display:flex;flex-direction:column;gap:.55rem}
.di-action__outcome{display:block;padding:8px 12px;border-radius:var(--r-md);background:#e7f5ec;border:1px solid #a8d3b8;color:#2a6b42;font-size:var(--fs-prose);line-height:1.5}
.di-action__outcome b{font-weight:600}
@media (max-width:52rem){.page{padding:0 12px 20px}.di-hero{padding:14px 16px}}
```

## Voice

Same restrained, evidence-only voice as every other render prompt in this repo. Vocabulary
is binding: **insights** (never "report"), **Entity** (never "Organisation"), exact role
names where used (**Performer, Reviewer, Compliance Officer, Compliance Owner**). Display
numbers carry `class="tnum"` and thousands separators; identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: emits `<details>`, `<summary>`, an accordion, or any
chevron icon anywhere · renders a tab strip, a second section, or a score donut · numbers the
lone section · sets numbers in a monospace font-family instead of `.tnum` · declares DM Sans,
IBM Plex Mono, or any font besides Poppins · adds a sticky topbar, breadcrumbs, or a separate
"as of" chip · uses a colour ladder on action-card rank chips (always `#b3261e`) · invents an
action-card owner that is not one of the four sanctioned role names · shows a state peer-rate
row for a state with no real `PeerStateOverduePct` on any row · omits a real state from the
"too small to rank fairly" callout when it has branches but no real peer-rate basis · excludes
a real zero-instance row from the location table · uses the table's total row count where a
real assertion's own rankable-population `of_n` belongs (or the reverse) · presents
`GhostEntities` and `BranchesWithNoObligations` as if they were the same count (they are not
- the first is a real strict subset of the second, see the correction above) · writes the
same real quantity as two different numbers in two places · restates the hero's own sentence
at the start of the narrative paragraph · right-aligns table numbers · introduces a
hex/colour not in the `:root` block · uses any oklch colour or DM-Sans/Trent naming.

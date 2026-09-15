# Report Generation — DIMENSION SELECTION, USERS-SPECIFIC TEMPLATE

**[REWRITTEN 2026-09-12] Reproduces the REAL, LIVE product's own `dimension === 'User'`
view** — `detailed-insights.component.html` lines 516-728 / `.css` lines 1580-1936 in the
real Angular repo (`D:\trpl-regtrack-angular-web\modules\compliance\reginsights\detailed\`),
confirmed live against `democomplianceapp.teamleaseregtech.com/insights` -> Holistic Insights
-> breakdown "User". This is the SAME `.di-*` design system as `05_report_html_fixed_holistic.md`
(same hero/donut/verdict/kpi/pane classes, same tab mechanism) — not a separate visual
language, because it never was one in the real product. It shares its hero+role-strip shape
with the fixed_holistic composite score hero almost verbatim.

**[SUPERSEDES the 2026-09-09 rewrite]** That version deliberately replaced this exact real
tabbed page with a single flat section, reasoning "that shape does NOT match the approved
AI-generation contract." Product direction reversed 2026-09-12: match the real page
byte-identical wherever the data is real: honesty findings from that version (below) still
apply UNCHANGED — this reverses the *shape* decision only, never the *never-fabricate* rule.

**Only ever invoked when `composition_plan.blocks` has exactly one entry named "Users".** If
given anything else, refuse rather than guess.

## The shape — REAL 4-tab page, not one flat section

**[2026-09-13] Completion timing (median days early/late) does NOT get its own tab.** A 5th
tab was built, gated, and tested, then dropped the same day - the numbers now live inline on
each Priority-load row instead, replacing the overdue-rate chip (see the PRIORITY LOAD section
below). Not in the real product's own mock either way - a genuinely new capability, added once
real `ScheduleOn`/`Dated` data was confirmed to exist (`sql/12_dimension_users.sql`).

topline → `.di-hero` (donut+identity | role-strip, exactly like the fixed_holistic composite
score hero) → sticky 4-tab nav (**Overview / Priority load / Standouts / What this means**)
→ four `.di-pane` sections, one per tab, each shown/hidden by the SAME CSS-only radio+`:has()`
mechanism `05_report_html_fixed_holistic.md` already declares (see that file's own "Tab
navigation" section — reuse the identical technique, just 4 radios/panes instead of 6, ids
`di-tab-1`..`di-tab-4`). **No `<details>`/`<summary>`/accordion anywhere** (Sambram,
2026-09-09: "an insight is a static document; nothing is gained by hiding content behind a
click") — the real page uses `(click)`-bound Angular tabs; a static document uses the
radio/label technique instead, same visual result, zero JS dependency.

## Output constraints

1. **Exactly one HTML document**, `<meta charset="utf-8">` first inside `<head>`.
2. **All CSS inline.** Inline `<script>` is allowed for the SAME reason
   `05_report_html_fixed_holistic.md` allows it (tab-switching stays CSS-only regardless,
   since it needs no real data to drive it) — zero external references, no runtime network
   calls, no fetch/XHR/WebSocket.
3. **Font:** `font-family:'Poppins',sans-serif` on `body`. Do **not** declare `@font-face`
   yourself — real vendored Poppins bytes (400/500/600) are embedded automatically by
   `PoppinsFontInjector`. Just write the family name.

## Security & Accessibility

Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
`href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>` where a table is used, ordered
headings, colour paired with a text label.

## The one rule that matters most here

**Every number comes from `assertions`, the complete real `dimension_rows.Users` row array,
or `dimension_control_totals.Users` — never invented, never from `narrative` prose alone.**
Simple arithmetic over real given numbers (sums, counts, sorts, shares) is expected; never a
number these three sources cannot support.

## What you are given, specific to this file

`dimension_rows.Users` — the COMPLETE real per-user row array: `UserID`, `UserName`,
`IsActive`, `Instances`, `PerformerInstances`, `ReviewerInstances`, `OtherRoleInstances`,
`Overdue`, `OverduePct`, `ImprisonmentInstances`, `BranchesCovered`, `Logins12m`,
`EngagementBand`, `CompletedEvents`, `OnTimeEvents`, `OnTimePct`,
`MedianDaysEarlyLate` **[ADDED 2026-09-13]** (real median of due-date-to-completion-date gap in
days, performer's own completed work only — negative = typically early, positive = typically
late — `NULL`, never `0`, if the user has no qualifying completed event),
`TimingSampleSize` **[ADDED 2026-09-13]** (how many completed events that median is drawn from
— `NULL`/absent means no reading, never treat as 0), `Flags`.

`dimension_control_totals.Users` — the REAL `UsersControlTotals` object: `ScopedInstances`,
`AssignedInstancesDistinct`, `UnassignedInstances`, `OverdueInstances`, `TenantOverduePct`,
`UsersReported`, `SumOfPerUserInstances`, `TenantMedianOnTimePct`,
`TenantMedianPerformerLoad`, `InstancesWithSoleReviewer`,
`PerformerUserCount` **[ADDED 2026-09-15]** (real distinct headcount of users with at least one
Performer-role assignment - pre-computed in C#, never count `dimension_rows.Users` rows yourself,
you cannot do this reliably over 300+ rows),
`ReviewerUserCount` **[ADDED 2026-09-15]** (same, Reviewer-role),
`TenantMedianDaysEarlyLate` **[ADDED 2026-09-13]** (tenant-wide version of the same median -
not currently rendered anywhere in this template, kept available for narrative/assertion use),
`TimingOutliersExcluded` **[ADDED 2026-09-13]** (count of completed events tenant-wide excluded
from every median above for showing an implausible >365-day gap — bulk-migration/backdated-
schedule artifacts, confirmed live on tenant 1300: 1,171 of 42,828 events, 2.7% — not currently
rendered here either; the per-row badge below never needs it since excluded events are already
out of every `MedianDaysEarlyLate` it reads).

`assertions` — the curated, top-5-capped comparative facts.

## Real vs. NOT AVAILABLE — unchanged from the 2026-09-09 version, still binding

The real page's mock data invents a few things `UsersRow` cannot support. Where the real
page shows one of these, the shape below tells you exactly what to omit or substitute —
**never fabricate the field just because the real page's mock has it.**

| Real page's mock has | Status | What this template does instead |
|---|---|---|
| "Approvers, role 6, unused" | No RoleID 6 concept anywhere in our schema. `OtherRoleInstances` lumps EVERY role outside {3,4} together, undifferentiated. | Role strip's 3rd card is labelled "Other roles" (never "Approver"), real `OtherRoleInstances` sum — omit the card entirely if that sum is 0. |
| Imprisonment-**overdue** combined lens | `UsersRow` has `ImprisonmentInstances` and `Overdue` as SEPARATE fields — no combined field (confirmed 2026-09-13: `sql/12_dimension_users.sql` carries them as separate columns, no `ImprisonmentOverdue`-style joint column like Risk/Departments have). Estimating the joint count from the two independent marginals (`ImprisonmentInstances x OverduePct`) would assume independence that may not hold — a fabricated joint number wearing a real-looking decimal, not a verified one. | Priority-load lens toggle **[NOW BUILT, was previously omitted entirely — see below]** shows only **2 real, working lens buttons** (Overdue items, Tenant share) — never a 3rd "Imprisonment-overdue" button. Product decision 2026-09-13: ship 2 honest lenses rather than 3 where one is estimated; revisit if `sql/12` ever grows a real joint column. |
| Per-user risk mix (Critical/High/Medium/Low %) bar | Not in `UsersRow` at all. | The `.ur-pu-mix` risk bar is **replaced** with a real on-time-vs-overdue split bar (`OnTimePct`/`OverduePct` of that user's own `Instances`) — same visual slot, real data. |
| "Heads N depts" (dept-head fan-out) | Not in this dimension's data — `BranchesCovered` is branches, a different real number. | Replace with real `BranchesCovered` ("covers N branches") wherever the mock shows "heads N depts". |
| "Two-person pipeline, 99.5% overlap" | No real field measures whether two specific accounts' instance sets overlap. | Never claim a pairing or an overlap percentage, anywhere. |
| Standout narrative detail ("100% licence-renewal batch") | Category-level detail is not in `UsersRow`. | Cite only real generic fields in standout credit lines. |

**`EngagementBand`/`OnTimePct`/`Logins12m` are real fields this dimension has that the real
page's mock does not use** — use them in the on-time-split bar above and in findings where
they add a real, checkable signal.

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Users insights · {real company name}</title>
  <style>/* :root tokens + every class below - verbatim, see CSS section */</style>
</head>
<body>
<div class="page">
  <div class="topline">
    <span class="topline__meta">Generated <span class="tnum">{generated_at, formatted DD MMM YYYY, HH:MM UTC}</span></span>
  </div>

  <section class="di-hero">
    <div class="di-hero__inner ur-hero__inner">
      <!-- concentration donut + identity - EXACTLY the fixed_holistic composite-score hero's
           own di-scoreblock/di-donut/di-id shape, just a different real number in the ring -->
      <div class="di-scoreblock ur-scoreblock">
        <div class="di-donut di-donut--{bad if top account's real share of SumOfPerUserInstances >= 30%, warn if 15-29.9%, ok otherwise} ur-donut" aria-hidden="true">
          <svg viewBox="0 0 120 120">
            <circle class="di-donut__track" cx="60" cy="60" r="52" fill="none" stroke-width="10"></circle>
            <circle class="di-donut__arc" cx="60" cy="60" r="52" fill="none" stroke-width="10" stroke-linecap="round" style="stroke-dasharray:{real top-account share/100 * 326.7} 326.7"></circle>
          </svg>
          <div class="di-donut__ctr">
            <div class="di-donut__num tnum">{real top-account share, one decimal}<span class="ur-donut__pct">%</span></div>
            <div class="ur-donut__of">of all work on the top account</div>
          </div>
        </div>
        <div class="di-id">
          <div class="di-eyebrow">Users · {real company name}</div>
          <h1 class="di-title">{one real sentence: total real UsersReported, and the top real account's real share}</h1>
          <p class="di-sub">{one real sentence with the top real UserName and real share%; a second real clause only if a real, materially high imprisonment concentration or reviewer-shortage fact applies - never a pipeline/overlap claim}</p>
          <div class="di-meta">
            <span class="di-chip">Estate: {real SumOfPerUserInstances} live obligations</span>
            <span class="di-chip">Users reported: {real UsersReported}</span>
            <span class="di-chip mono">Reviewer cover {real PerformerUserCount}:{real ReviewerUserCount} — cite BOTH integers verbatim from dimension_control_totals.Users, never a computed/reduced ratio</span>
          </div>
        </div>
      </div>

      <div class="di-verdict">
        <div class="di-verdict__row">
          <span class="di-band di-band--{bad|warn|ok, same threshold as the donut above}"><span class="di-band__dot"></span>{real band text}</span>
        </div>
        <p class="di-headline">{one real sentence, key figures in <em class="tnum">}</p>
        <p class="di-method">The comparison below walks from the estate's role structure to each account's real load, its detector flags, and the full priority-load ranking.</p>
      </div>
    </div>

    <!-- role strip - reuses di-components exactly, real counts only -->
    <div class="di-components ur-roles">
      <div class="di-components__head">
        <span class="di-components__label">Role structure across {real UsersReported} users</span>
        <span class="ur-roles__note">Reviewer-to-performer ratio {real ratio}</span>
      </div>
      <div class="ur-rolesgrid">
        <!-- up to 3 cards: Performer (real performerUserCount, real sum of their Instances),
             Reviewer (real reviewerUserCount, real sum), Other roles (real count with
             OtherRoleInstances > 0 - OMIT this 3rd card entirely if that count is 0, never a
             "0 other roles" card) -->
        <div class="ur-role ur-role--{perf|rev|unused}">
          <div class="ur-role__top"><span class="ur-role__dot"></span><span class="ur-role__name">{Performer|Reviewer|Other roles}</span><span class="ur-role__note">{real sub-detail}</span></div>
          <div class="ur-role__num tnum">{real count}</div>
          <div class="ur-role__sub">{real one-line detail}</div>
        </div>
      </div>
    </div>
  </section>

  <div class="di-tabsroot">
    <input type="radio" name="di-tab" id="di-tab-1" class="di-tab-input" checked>
    <input type="radio" name="di-tab" id="di-tab-2" class="di-tab-input">
    <input type="radio" name="di-tab" id="di-tab-3" class="di-tab-input">
    <input type="radio" name="di-tab" id="di-tab-4" class="di-tab-input">
    <div class="di-stickytabs">
      <nav class="di-tabnav" role="tablist" aria-label="User and role sections">
        <label for="di-tab-1" class="di-tab" role="tab">Overview</label>
        <label for="di-tab-2" class="di-tab" role="tab">Priority load<span class="di-tab__count tnum">{real row count}</span></label>
        <label for="di-tab-3" class="di-tab" role="tab">Standouts<span class="di-tab__count tnum">{real standout count}</span></label>
        <label for="di-tab-4" class="di-tab" role="tab">What this means<span class="di-tab__count tnum">{real finding count}</span></label>
      </nav>
    </div>

    <!-- ---- OVERVIEW · the estate at a glance ---- -->
    <section class="di-pane" id="di-pane-1" aria-label="Overview">
      <div class="di-pane__head">
        <h2 class="di-pane__title">The estate at a glance</h2>
        <span class="di-pane__hint">who carries the work, and how concentrated it is</span>
      </div>
      <div class="di-kpigrid">
        <article class="di-kpi di-kpi--span3">
          <div class="di-kpi__eyebrow">Users in a compliance role</div>
          <div class="di-kpi__big"><div class="di-kpi__num tnum">{real UsersReported}</div></div>
          <p class="di-kpi__narr">{real one-sentence load-split summary}</p>
        </article>
        <article class="di-kpi di-kpi--span6">
          <div class="di-kpi__head">
            <div class="di-kpi__headtext"><div class="di-kpi__eyebrow">Imprisonment-bearing work</div></div>
            <span class="di-kpi__tag di-kpi__tag--bad"><span class="di-kpi__dot"></span>{real severity label}</span>
          </div>
          <div class="ur-ratiobar"><i class="ur-ratiobar__seg ur-sw--crit" style="width:{real top account's share of total ImprisonmentInstances}%"></i></div>
          <div class="ur-ratiolegend">
            <span class="ur-ratiolegend__k"><i class="ur-sw ur-sw--crit"></i>{real top UserName} <b>{real ImprisonmentInstances}</b> · {real share}%</span>
            <span class="ur-ratiolegend__k"><i class="ur-sw ur-sw--neu"></i>Everyone else <b>{real remainder}</b></span>
          </div>
          <p class="di-kpi__narr">{real one-sentence interpretation}</p>
        </article>
        <article class="di-kpi di-kpi--span3">
          <div class="di-kpi__eyebrow">Reviewer coverage</div>
          <div class="di-kpi__big"><div class="di-kpi__num tnum">{real ratio}</div><div class="di-kpi__unit">reviewer : performer</div></div>
          <div class="ur-ratiobar"><i class="ur-ratiobar__seg ur-sw--rev" style="width:{real reviewer share}%"></i><i class="ur-ratiobar__seg ur-sw--perf" style="width:{100 minus that}%"></i></div>
          <p class="di-kpi__narr">{real one-sentence interpretation}</p>
        </article>
      </div>
    </section>

    <!-- ---- PRIORITY LOAD · lens-driven leaderboard ---- -->
    <section class="di-pane" id="di-pane-2" aria-label="Priority load">
      <div class="di-pane__head">
        <h2 class="di-pane__title">Who carries the priority load</h2>
        <span class="di-pane__hint">re-rank the same users by what matters to you</span>
      </div>
      <!-- [FIX 2026-09-13] Previously rendered ONE static ranking only, reasoning "the real
           page's live toggle needs JS state this document does not have" - that reasoning was
           wrong. The 4-tab nav elsewhere on this exact document already proves a CSS-only
           radio+:has() toggle needs zero JS; the SAME technique drives this lens toggle. Two
           real lenses (never 3 - see honesty table above), so two hidden radios and two
           parallel row lists, shown/hidden together with the matching threshold/question text. -->
      <!-- [FIX 2026-09-15] The two radios MUST be inside .ur-lensroot, not siblings before it -
           a real render placed them before the div opened (matching an earlier, wrong version of
           this exact snippet) and every :has() rule below silently did nothing, because :has()
           only sees DESCENDANTS of the element it's applied to, never preceding siblings. This
           broke the whole toggle in production - confirmed live, both lens buttons rendered but
           neither actually switched anything. -->
      <div class="ur-lensroot">
        <input type="radio" name="ur-lens" id="ur-lens-1" class="ur-lens-radio" checked hidden>
        <input type="radio" name="ur-lens" id="ur-lens-2" class="ur-lens-radio" hidden>
        <div class="ur-lensbar">
          <span class="ur-lensbar__sort">Sort by</span>
          <div class="ur-lenstoggle" role="tablist" aria-label="Priority ranking lens">
            <label for="ur-lens-1" class="ur-lensbtn">Overdue items</label>
            <label for="ur-lens-2" class="ur-lensbtn">Tenant share</label>
          </div>
          <span class="ur-lensthr ur-lensthr-1 tnum">Critical &ge; 5,000 &middot; High &ge; 500 &middot; Medium &ge; 100</span>
          <span class="ur-lensthr ur-lensthr-2 tnum">Critical &ge; 20% &middot; High &ge; 5% &middot; Medium &ge; 1%</span>
        </div>
        <p class="ur-lensq ur-lensq-1">Who is sitting on the most overdue work right now?</p>
        <p class="ur-lensq ur-lensq-2">If this person vanished tomorrow, how much of the estate goes unowned?</p>

        <div class="ur-pulist ur-pulist-1">
          <!-- LENS 1 - Overdue items. ONE row per real UsersRow with Instances > 0, sorted
               worst-first by real Overdue (the raw count field, NOT OverduePct - this lens
               ranks by volume of overdue work, matching the real product's own by_overdue_count
               lens), capped at the top 10 by materiality (never all rows - this is a
               leaderboard; show fewer if fewer than 10 real rows qualify).
               Band thresholds are the real product's own literal thresholds for this lens
               (UR_LENSES[0].thr), applied to our real Overdue count:
               critical_attention if Overdue>=5000, high if >=500, medium if >=100, watch
               otherwise. -->
          <div class="ur-puser">
            <div class="ur-pu-id">
              <div class="ur-pu-acct">{real UserName}<span class="ur-rolechip ur-rolechip--{Performer|Reviewer}">{Performer|Reviewer}</span></div>
              <div class="ur-pu-sub"><span>{real share}% of tenant work</span><span class="ur-pu-heads">· covers {real BranchesCovered} branches</span></div>
            </div>
            <div class="ur-pu-metric ur-band--{critical_attention if Overdue>=5000, high if >=500, medium if >=100, watch otherwise}">
              <div class="ur-mnum tnum">{real Overdue}</div>
              <div class="ur-mcap">overdue items</div>
              <div class="ur-mband">{Critical|High|Medium|Watch}</div>
            </div>
            <div class="ur-pu-mix">
              <!-- REPLACES the real page's fabricated risk-mix bar (not in our data) with a real
                   on-time vs overdue split of this user's own Instances - same visual slot -->
              <!-- [FIX 2026-09-14] A real render used two different labels for this same
                   element across different rows in the SAME document ("On-time completion rate"
                   on some rows, "Current vs overdue split" on others) - never invent a variant,
                   this exact literal string, every row, every lens, no exceptions. -->
              <div class="ur-mixlabel">On-time vs overdue split</div>
              <div class="ur-riskbar"><i class="ur-riskbar__seg ur-sw--low" style="width:{real OnTimePct}%"></i><i class="ur-riskbar__seg ur-sw--crit" style="width:{100 minus real OnTimePct}%"></i></div>
              <div class="ur-risklegend">
                <span class="ur-risklegend__k"><i class="ur-sw ur-sw--low"></i>On time {real OnTimePct}%</span>
                <span class="ur-risklegend__k"><i class="ur-sw ur-sw--crit"></i>Overdue {real OverduePct}%</span>
              </div>
              <!-- [FIX 2026-09-14, REPLACES the ur-timingbadge tag that used to sit in ur-pu-load]
                   A plain sentence, not a terse tag - reads as a real interpretation, not a stat
                   someone has to decode. Real MedianDaysEarlyLate, sign gives direction. Never
                   shown for a user whose real TimingSampleSize is null or below 5 (not enough
                   completed events for a pattern - omit the line entirely, never a placeholder). -->
              <p class="ur-timingline ur-timingline--{late if MedianDaysEarlyLate>0, early if <0, ontime if ==0}" style="{display:none if TimingSampleSize is null or TimingSampleSize < 5}">Typically finishes {real |MedianDaysEarlyLate|} days {before if negative, after if positive} the deadline, on average{" - right on the deadline, on average" replacing everything from "Typically" onward if MedianDaysEarlyLate is exactly 0}.</p>
            </div>
            <div class="ur-pu-load">
              <div class="ur-vol tnum">{real Instances}<small> items</small></div>
              <!-- [FIX 2026-09-14] The days-early/late figure moved to a plain-language sentence
                   in .ur-pu-mix above (ur-timingline) - no tag/badge here any more. -->
              <span class="ur-imp" style="{display:none if real ImprisonmentInstances is 0}">{real ImprisonmentInstances as a % of this user's own Instances} jailable</span>
            </div>
          </div>
          <!-- repeat: one .ur-puser per qualifying row, sorted by real Overdue descending -->
        </div>

        <div class="ur-pulist ur-pulist-2">
          <!-- LENS 2 - Tenant share. The SAME row set as lens 1 (same materiality cap), just
               re-sorted worst-first by real share (Instances / SumOfPerUserInstances * 100).
               Band thresholds are again the real product's own literal thresholds for this
               lens (UR_LENSES[2].thr): critical_attention if share>=20, high if >=5,
               medium if >=1, watch otherwise. ur-pu-id/ur-pu-mix/ur-pu-load stay identical in
               shape to lens 1 above - only ur-pu-metric's number/caption/band change. -->
          <div class="ur-puser">
            <div class="ur-pu-id">
              <div class="ur-pu-acct">{real UserName}<span class="ur-rolechip ur-rolechip--{Performer|Reviewer}">{Performer|Reviewer}</span></div>
              <div class="ur-pu-sub"><span>{real share}% of tenant work</span><span class="ur-pu-heads">· covers {real BranchesCovered} branches</span></div>
            </div>
            <div class="ur-pu-metric ur-band--{critical_attention if share>=20, high if >=5, medium if >=1, watch otherwise}">
              <div class="ur-mnum tnum">{real share}%</div>
              <div class="ur-mcap">of all tenant work</div>
              <div class="ur-mband">{Critical|High|Medium|Watch}</div>
            </div>
            <div class="ur-pu-mix">
              <!-- [FIX 2026-09-14] A real render used two different labels for this same
                   element across different rows in the SAME document ("On-time completion rate"
                   on some rows, "Current vs overdue split" on others) - never invent a variant,
                   this exact literal string, every row, every lens, no exceptions. -->
              <div class="ur-mixlabel">On-time vs overdue split</div>
              <div class="ur-riskbar"><i class="ur-riskbar__seg ur-sw--low" style="width:{real OnTimePct}%"></i><i class="ur-riskbar__seg ur-sw--crit" style="width:{100 minus real OnTimePct}%"></i></div>
              <div class="ur-risklegend">
                <span class="ur-risklegend__k"><i class="ur-sw ur-sw--low"></i>On time {real OnTimePct}%</span>
                <span class="ur-risklegend__k"><i class="ur-sw ur-sw--crit"></i>Overdue {real OverduePct}%</span>
              </div>
              <!-- [FIX 2026-09-14, REPLACES the ur-timingbadge tag that used to sit in ur-pu-load]
                   A plain sentence, not a terse tag - reads as a real interpretation, not a stat
                   someone has to decode. Real MedianDaysEarlyLate, sign gives direction. Never
                   shown for a user whose real TimingSampleSize is null or below 5 (not enough
                   completed events for a pattern - omit the line entirely, never a placeholder). -->
              <p class="ur-timingline ur-timingline--{late if MedianDaysEarlyLate>0, early if <0, ontime if ==0}" style="{display:none if TimingSampleSize is null or TimingSampleSize < 5}">Typically finishes {real |MedianDaysEarlyLate|} days {before if negative, after if positive} the deadline, on average{" - right on the deadline, on average" replacing everything from "Typically" onward if MedianDaysEarlyLate is exactly 0}.</p>
            </div>
            <div class="ur-pu-load">
              <div class="ur-vol tnum">{real Instances}<small> items</small></div>
              <!-- [FIX 2026-09-14] The days-early/late figure moved to a plain-language sentence
                   in .ur-pu-mix above (ur-timingline) - no tag/badge here any more. -->
              <span class="ur-imp" style="{display:none if real ImprisonmentInstances is 0}">{real ImprisonmentInstances as a % of this user's own Instances} jailable</span>
            </div>
          </div>
          <!-- repeat: SAME rows as ur-pulist-1, same count, re-ordered by real share descending -->
        </div>

        <div class="ur-tailnote">{real one-sentence note on how much of the tail (rows not shown) accounts for, using real remaining counts - omit this note entirely if every qualifying row is already shown}</div>
      </div>
    </section>

    <!-- ---- STANDOUTS · who clears it cleanly ---- -->
    <section class="di-pane" id="di-pane-3" aria-label="Standouts">
      <div class="di-pane__head">
        <h2 class="di-pane__title">Who clears it cleanly</h2>
        <span class="di-pane__hint">the counter-story — high volume, low overdue, worth replicating</span>
      </div>
      <div class="ur-opsgrid">
        <!-- one card per real row that is materially high-volume (Instances at or above this
             run's real materiality floor) AND has a real low/zero OverduePct - up to 6, never
             padded, never invented if fewer qualify -->
        <div class="ur-op">
          <div class="ur-op__top"><span class="ur-op__acct">{real UserName}</span><span class="ur-op__ovd">{real OverduePct}% overdue</span></div>
          <div class="ur-op__vol"><b class="tnum">{real Instances}</b> items · {Performer|Reviewer}</div>
          <div class="ur-op__cred">{real one-sentence credit, citing only real fields - e.g. real EngagementBand or real OnTimePct}</div>
        </div>
      </div>
    </section>

    <!-- ---- WHAT THIS MEANS · findings ---- -->
    <section class="di-pane" id="di-pane-4" aria-label="What this means">
      <div class="di-pane__head">
        <h2 class="di-pane__title">What this means</h2>
        <span class="di-pane__hint">the reasoning that turns the numbers into a verdict</span>
      </div>
      <div class="ur-findgrid">
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
        <div class="ur-finding">
          <div class="ur-finding__fh">
            <span class="ur-finding__fn tnum">{01, 02, ...}</span>
            <span class="ur-finding__headline">{real headline sentence}</span>
          </div>
          <div class="ur-finding__ev">
            <span class="ur-ev">{real stat}</span>
          </div>
          <div class="ur-finding__impl">{real one-sentence implication, grounded only in the stats above}</div>
        </div>
      </div>
    </section>
  </div>
</div>
</body>
</html>
```

## CSS — fully self-contained, plus the User-specific classes below (copied from the real Angular component's own `.ur-*` block)

**[FIX 2026-09-14] Root cause of the "round, purple tabs" bug found and fixed.** This section
used to say "copy verbatim from `05_report_html_fixed_holistic.md`" for every base `.di-*`
class and every `:root` token — but `PaidReportAgentsRegistration.cs` builds the
`dimension_selection:Users` render agent from **only this file**
(`05_report_html_dimension_selection_user.md`); `05_report_html_fixed_holistic.md`'s content
is never in this agent's context at render time. That instruction was therefore unfollowable —
proven live: a real render's main tab strip came out as a full 999px pill with a white rounded
pill for the active tab, matching neither this project's real spec (outline pill, 7px radius)
nor any other known reference, because the model had nothing to copy and improvised. Every
class below that used to be "copied verbatim from that file" is now **inlined here directly,
verbatim**, so the render agent actually has it. Do not reintroduce a cross-file pointer for
any of these.

```css
:root{
  --c-text:#3d3d3d; --c-border:#dbdbdb; --c-border-2:#d0d0d0; --c-grey:#999999;
  --c-bg:#f1f1fe; --c-pill-bg:#f5f5fe; --c-surface:#ffffff; --c-brand:#125aab;
  --c-text-2:#585858; --c-text-3:#666666; --c-faint:#bfbfbf;
  --r-sm:3.5px; --r-md:5.5px; --r-lg:9px; --r-xl:11px;
  --di-donut:92px; --di-hero-pad-y:16px; --di-hero-pad-x:16px;
  --gap-md:calc(12.5px*1.25); --gap-lg:calc(16px*1.25);
  --fs-di-score:1.875rem; --fs-di-of:.66rem; --fs-di-title:.95rem; --fs-di-sub:.88rem;
  --fs-di-method:.72rem; --fs-di-comp-name:.66rem; --fs-di-comp-num:.94rem;
  --fs-di-chip:calc(11px*1.25); --fs-di-eyebrow:calc(10.5px*1.25);
  --fs-di-headline:calc(14.5px*1.25); --fs-di-snaphead:calc(15.5px*1.25);
  --fs-di-snap-num:calc(22px*1.25); --fs-di-snap-label:calc(9.5px*1.25);
  --fs-di-snap-desc:calc(11.5px*1.25); --fs-di-action:calc(13.5px*1.25);
  --fs-meta:calc(10.5px*1.25);
}
.di-hero{position:relative;overflow:hidden;border:1px solid transparent;border-radius:var(--r-xl);color:var(--c-text);background:linear-gradient(115deg,#eef1fe 0%,#f7f0fb 38%,#fdf6fb 62%,#f4f8ff 100%) padding-box,linear-gradient(100deg,#9db4e8 0%,#c3b2ee 30%,#e8b8dd 55%,#a8c8f0 80%,#9db4e8 100%) border-box;background-size:auto,300% 100%;box-shadow:0 2px 10px rgba(18,90,171,.08);--di-fg:var(--c-text);--di-fg-muted:var(--c-text-3);--di-fg-faint:var(--c-grey);--di-line:#e6e6f2;--di-panel:#ffffff;--di-ok:#2e9e5b;--di-warn:#e0a106;--di-bad:#d94a3d}
.di-scoreblock{display:grid;grid-template-columns:auto 1fr;gap:var(--gap-lg);align-items:center}
.di-donut{position:relative;width:var(--di-donut);height:var(--di-donut);flex-shrink:0}
.di-donut svg{width:100%;height:100%;transform:rotate(-90deg)}
.di-donut__track{stroke:#e3e9f5}
.di-donut--ok .di-donut__arc{stroke:var(--di-ok)}
.di-donut--warn .di-donut__arc{stroke:var(--di-warn)}
.di-donut--bad .di-donut__arc{stroke:var(--di-bad)}
.di-donut__ctr{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center}
.di-donut__num{font-size:var(--fs-di-score);font-weight:600;letter-spacing:-.03em;line-height:1}
.di-donut__of{font-size:var(--fs-di-of);color:var(--di-fg-muted);margin-top:2px;letter-spacing:.04em}
.di-id{min-width:0}
.di-eyebrow{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-brand)}
.di-title{font-size:var(--fs-di-title);font-weight:600;line-height:1.15;margin:6px 0 4px;color:var(--di-fg)}
.di-sub{font-size:var(--fs-di-sub);color:var(--di-fg-muted)}
.di-verdict{min-width:0}
.di-verdict__row{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.di-band{display:inline-flex;align-items:center;gap:8px;padding:4px 12px;border-radius:999px;font-size:var(--fs-di-chip);font-weight:500}
.di-band__dot{width:6px;height:6px;border-radius:999px}
.di-band--warn{background:#fcf0de;color:#b45708;border:1px solid #e8c79c}
.di-band--warn .di-band__dot{background:#b45708}
.di-band--bad{background:#fceae8;color:#b3261e;border:1px solid #dfa39d}
.di-band--bad .di-band__dot{background:#b3261e}
.di-band--ok{background:#e7f5ec;color:#1e8a4a;border:1px solid #a8d3b8}
.di-band--ok .di-band__dot{background:#1e8a4a}
.di-hero__title{margin:10px 0 .35rem;font-size:var(--fs-di-title);font-weight:600;line-height:1.25;letter-spacing:-.02em;color:var(--di-fg)}
.di-headline{font-size:var(--fs-di-headline);line-height:1.45;color:var(--di-fg);margin:0}
.di-verdict:has(.di-band--bad) .di-headline em{color:#b3261e}
.di-method{margin-top:12px;font-size:var(--fs-di-method);color:var(--di-fg-muted);line-height:1.55}
.di-components{position:relative;background:rgba(255,255,255,.5);border-top:1px solid var(--di-line);padding:var(--gap-lg) var(--di-hero-pad-x)}
.di-components__head{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:var(--gap-md)}
.di-components__label{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-brand)}
.di-tabsroot{margin-top:22px}
.di-tab-input{position:absolute;opacity:0;pointer-events:none}
.di-tabpane,.di-pane{display:none}
.di-pane{padding:18px 0}
#di-tab-1:checked ~ #di-pane-1,
#di-tab-2:checked ~ #di-pane-2,
#di-tab-3:checked ~ #di-pane-3,
#di-tab-4:checked ~ #di-pane-4{display:block}
.di-tabnav{display:inline-flex;align-items:center;gap:4px;padding:4px 6px;border-radius:var(--r-lg);background:var(--c-pill-bg)}
.di-tab{cursor:pointer;user-select:none;display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border-radius:7px;font-size:var(--fs-di-chip);color:var(--c-grey);font-weight:500;border:1.25px solid transparent}
.di-tab:hover{color:var(--c-brand)}
.di-tab__count{font-size:10px;font-weight:600;padding:1px 6px;border-radius:999px;background:var(--c-surface);border:1.25px solid var(--c-border);color:var(--c-grey)}
.di-tabsroot:has(#di-tab-1:checked) label[for="di-tab-1"],
.di-tabsroot:has(#di-tab-2:checked) label[for="di-tab-2"],
.di-tabsroot:has(#di-tab-3:checked) label[for="di-tab-3"],
.di-tabsroot:has(#di-tab-4:checked) label[for="di-tab-4"]{color:var(--c-brand);border-color:var(--c-brand);font-weight:600}
.di-tabsroot:has(#di-tab-1:checked) label[for="di-tab-1"] .di-tab__count,
.di-tabsroot:has(#di-tab-2:checked) label[for="di-tab-2"] .di-tab__count,
.di-tabsroot:has(#di-tab-3:checked) label[for="di-tab-3"] .di-tab__count,
.di-tabsroot:has(#di-tab-4:checked) label[for="di-tab-4"] .di-tab__count{background:var(--c-surface);border-color:var(--c-brand);color:var(--c-brand)}
.di-pane__head{display:flex;align-items:center;gap:10px;margin:0 0 12px}
.di-secnum{display:inline-flex;align-items:center;justify-content:center;width:2rem;height:2rem;border-radius:var(--r-md);background:var(--c-surface);border:1px solid #dbdbdb;color:#585858;font-size:.8rem;font-weight:600;font-variant-numeric:tabular-nums;flex-shrink:0}
.di-pane__title{font-size:var(--fs-di-snaphead);font-weight:600;letter-spacing:-.01em;color:var(--c-text)}
.di-pane__hint{font-size:var(--fs-di-snap-desc);color:var(--c-grey)}
.di-kpigrid{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:var(--gap-md);margin-top:var(--gap-lg)}
.di-kpi{grid-column:span 12;display:flex;flex-direction:column;gap:var(--gap-md);min-width:0;background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:var(--gap-lg) calc(var(--gap-lg) + 2px);box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-kpi--span3{grid-column:span 3} .di-kpi--span4{grid-column:span 4} .di-kpi--span6{grid-column:span 6} .di-kpi--span8{grid-column:span 8} .di-kpi--span12{grid-column:span 12}
.di-kpi__head{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}
.di-kpi__headtext{min-width:0}
.di-kpi__eyebrow{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-grey)}
.di-kpi__tag{flex-shrink:0;display:inline-flex;align-items:center;gap:5px;font-size:var(--fs-di-chip);font-weight:500;padding:3px 9px;border-radius:999px;white-space:nowrap;letter-spacing:.02em;border:1px solid transparent}
.di-kpi__dot{width:6px;height:6px;border-radius:999px;flex-shrink:0}
.di-kpi__tag--bad{background:#fcebea;color:#b3261e;border-color:#f3cfca} .di-kpi__tag--bad .di-kpi__dot{background:#b3261e}
.di-kpi__tag--warn{background:#fdf3e2;color:#b45708;border-color:#f0dcb4} .di-kpi__tag--warn .di-kpi__dot{background:#b45708}
.di-kpi__tag--ok{background:#e9f6ee;color:#1e8a4a;border-color:#c4e6d0} .di-kpi__tag--ok .di-kpi__dot{background:#1e8a4a}
.di-kpi__big{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}
.di-kpi__num{font-size:calc(var(--fs-di-snap-num) * 1.35);font-weight:600;letter-spacing:-.02em;line-height:1;color:var(--c-text)}
.di-kpi__unit{font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.4}
.di-kpi__narr{font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.55;margin:0}
```

**`.di-eyebrow` note** — this class was used bare in `05_report_html_fixed_holistic.md`'s own
hero markup but never actually had a CSS rule anywhere in this project until this fix (verified
by exhaustive search); the rule above matches the identical treatment already given to
`.di-kpi__eyebrow`/`.di-components__label`/`.di-caveats__eyebrow` (uppercase, letter-spaced,
600 weight), brand-coloured since it sits directly in the hero band.

**`.di-meta`/`.di-chip`** (used in the identity block, NOT declared in `05_report_html_fixed_holistic.md`'s own hero since that hero has none — copied verbatim from the real component):
```css
.di-meta{margin-top:var(--gap-md);display:flex;flex-wrap:wrap;gap:6px}
.di-chip{font-size:var(--fs-di-chip);color:var(--di-fg-muted);background:var(--di-panel);border:1px solid var(--di-line);padding:3px 10px;border-radius:999px;white-space:nowrap}
.di-chip b{color:var(--di-fg);font-weight:600}
.di-chip.mono{letter-spacing:.02em}
```

**`.ur-*` classes** (copied verbatim from `detailed-insights.component.css` lines 1580-1936,
the real `dimension === 'User'` block — colours/spacing unchanged):
```css
.ur .di-pane__head{margin-bottom:8px}
.ur .di-kpigrid{margin-top:0}
.ur-hero__inner{grid-template-columns:1fr;align-items:start;gap:var(--gap-lg)}
.ur-scoreblock{align-items:start}
.ur .di-id .di-title{font-size:calc(var(--fs-di-title) * .84)}
.ur .di-id .di-sub{font-size:calc(var(--fs-di-sub) * .9);line-height:1.55;margin-top:3px}
.ur-donut{width:calc(var(--di-donut) * 1.2);height:calc(var(--di-donut) * 1.2)}
.ur-donut .di-donut__track,.ur-donut .di-donut__arc{stroke-width:10}
.ur-donut .di-donut__num{font-size:calc(var(--fs-di-score) * .72)}
.ur-donut__pct{font-size:.5em;color:var(--di-fg-muted);font-weight:600;margin-left:1px}
.ur-donut__of{font-size:calc(var(--fs-di-of) * .74);color:var(--di-fg-muted);margin-top:3px;letter-spacing:.03em;max-width:15ch;text-align:center;line-height:1.3}
.ur-headline em{color:#b3261e;font-style:normal;font-weight:600}
.ur-roles__note{font-size:var(--fs-di-chip);color:var(--di-fg-muted)}
.ur-rolesgrid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
.ur-role{background:var(--di-panel);border:1px solid var(--di-line);border-radius:var(--r-md);padding:12px 14px;min-width:0}
.ur-role__top{display:flex;align-items:center;gap:8px}
.ur-role__dot{width:9px;height:9px;border-radius:3px;flex-shrink:0}
.ur-role--perf .ur-role__dot{background:#2f6fd0}
.ur-role--rev .ur-role__dot{background:#6f49e0}
.ur-role--unused .ur-role__dot{background:#b9bec7}
.ur-role__name{font-size:var(--fs-di-comp-name);font-weight:600;color:var(--di-fg)}
.ur-role__note{margin-left:auto;font-size:var(--fs-di-chip);color:var(--di-fg-faint)}
.ur-role__num{font-size:calc(var(--fs-di-score) * .7);font-weight:600;letter-spacing:-.02em;color:var(--di-fg);margin:8px 0 2px;line-height:1}
.ur-role__sub{font-size:var(--fs-di-chip);color:var(--di-fg-muted);line-height:1.4}
.ur-role--unused{opacity:.74}
.ur-role--unused .ur-role__num{color:var(--di-fg-muted)}
.ur-sw{display:inline-block;width:9px;height:9px;border-radius:2px;flex-shrink:0}
.ur-sw--crit{background:#b3261e}.ur-sw--high{background:#d24a3a}.ur-sw--med{background:#e0a106}.ur-sw--low{background:#aeb4bf}.ur-sw--neu{background:#c2c7d0}.ur-sw--rev{background:#6f49e0}.ur-sw--perf{background:#2f6fd0}
.ur-ratiobar{height:10px;border-radius:999px;overflow:hidden;display:flex;background:#eef1f7;border:1px solid var(--c-border)}
.ur-ratiobar__seg{height:100%;display:block;flex-shrink:0}
.ur-ratiolegend{display:flex;flex-wrap:wrap;gap:8px 16px;font-size:var(--fs-meta);color:var(--c-text-3)}
.ur-ratiolegend__k{display:inline-flex;align-items:center;gap:6px}
.ur-ratiolegend__k b{color:var(--c-text);font-weight:600}
.ur-lens-radio{position:absolute;opacity:0;pointer-events:none}
.ur-lensbar{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:10px}
.ur-lensbar__sort{font-size:var(--fs-di-snap-label);text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:600}
.ur-lenstoggle{display:inline-flex;background:var(--c-pill-bg);border-radius:999px;padding:4px 6px;gap:2px}
.ur-lensbtn{font:inherit;font-size:var(--fs-di-snap-desc);font-weight:500;color:var(--c-grey);background:transparent;padding:6px 14px;border-radius:999px;cursor:pointer;white-space:nowrap;display:inline-block}
.ur-lensbtn:hover{color:var(--c-text)}
.ur-lensthr{font-size:var(--fs-meta);color:var(--c-grey);margin-left:auto;letter-spacing:.01em;display:none}
.ur-lensq{font-size:var(--fs-di-snap-desc);color:var(--c-text-3);font-style:italic;margin:0 0 12px;display:none}
/* [FIX 2026-09-13] CSS-only lens toggle - same radio+:has() technique as the 4-tab nav
   (05_report_html_fixed_holistic.md "Tab navigation"), scoped to .ur-lensroot so :has() reaches
   the radios regardless of DOM position. Active-lens button style copied verbatim from the
   real .ur-lensbtn--active rule (getComputedStyle-confirmed live, 2026-09-13). */
.ur-lensroot:has(#ur-lens-1:checked) .ur-lensthr-1,
.ur-lensroot:has(#ur-lens-2:checked) .ur-lensthr-2{display:inline-block}
.ur-lensroot:has(#ur-lens-1:checked) .ur-lensq-1,
.ur-lensroot:has(#ur-lens-2:checked) .ur-lensq-2{display:block}
.ur-lensroot:has(#ur-lens-1:checked) .ur-pulist-2,
.ur-lensroot:has(#ur-lens-2:checked) .ur-pulist-1{display:none}
.ur-lensroot:has(#ur-lens-1:checked) label[for="ur-lens-1"],
.ur-lensroot:has(#ur-lens-2:checked) label[for="ur-lens-2"]{background:var(--c-surface);color:var(--c-brand);box-shadow:0 1px 3px rgba(20,28,48,.12);font-weight:600}
.ur-risklegend{display:flex;flex-wrap:wrap;gap:8px 12px;font-size:var(--fs-meta);color:var(--c-text-3)}
.ur-risklegend__k{display:inline-flex;align-items:center;gap:5px}
.ur-pulist{display:flex;flex-direction:column;gap:10px}
.ur-puser{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);box-shadow:0 1px 3px rgba(20,28,48,.05);padding:13px 16px;display:grid;grid-template-columns:1.5fr 1.25fr 2.7fr 1fr;gap:var(--gap-lg);align-items:center}
.ur-pu-id{min-width:0}
.ur-pu-acct{font-size:var(--fs-di-action);font-weight:600;display:flex;align-items:center;gap:8px;flex-wrap:wrap;color:var(--c-text)}
.ur-rolechip{font-size:var(--fs-di-snap-label);font-weight:600;letter-spacing:.03em;text-transform:uppercase;padding:2px 7px;border-radius:999px;color:#fff}
.ur-rolechip--Performer{background:#2f6fd0}
.ur-rolechip--Reviewer{background:#6f49e0}
.ur-pu-sub{font-size:var(--fs-meta);color:var(--c-text-3);margin-top:5px;display:flex;gap:6px;flex-wrap:wrap}
.ur-pu-heads{color:var(--c-brand);font-weight:600}
.ur-pu-metric{border-left:1px solid var(--c-border);padding-left:14px;min-width:0}
.ur-mnum{font-size:calc(var(--fs-di-snap-num) * 1.05);font-weight:700;letter-spacing:-.02em;line-height:1;color:var(--c-text)}
.ur-mcap{font-size:var(--fs-meta);color:var(--c-grey);margin-top:3px}
.ur-mband{display:inline-block;font-size:var(--fs-di-snap-label);text-transform:uppercase;letter-spacing:.04em;font-weight:600;margin-top:7px;padding:2px 8px;border-radius:999px;border:1px solid transparent}
.ur-band--critical_attention .ur-mnum{color:#b3261e}
.ur-band--critical_attention .ur-mband{background:#fceae8;color:#b3261e;border-color:#f3cfca}
.ur-band--high .ur-mnum{color:#b45708}
.ur-band--high .ur-mband{background:#fdf3e2;color:#b45708;border-color:#f0dcb4}
.ur-band--medium .ur-mnum{color:var(--c-text)}
.ur-band--medium .ur-mband{background:#f1f1f4;color:var(--c-text-2);border-color:#e4e4e8}
.ur-band--watch .ur-mnum{color:var(--c-grey)}
.ur-band--watch .ur-mband{background:#f6f7f9;color:var(--c-grey);border-color:#e8e8ec}
.ur-pu-mix{min-width:0}
.ur-mixlabel{font-size:var(--fs-di-snap-label);text-transform:uppercase;letter-spacing:.05em;color:var(--c-grey);font-weight:600;margin-bottom:6px}
.ur-riskbar{height:8px;border-radius:999px;overflow:hidden;display:flex;background:#eef1f7;margin-bottom:7px}
.ur-riskbar__seg{height:100%;display:block;flex-shrink:0}
.ur-pu-load{text-align:right;min-width:0}
.ur-vol{font-size:var(--fs-di-action);font-weight:600;color:var(--c-text)}
.ur-vol small{font-size:.7em;color:var(--c-grey);font-weight:400}
.ur-imp{display:inline-block;margin-top:6px;font-size:var(--fs-di-snap-label);font-weight:600;padding:2px 8px;border-radius:999px;background:#b3261e;color:#fff}
.ur-tailnote{margin-top:12px;background:#f7f8fc;border:1px dashed var(--c-border-2);border-radius:var(--r-md);padding:11px 14px;font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.5}
.ur-tailnote b{color:var(--c-text);font-weight:600}
.ur-opsgrid{display:grid;grid-template-columns:repeat(3,1fr);gap:var(--gap-md)}
.ur-op{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);box-shadow:0 1px 3px rgba(20,28,48,.05);padding:14px 16px;display:flex;flex-direction:column;gap:8px}
.ur-op__top{display:flex;align-items:center;justify-content:space-between;gap:10px}
.ur-op__acct{font-size:var(--fs-di-action);font-weight:600;color:var(--c-text)}
.ur-op__ovd{font-size:var(--fs-meta);font-weight:600;color:#1e8a4a;background:#e7f5ec;border:1px solid #a8d3b8;padding:2px 8px;border-radius:999px;white-space:nowrap}
.ur-op__vol{font-size:var(--fs-di-snap-desc);color:var(--c-text-3)}
.ur-op__vol b{color:var(--c-text);font-weight:600}
.ur-op__cred{font-size:var(--fs-di-snap-desc);color:var(--c-text-3);line-height:1.5;border-top:1px solid var(--c-border);padding-top:9px}
.ur-findgrid{display:grid;grid-template-columns:1fr;gap:var(--gap-md)}
.ur-finding{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);box-shadow:0 1px 3px rgba(20,28,48,.05);padding:15px 17px;display:flex;flex-direction:column;gap:10px}
.ur-finding__fh{display:flex;gap:10px;align-items:flex-start}
.ur-finding__fn{width:22px;height:22px;flex-shrink:0;border-radius:6px;background:var(--c-bg);border:1px solid var(--c-border);color:var(--c-grey);display:flex;align-items:center;justify-content:center;font-size:var(--fs-di-snap-label);font-weight:600}
.ur-finding__headline{font-size:var(--fs-di-action);font-weight:600;letter-spacing:-.01em;line-height:1.35;color:var(--c-text)}
.ur-finding__ev{display:flex;flex-wrap:wrap;gap:6px}
.ur-ev{font-size:var(--fs-meta);color:var(--c-text-3);background:var(--c-bg);border:1px solid var(--c-border);padding:2px 8px;border-radius:999px}
.ur-finding__impl{font-size:var(--fs-di-snap-desc);color:var(--c-text-3);line-height:1.55;border-top:1px solid var(--c-border);padding-top:10px}
/* [ADDED 2026-09-13, replaces the ur-ovd overdue-rate chip in ur-pu-load] Completion timing -
   inline per-row badge, same colour language as .ur-op__ovd (Standouts) reused for the same
   early=good/late=bad meaning, just a different metric (signed days, not a rate). */
.ur-timingline{font-size:var(--fs-meta);font-weight:500;margin:6px 0 0;line-height:1.4}
.ur-timingline--early{color:#1e8a4a}
.ur-timingline--late{color:#b3261e}
.ur-timingline--ontime{color:var(--c-text-2)}
@media (max-width:52rem){
  .ur-hero__inner{grid-template-columns:1fr}
  .ur-findgrid{grid-template-columns:1fr}
  .ur-rolesgrid{grid-template-columns:1fr}
  .ur-opsgrid{grid-template-columns:1fr}
  .ur-puser{grid-template-columns:1fr}
  .ur-pu-metric{border-left:0;padding-left:0}
  .ur-pu-load{text-align:left}
}
```
**[NOTE] `@container` in the real source becomes `@media` here** — this document has no
containment context declared (single top-level `.page` wrapper, same as every other prompt
in this repo), so use the equivalent viewport media query, not the real component's container
query syntax.

## Voice

Same restrained, evidence-only voice as every other render prompt in this repo. Vocabulary
is binding: **insights** (never "report"), exact role names (**Performer, Reviewer**).
Display numbers carry `class="tnum"` and thousands separators; identifiers never do.

## Self-check before returning

Reject your own draft and fix it if it: renders fewer or more than 4 tabs · omits the
Priority-load lens toggle (both `ur-lens-1`/`ur-lens-2` radios and both
`ur-pulist-1`/`ur-pulist-2` lists) · shows a `ur-timingline` sentence for a user whose real
`TimingSampleSize` is null or below 5 (omit the line for that row instead) · states the
days-early/late figure as a terse tag instead of the plain sentence · uses
`(click)`/JS-bound tab switching instead of the radio/`:has()` technique · claims a
"two-person pipeline"/overlap between any two accounts · labels any row/card "Approver"/
"role 6" · shows a per-user risk-mix breakdown (Critical/High/Medium/Low %) anywhere ·
shows a combined imprisonment-overdue lens or figure · says "heads N depts" instead of real
`BranchesCovered` · shows a percentage as the PRIMARY Priority-load ranking number for the
Overdue-items lens instead of the real raw `Overdue` count · emits `<details>`, an accordion,
or any chevron icon · introduces a hex/colour not in the `:root` block or this file's CSS ·
uses any oklch colour or DM-Sans/Trent naming · declares `@font-face` itself · leaves the
priority-load list missing a real row that has `Instances > 0` and would rank in the top 10 by
real `Overdue` (lens 1) or real `share` (lens 2) · pads Standouts or Findings to a round number
with an invented row · states `MedianDaysEarlyLate` as anything other than the real value
already computed by `sql/12_dimension_users.sql` (never re-derive it as a mean).

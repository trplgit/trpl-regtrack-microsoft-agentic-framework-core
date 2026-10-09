# Report Generation — Licences, freehand (v10, 2026-09-30)

**[v10, 2026-09-30] Section 7a below (the per-number formula hover-link for EVERY key-metric
number) is RETIRED - do not follow it.** Two prompt-only phrasings (attach an "i" to a `.kpi` tile,
then wrap the number inline in its own prose) were tried across 3 real tenant-1285 renders and
NEITHER ever produced the markup - this freehand dimension's real layout varies too much per run
(even the outer container class differed: "report-stack"/"report"/"sections" across the 3 runs) for
a free-text instruction covering EVERY number to have a reliable anchor. The fixed reference strip
at the bottom of the page is now handled the same way every other exact-shape requirement in this
pipeline is fixed (see `InsightsReportOrchestrator`'s node 8i / `NumberFormulaInjector` /
`LicenceNumberFormulas`): a deterministic post-render step, not authored by you.

**[SAME DAY] Section 7b (NEW) narrows this to PERCENTAGES ONLY, inline in your own prose** - a real
product reference screenshot showed a percentage figure itself as a hover-link to its formula, and
the user asked for exactly that pattern (not the bottom-of-page strip) for percentage/
percentage-point figures specifically, never plain counts. Narrower and more mechanically
identifiable than 7a's blanket attempt (a percentage always has a literal `%`/`pp` to trigger on),
so worth a fresh prompt-only try before reaching for code injection again. Everything else in this
file (sections 1-6, 8-15) is unchanged from v9.

**[2026-09-29, from a real user review] PLAIN LANGUAGE - write for a compliance manager, not for the
developer who built the report.** Only the WORDS change in this version: keep the same sections, the same
charts, the same layout, the same "i" / How-to-read mechanism and the same data. Every sentence a reader
sees must be understandable by anyone on first reading.

- The report answers three questions, in plain words: what needs my attention; which licences / items are
  affected; is anything missing from this report.
- Short sentences, everyday words. No analytics words: never "status mix", "share" (say "percentage"),
  "report-wide" (say "overall" / "across this report"), "workflow", "scoped", "period licences",
  "catalogue", "matrix", "positive totals", "untyped", "reconciled", "True/False".
- Never describe how a chart is built ("lollipop", "vertical position identifies", "encoding"). Describe
  what the reader learns: "Each line is one licence type. The further right the dot, the higher the
  percentage of expired licences."
- The "How to read this chart" panels are conversational: 2-4 short plain rows, no chart jargon.
- Rules about how the data was collected (access rules, why some records are left out) go INSIDE an "i"
  panel or a Details line, not in the main reading flow. The main flow keeps one plain sentence, e.g.
  "29 licences are outside your access permissions." / "5 licences could not be included because
  required status information is missing or incomplete."
- Never show internal words: "licence role", "Management licence role", "linked task", "active
  performer", "acted on", "dated status record", "omitted by report rules", "branch and category access",
  "totals match: True". Use the plain replacements below.

**Licence wording - use the right-hand words (the numbers are unchanged).**

| Never write | Write instead |
|---|---|
| period licences / 21 period licences | licences ending this period / 21 licences ending this period (or "ending between 2 Jul and 29 Sep 2026") |
| None of the 21 period licences is Active | 21 licences end this period - none are currently active |
| still in the workflow / where licences sit in the workflow | not yet active / the current status of each licence |
| status mix | current status ("how many licences are Active, Pending for review, Rejected, Expired or in another status") |
| Expired share | percentage of expired licences ("1 of 19 Transport licences is expired (5.3%)") |
| report-wide share | overall percentage ("compared with 4.8% across all licences in this report") |
| One lollipop = one licence type | Each line is one licence type; the dot shows the percentage of expired licences |
| Horizontal axis = Expired share | The further right the dot, the higher the percentage of expired licences |
| Complete type assignment / assigned to a type | All licences have a licence type |
| Untyped licences / Untyped: 0 | Licences without a type / Without a type: 0 |
| Totals match: True | All 21 licences are accounted for |
| Licence access follows RegTrack's licence rules | This report shows the licences available to you |
| branch and category access used in other reports | (Details panel only) Other reports may use different access rules |
| outside the Management licence role | outside your access permissions |
| omitted by report rules | could not be included |
| linked task / active performer / dated status record | (main flow) required status information is missing or incomplete; (Details panel) the licence may not have an active task, an assigned person, or a dated status update |
| records retained by the licence-report rules | licences available to this user under the report's access rules |
| licence-type catalogue / matrix / reported licence type | list of licence types / each tile is one licence type |
| Positive totals are ordered by licence count | Licence types with licences come first, most licences first; types with none follow in A-Z order |
| RegTrack status Expired | status: Expired |

**Status names stay EXACTLY as recorded** (Active, Expiring, Expired, Applied, Pending for review, Rejected,
Application rejected, Terminated, Not applicable) - testers compare them with the licence Excel. Rejected and
Application rejected are two separate recorded statuses; never invent a difference between them. "Other
status" = Draft, Registered, Registered & Renewal Filed or Validity Expired - say that in its tooltip.


**[v6-v9, 2026-09-29, from user review of a real report] Four reader rules.**
1. **Never name the source system.** No "RegTrack", "RegTrack shows", "RegTrack's status" or "the licence
   report" anywhere the reader sees. Say "status", "recorded status" or just the status name: "5 are Active
   and 3 are Expired". Never explain how a status is worked out (no "not calculated from an end date", no
   "latest recorded value" notes) - that belongs in the reasoning file, not the report.
2. **Lead with renewal progress, never with "none are Active".** A licence enters a period because its end
   date falls in it, so for a period that has already passed, not being Active is normal - it is not the
   finding. Name each status count directly - never invent an umbrella label ("ended another way", "in the
   workflow", "resolved") that hides what is inside it. If you group counts together for the lead, say every
   status name in that same sentence, not in a parenthesis the reader might skip: "7 licences are Pending
   for review and 3 are Applied - these 10 are still being processed. Of the rest, 5 are Rejected,
   3 are Application rejected, 1 is Terminated and 1 is Not applicable." Never write "None of the N is
   Active" as a headline or as the main issue, and never use a bare phrase like "ended another way",
   "in progress" or "resolved" without the real status names next to it every time it appears.
3. **Say each fact once.** Decide the ONE section where a given number or finding belongs, then do not
   restate it as a near-duplicate sentence in another section, card, or chart caption - a reader who reads
   the whole report should never see "1 of 19 Transport licences is expired (5.3%)" twice in different
   words. A number may appear again only where it is genuinely doing new work (e.g. once as a headline
   figure, once inside a chart's own hover/focus detail) - never as a second explanatory sentence that adds
   nothing new. Give the composition its own freedom for section names, order, count and chart choice -
   nothing in this file fixes a specific set of tile titles or a specific report shape; choose what best
   fits THIS tenant's real numbers.
4. **No section or chart for a tiny difference.** An Expired-share comparison between licence types (or any
   per-type rate ranking) gets its own section or chart ONLY when `TenantExpiredLicences` is at least 5 AND
   the types compared have at least 20 licences each. Otherwise mention it in at most one sentence inside
   another section (e.g. "1 licence is Expired, in Transport") - never a section built on one licence.
   Say Expired counts plainly; never speculate that a licence "may no longer be valid" or that the business
   "may be operating without a licence".


**[v5/v6, 2026-09-29] The report period.** Every row, total, share, rank and finding counts ONLY the licences
whose END DATE falls in the period the user picked (`WindowStart` to the day before `WindowEnd`) - the same
end-date filter the licence report applies, so testers compare against that report with the same
dates. A licence with no end date (perpetual) is never in a period. Say this plainly once, near the top
("licences whose end date falls in this period"), and never call these figures "all your licences".
`AllLicences`, `AllActiveLicences` and `AllExpiredLicences` are CONTEXT across every licence in the user's
licence scope, whatever its end date: use them in ONE short line (e.g. "Across all 180 licences,
5 are Active and 3 are Expired"), never mix them into a period share or chart. If `ScopedLicences` is 0, the
report says no licence ends in this period, gives the context line, and stops - no empty charts.
`EndingNext30` is counted from TODAY, not from the period - label it "end date within the next 30 days
(from today)" and show it only when it is above 0.


**[v4/v5, 2026-09-29] Licence status = the recorded status.** Every status count
(`ActiveLicences`, `Expired`, `Expiring`, `Applied`, `PendingForReview`, `Rejected`, `ApplicationRejected`,
`Terminated`, `NotApplicable`, `OtherStatus`) is the licence's latest status exactly as the licence
report shows it - testers check the report against that report's Excel. They are NOT worked out from the end
date: never call a licence "lapsed", "overdue" or "expired" because of its end date, and never recompute
Active or Expired from `EndingNext30`. Use these status words: Active, Expired, Expiring, Applied, Pending for
review, Rejected, Application rejected, Terminated, Not applicable. The status columns add up to
`TotalLicences` on every row. Only `EndingNext30` is date-based (end date within the next 30 days).


**[v4, 2026-09-27]** Copied from v3 (untouched). Changes: section 10 - Atmosphere (hero wash,
one decorative corner shape, tinted plot areas, meaning-tinted summary cards). Found live: a deployed
v3 render came out flat white while earlier renders had these touches; now they are required.
Section 11 - never drop real rows. Section 12 - plain CEO/CFO words, every narrative point shown.
Section 13 - rows are injected by code as #insights-data; the model reads them, never types them.
REVISED same day after side-by-side feedback: section 10 now = clean white cards with colour in
tiles/tags/charts (the washes and corner shape looked ugly); section 12 = no "What this means"
heading; section 14 = small blue company-name label, big headline, blue reporting-window box.

**[v3, 2026-09-27]** Copied from the previous version (which is untouched). New in v3: every chart
gets an "i" button that opens a "How to read this chart" panel (section 7), every chart is
interactive (section 8), chart craft rules (section 9), and a stricter script rule (technical
constraint 3). Sections 7-9 are shared word-for-word with every other v3 dimension render prompt.

**[REPLACED 2026-09-14]** This file previously reproduced Sambram's fixed single-section
"dimension view" template verbatim. Product direction now: composition (the plan you are given,
already approved) decided real structure/hero/emphasis for THIS tenant's own data — your job is to
actually build what it describes. Real markup, real CSS, real layout, matching what
`composition_plan.blocks[].emphasis` asks for. You have genuine freedom over illustration choice,
layout, and visual treatment — this is deliberately not a fixed document shape.

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

**6. Long tables — contained, never page-growing.** [ADDED 2026-09-21] Real feedback on an early
Act render: a "complete register" table with 30+ rows just kept growing the whole page - the
reader had to scroll the entire document to reach the table's last row. Any table listing every
real member of the dimension (a "complete register," "all configured X," or similar - not a
capped top-5/top-10 list) goes inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header (`position:sticky;top:0`) keeps column labels visible while scrolling. A live search input
above the table and clickable per-column sort are a real, approved enhancement for a large
register - not mandatory, but worth doing when the composition plan's emphasis calls for the
register being genuinely explorable rather than just present.

**7. The "i" button and the "How to read this chart" panel (NEW in v3, required on EVERY chart).**
Every chart, graph, heatmap, distribution, scatter, dot strip or other visual gets a small round
"i" button right after its title. Hovering the "i" opens a panel on the right side of the screen
titled **"How to read this chart"**; moving the mouse away closes it (the panel stays open while
the mouse is over the "i" OR over the panel itself). Clicking the "i" pins the panel open until the
reader clicks the close X (or presses Escape). Tables do not need one; charts always do.

Every number inside the examples in sections 7 and 8 (42 licence types, median 25.5, 18.2%, etc.) is
ILLUSTRATIVE ONLY - never copy one onto the page; use this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title - all 42 licence types in this period</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences: what it shows and what question it answers)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each bar = one licence type</strong><span>A longer bar means more expired licences of that type.</span></div>
        <div class="hr-viz"><!-- optional mini inline-SVG illustration --></div>
      </div>
      <!-- one .hr-row per component -->
    </aside>
  </div>
</div>
```

```css
.chart-head{display:flex;align-items:center;gap:10px}
.hr{position:static;display:inline-flex}
.hr-toggle{position:absolute;opacity:0;width:1px;height:1px;margin:0}
.hr-i{display:inline-grid;place-items:center;width:22px;height:22px;border-radius:50%;background:#8a8f99;color:#fff;
  font:700 13px/1 'Poppins',sans-serif;font-style:normal;cursor:pointer;user-select:none;flex:none;transition:background .15s}
.hr-i:hover,.hr-toggle:checked+.hr-i{background:var(--c-brand)}
.hr-toggle:focus-visible+.hr-i{outline:2px solid var(--c-brand);outline-offset:2px}
.hr-panel{position:fixed;top:16px;right:16px;bottom:16px;width:min(600px,calc(100vw - 32px));z-index:60;overflow-y:auto;
  background:#fff;border:1px solid #e6e9ef;border-radius:16px;box-shadow:0 18px 48px rgba(16,24,40,.18);padding:28px 26px 20px;
  opacity:0;visibility:hidden;transform:translateX(12px);
  transition:opacity .18s ease,transform .18s ease,visibility 0s linear .3s}
.hr:hover .hr-panel,.hr:has(.hr-toggle:checked) .hr-panel{opacity:1;visibility:visible;transform:none;transition-delay:0s}
.hr:hover .hr-panel{z-index:61}
.hr.hr-just-closed .hr-panel{opacity:0!important;visibility:hidden!important;transition:none!important}
.hr-close{position:absolute;top:16px;right:18px;width:32px;height:32px;display:grid;place-items:center;border-radius:8px;
  font-size:26px;line-height:1;color:#6b7280;cursor:pointer}
.hr-close:hover{background:var(--c-mist);color:#1f2937}
.hr-title{margin:0 40px 6px 0;font-size:24px;font-weight:700;color:#1f2937;letter-spacing:-.01em}
.hr-intro{margin:0 0 18px;font-size:15px;line-height:1.55;color:var(--c-text-2)}
.hr-row{display:grid;grid-template-columns:40px 1fr auto;gap:16px;align-items:center;background:#f5f7fb;border-radius:12px;
  padding:16px 18px;margin-bottom:10px}
.hr-ico{display:grid;place-items:center}
.hr-txt strong{display:block;font-size:16px;font-weight:600;color:#1f2937;margin-bottom:3px}
.hr-txt span{display:block;font-size:14px;line-height:1.5;color:var(--c-text-2)}
.hr-viz{min-width:0;max-width:220px}
@media (max-width:52rem){
  .hr-panel{top:auto;left:8px;right:8px;bottom:8px;width:auto;max-height:82vh;transform:translateY(12px)}
  .hr-row{grid-template-columns:32px 1fr}.hr-viz{grid-column:1/-1;max-width:none}
}
@media print{.hr-i,.hr-panel{display:none}}
```

Add this one small script once, at the end of `<body>`, so Escape closes any pinned panel, and so the close [x] actually hides the panel right away and KEEPS it hidden while the pointer rests on it (found live, 2026-09-30: the [x] sits INSIDE `.hr-panel`, so the pointer is still over `.hr` the instant it is clicked - without this, the `:hover` half of the rule above keeps showing the panel until the mouse fully leaves, which reads as "the close button does nothing"; a fixed timer instead of a real mouseleave check was tried first and failed the same way once the timer ran out while the pointer was still resting there):
```js
document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
document.querySelectorAll('.hr-close').forEach(function(btn){btn.addEventListener('click',function(){var hr=btn.closest('.hr');if(!hr)return;hr.classList.add('hr-just-closed');hr.addEventListener('mouseleave',function onLeave(){hr.classList.remove('hr-just-closed');hr.removeEventListener('mouseleave',onLeave);});});});
```

Each chart's `id` (`hr-load` above) must be unique on the page.

**7a. [RETIRED 2026-09-30]** Was a per-number formula hover-link instruction for EVERY key-metric
number (counts included); never once produced the markup across 3 real trial renders (see the note
at the top of this file). Superseded by 7b below, which is narrower (percentages only) and by a
deterministic post-render step for the fixed reference strip (`NumberFormulaInjector`/
`LicenceNumberFormulas` in `Insights.Presentation`, node 8i) - neither is your job.

**7b. Percentage figures become a hover-link to their formula (NEW, 2026-09-30).** Wherever a
percentage or percentage-point figure appears in your OWN written prose - a headline, a section's
description text, a card's `.note` - wrap just that figure (e.g. `(5.3%)`, `84.8%`) as a hover-link
to a small popup showing how it is worked out. Reference: a real product screenshot showing exactly
this pattern - an underlined percentage in a sentence, hover reveals a card with a title, one plain
sentence, and a "HOW IT IS CALCULATED" fraction box.

**Scope - percentages only, never counts.** This dimension has one real percentage shape: the share
of licences with a given status out of the licences counted in that same scope (tenant-wide, or one
licence type) - always `{status count} / {counted total} x 100` (`TenantExpiredPct` tenant-wide,
`ExpiredPct` per licence type - the only percentage fields you have). Never wrap a plain count
("21 licences", "3 types", "5 Applied") - only a number that is itself a percentage figure. If you
never write a percentage in your prose this run, this section produces nothing - never invent one
to have something to wrap.

Reuse the SAME `.hr` mechanism section 7 already requires (same checkbox/label/panel, same
hover-to-preview / click-to-pin / Escape-to-close) - just styled and triggered differently: the
trigger is the percentage text itself (dotted underline, not a round "i" badge), and the panel body
is a title, one plain sentence, and a fraction formula box instead of `.hr-row`s.

**[FIX - found live in this project's own local lab, 2026-09-30] Every tag inside `.hr` here must be
inline (`span`), never `aside`/`div`/`h4`/`p`.** This block sits INSIDE a `<p>` of running prose
(unlike section 7's chart version, which sits inside a `<div>` chart-head, never inside a `<p>`). A
browser auto-closes a `<p>` the instant it meets a BLOCK-level start tag inside it (`aside`, `div`,
`h4`, `p` are all block) - the panel silently ends up as a sibling of your paragraph instead of
nested inside `.hr`, so it renders sanely as flat text but the popup can never open (confirmed live:
the "44.9%" link rendered fine, but clicking/hovering it did nothing - `.hr-panel` had been ripped
out of `.hr` by the browser's own parser). Use ONLY `span` for every element below - the CSS below
already declares `display:block`/`flex` on each one where a block layout is still wanted, so the
visual result is identical:

```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pf-{unique}" aria-label="How this percentage is worked out">
  <label for="pf-{unique}" class="hr-i pf">(5.3%)</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
    <label for="pf-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">Expired percentage - 5.3%</span>
    <span class="hr-intro pf-intro">The share of Transport licences counted this period whose status is Expired.</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-frac">
        <span class="pf-frac-stack">
          <span class="pf-num"><span class="pf-num-value">1</span><span class="pf-num-label">Expired licences (this scope)</span></span>
          <span class="pf-den"><span class="pf-den-value">19</span><span class="pf-den-label">Licences counted (this scope)</span></span>
        </span>
        <span class="pf-times">&times; 100</span>
      </span>
    </span>
  </span>
</span>
```
```css
.pf{display:inline;width:auto;height:auto;padding:0;margin:0;border-radius:0;background:none;
  color:inherit;font:inherit;font-weight:inherit;border-bottom:1.5px dotted var(--c-brand);cursor:help}
.pf:hover,.hr-toggle:checked+.pf{background:var(--c-light-blue)}
.pf-panel{display:block;right:auto;bottom:auto;width:min(340px,calc(100vw - 32px));max-height:calc(100vh - 24px);overflow-y:auto;padding:18px 20px 20px}
.pf-panel::before{content:"";position:absolute;top:-8px;left:20px;width:14px;height:14px;background:#fff;
  border-left:1px solid #e6e9ef;border-top:1px solid #e6e9ef;transform:rotate(45deg);border-radius:2px}
.pf-title{display:block;margin:0 40px 6px 0;font-size:20px;font-weight:700;color:#1f2937}
.pf-intro{display:block;margin:0 0 14px;font-size:14px;line-height:1.55;color:var(--c-text-2)}
.pf-formula{display:block;background:var(--c-light-blue);border-radius:10px;padding:14px 16px 16px;margin-top:4px}
.pf-formula-label{display:block;font-size:11px;font-weight:700;color:var(--c-brand);letter-spacing:.03em;margin:0 0 10px}
.pf-frac{display:flex;align-items:center;justify-content:center;gap:10px}
.pf-frac-stack{display:flex;flex-direction:column;align-items:center}
.pf-num{display:flex;flex-direction:column;align-items:center;padding-bottom:6px;border-bottom:1.5px solid #1f2937}
.pf-den{display:flex;flex-direction:column;align-items:center;padding-top:6px}
.pf-num-value,.pf-den-value{font-size:15px;font-weight:700;color:#1f2937;white-space:nowrap}
.pf-num-label,.pf-den-label{font-size:11px;color:var(--c-text-2);white-space:nowrap}
.pf-times{font-size:14px;font-weight:600;color:#1f2937}
```

**[FIX - found live in the same local lab test, 2026-09-30] `.pf-panel` must stay `position:fixed`
(inherited from the shared `.hr-panel` rule in section 7 - do not override it to `absolute` or
`relative`), with its `top`/`left` set by the small script below, not by CSS alone.** Every section
card is `overflow:hidden` (section 10 - needed for its own corner-circle decoration); a
`position:absolute` popup nested inside one gets silently clipped to the card's edges the instant it
grows taller than the card's remaining space (confirmed live: the card cut the popup off right after
its description, before the formula box). `position:fixed` escapes that clipping entirely (same
reason chart panels already use it) - only the exact screen position needs to track the link instead
of docking to a fixed screen edge, which plain CSS cannot do on its own.

**[FIX - found live, SAME lab test, immediately after the fix above] The shared `.hr-panel` rule
also sets `bottom:16px` (its own right-docked layout) - `.pf-panel` above already overrides this
with `right:auto;bottom:auto`, and that override matters: leaving `bottom:16px` in place while the
script sets `top` to some large value makes the browser stretch the panel's HEIGHT all the way down
to 16px from the viewport bottom (confirmed live: a card 500px+ tall with a large blank area below
the real content). `right:auto;bottom:auto` lets height/width go back to fitting the real content,
same as any ordinary element.** Copy the `.pf-panel` rule above exactly, including both overrides -
do not drop them because the popup "looks fine" in a quick read-through of the CSS.

Add this ONE small script once, right next to the Escape-close script already required in section 7 above (both go at the end
of `<body>`, in the same `<script>` or a second one - either is fine):
```js
function pfPlace(el){
  var panel=el.closest('.hr').querySelector('.pf-panel');if(!panel)return;
  var margin=12,r=el.getBoundingClientRect(),vw=window.innerWidth,vh=window.innerHeight;
  var w=panel.offsetWidth||340,h=panel.offsetHeight||200;
  var left=Math.min(Math.max(margin,r.left),vw-w-margin);
  var spaceBelow=vh-r.bottom-margin,spaceAbove=r.top-margin;
  var top=(h<=spaceBelow||spaceBelow>=spaceAbove)?r.bottom+10:r.top-h-10;
  top=Math.max(margin,Math.min(top,vh-margin-Math.min(h,vh-2*margin)));
  panel.style.left=left+'px';panel.style.top=top+'px';
}
document.querySelectorAll('.pf').forEach(function(el){el.addEventListener('mouseenter',function(){pfPlace(el);});el.addEventListener('focus',function(){pfPlace(el);});el.addEventListener('click',function(){pfPlace(el);});});
window.addEventListener('resize',function(){document.querySelectorAll('.hr-toggle:checked').forEach(function(cb){var hr=cb.closest('.hr'),trig=hr&&hr.querySelector('.pf');if(trig)pfPlace(trig);});});
```
(No `<` characters appear in this script, so the "space after every `<`" rule in technical
constraint 3 does not apply here - still double-check before returning, same as every other script.)

Rules:
- **Only `span` tags inside `.hr`, ever** - never `aside`, `div`, `h4`, or `p`, even though section
  7's chart panel uses those (that one is never inside running prose, this one always is).
- **Title** = "{Status word} percentage - {the real value}%" (e.g. "Expired percentage - 5.3%").
  Never any other percentage type - `Expired` is the only status this dimension turns into a share.
- **Description** = ONE plain sentence naming the real scope: "all licences counted this period"
  (tenant-wide `TenantExpiredPct`) or "{real LicenceTypeName} licences counted this period" (a
  type's own `ExpiredPct`) - the exact same scope word the surrounding sentence already used.
- **Numerator/denominator show the REAL NUMBER first, then its caption** (NEW, 2026-09-30 - real
  user feedback: the fraction is more useful with the actual count on it, not just the bare words).
  `pf-num-value`/`pf-den-value` = the real whole numbers for THIS scope - the Expired count and the
  total counted, same scope as the title/description (e.g. one licence type's own Expired/Total, or
  the tenant-wide `TenantExpiredLicences`/`ScopedLicences`). `pf-num-label`/`pf-den-label` stay
  exactly "Expired licences (this scope)" and "Licences counted (this scope)" underneath each number
  - never a raw field name (`TenantExpiredPct`, `ExpiredPct`, `TenantExpiredLicences`,
  `ScopedLicences`...) anywhere the reader can see, same rule as every other panel in this file. The
  two real numbers you write here, divided and multiplied by 100, must equal the percentage in the
  title - if they do not, you have the wrong scope's numbers, fix it before returning.
- **Wrap the figure only at its one home appearance** (section 2/3's "say each fact once" rule
  already decided where that is) - never re-wrap the same value if it legitimately repeats inside a
  chart's own hover/focus detail (that is covered by the chart's own "i" panel, not this one).
- Each `id` (`pf-{unique}` above) must be unique on the page, distinct from every chart's own
  `hr-load`-style id and from every other `pf-` id.

**What goes in the panel.** The composition plan gives you each section's content after the marker
`HOW TO READ:` inside its `emphasis` - use it as your source, one `.hr-row` per component, and fill
any component it missed so that EVERY visible part of the chart is explained. Required coverage:
- what one mark is (bar / bubble / tile / cell) - "Each bar = one licence type";
- each axis or position, with its direction ("Negative = recorded early, Positive = recorded late");
- size, when size encodes something ("Dot size = number of events" with small/medium/large);
- **every colour used**, one row each or one combined "Colours" row - and the swatch in `.hr-ico`
  must be the SAME colour value the chart actually uses;
- every reference line, with its real value in the title ("Median line (25.5)");
- highlighted marks and why they are highlighted;
- the scale, when it is not plain linear (logarithmic - and why);
- cards or labels shown under the chart;
- a "Data notes" row for any caveat that affects the chart (exclusions, nulls not plotted, window).

Write from the reader's point of view, plain words, short sentences. Every number in the panel
must already be in the data - the panel explains, it never adds a new figure. **Never show a raw
field or column name in a panel** (`TimingSampleSize`, `ImprisonmentInstances`, `OnTimePct`,
`EngagementBand`...) - say what it means instead ("number of completed events", "compliances
that carry a possible prison term", "share completed on time", "how often the user logs in").
The same applies to chart titles, legends and tooltips anywhere a reader sees them.

**Icons - use these exact inline SVGs in `.hr-ico`, never a text glyph or emoji.** A row about a
colour uses a swatch instead (`<span class="hr-sw" style="background:#XXXXXX"></span>` with the
chart's exact colour). Pick the icon that fits the row:
```html
<!-- marks: bars -->   <svg width="30" height="30" viewBox="0 0 30 30"><rect x="4" y="15" width="5" height="11" rx="1.5" fill="#125aab" opacity=".55"/><rect x="12.5" y="9" width="5" height="17" rx="1.5" fill="#125aab" opacity=".8"/><rect x="21" y="4" width="5" height="22" rx="1.5" fill="#125aab"/></svg>
<!-- marks: dots -->   <svg width="30" height="30" viewBox="0 0 30 30"><circle cx="10" cy="11" r="6" fill="#125aab" opacity=".85"/><circle cx="21" cy="9" r="4.5" fill="#e0a106"/><circle cx="17" cy="21" r="6.5" fill="#125aab"/></svg>
<!-- axis / direction --><svg width="30" height="30" viewBox="0 0 30 30"><path d="M3 15h24M8 10l-5 5 5 5M22 10l5 5-5 5" stroke="#125aab" stroke-width="2.4" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>
<!-- order / ranking -->  <svg width="30" height="30" viewBox="0 0 30 30"><path d="M5 7h20M5 15h14M5 23h8" stroke="#125aab" stroke-width="3" stroke-linecap="round"/></svg>
<!-- reference line -->   <svg width="30" height="30" viewBox="0 0 30 30"><path d="M2 15h26" stroke="#125aab" stroke-width="2.6" stroke-dasharray="5 4"/></svg>
<!-- scale -->            <svg width="30" height="30" viewBox="0 0 30 30"><rect x="4" y="19" width="5" height="7" rx="1.2" fill="#8a8f99"/><rect x="12.5" y="13" width="5" height="13" rx="1.2" fill="#8a8f99"/><rect x="21" y="5" width="5" height="21" rx="1.2" fill="#8a8f99"/></svg>
<!-- person / cards -->   <svg width="30" height="30" viewBox="0 0 30 30"><circle cx="15" cy="10" r="5.5" fill="#6b7280"/><path d="M5 26c1-6 5.2-9 10-9s9 3 10 9z" fill="#6b7280"/></svg>
<!-- hover / interact --> <svg width="30" height="30" viewBox="0 0 30 30"><path d="M9 4l14 11-6.5 1.2 3.8 7.6-3 1.5-3.8-7.6L9 22z" fill="#125aab"/></svg>
<!-- search / filter -->  <svg width="30" height="30" viewBox="0 0 30 30"><circle cx="13" cy="13" r="7.5" stroke="#125aab" stroke-width="2.6" fill="none"/><path d="M19 19l7 7" stroke="#125aab" stroke-width="2.8" stroke-linecap="round"/></svg>
<!-- data notes -->       <svg width="30" height="30" viewBox="0 0 30 30"><path d="M8 3h10l6 6v18H8z" fill="#8a8f99"/><path d="M12 14h8M12 18h8M12 22h5" stroke="#fff" stroke-width="1.8" stroke-linecap="round"/></svg>
```
```css
.hr-sw{display:block;width:26px;height:26px;border-radius:6px}
```

**Mini visuals - `.hr-viz`, REQUIRED on at least half the rows of every panel,** and always on the
"each mark", "axis/position", "size", "reference line" and "highlighted marks" rows when those
exist. Each is a small inline SVG (width 200, height 64-80) that recreates THAT ONE component with
the chart's real colours and a short label, in the style of these templates:
```html
<!-- descending bars + direction label -->
<svg width="200" height="72" viewBox="0 0 200 72"><g fill="#125aab"><rect x="4" y="6" width="12" height="44" rx="2"/><rect x="20" y="10" width="12" height="40" rx="2" opacity=".92"/><rect x="36" y="15" width="12" height="35" rx="2" opacity=".84"/><rect x="52" y="20" width="12" height="30" rx="2" opacity=".76"/><rect x="68" y="24" width="12" height="26" rx="2" opacity=".68"/><rect x="84" y="28" width="12" height="22" rx="2" opacity=".6"/><rect x="100" y="31" width="12" height="19" rx="2" opacity=".52"/><rect x="116" y="34" width="12" height="16" rx="2" opacity=".45"/><rect x="132" y="37" width="12" height="13" rx="2" opacity=".38"/></g><text x="4" y="66" font-size="11" fill="#125aab">Higher load</text><text x="92" y="66" font-size="11" fill="#125aab">&#8594;  Lower load</text></svg>
<!-- dashed reference line with label chip (use the REAL value) -->
<svg width="200" height="72" viewBox="0 0 200 72"><g fill="#125aab" opacity=".25"><rect x="8" y="14" width="10" height="50"/><rect x="24" y="18" width="10" height="46"/><rect x="40" y="22" width="10" height="42"/><rect x="56" y="28" width="10" height="36"/><rect x="72" y="34" width="10" height="30"/><rect x="88" y="40" width="10" height="24"/><rect x="104" y="44" width="10" height="20"/></g><path d="M2 38h196" stroke="#125aab" stroke-width="2" stroke-dasharray="6 4"/><rect x="104" y="6" width="84" height="22" rx="6" fill="#e8f2fd"/><text x="146" y="21" font-size="11.5" font-weight="600" text-anchor="middle" fill="#125aab">Median 25.5</text></svg>
<!-- signed axis around zero -->
<svg width="200" height="72" viewBox="0 0 200 72"><path d="M96 24H14M20 18l-7 6 7 6" stroke="#3b82f6" stroke-width="2.4" fill="none" stroke-linecap="round"/><path d="M104 24h82M180 18l7 6-7 6" stroke="#e0a106" stroke-width="2.4" fill="none" stroke-linecap="round"/><path d="M100 6v40" stroke="#3d3d3d" stroke-width="2"/><text x="100" y="4" font-size="10" text-anchor="middle" fill="#3d3d3d">0</text><text x="46" y="44" font-size="10.5" font-weight="600" text-anchor="middle" fill="#125aab">Negative</text><text x="46" y="58" font-size="10" text-anchor="middle" fill="#585858">= recorded early</text><text x="154" y="44" font-size="10.5" font-weight="600" text-anchor="middle" fill="#b45708">Positive</text><text x="154" y="58" font-size="10" text-anchor="middle" fill="#585858">= recorded late</text></svg>
<!-- size legend -->
<svg width="200" height="72" viewBox="0 0 200 72"><circle cx="34" cy="28" r="5" fill="#125aab"/><circle cx="100" cy="28" r="10" fill="#125aab"/><circle cx="166" cy="28" r="16" fill="#3d3d3d"/><text x="34" y="62" font-size="10.5" text-anchor="middle" fill="#585858">Small</text><text x="100" y="62" font-size="10.5" text-anchor="middle" fill="#585858">Medium</text><text x="166" y="62" font-size="10.5" text-anchor="middle" fill="#585858">Large</text></svg>
<!-- highlighted vs normal marks -->
<svg width="200" height="72" viewBox="0 0 200 72"><g fill="#e0a106"><rect x="4" y="8" width="12" height="46" rx="2"/><rect x="20" y="11" width="12" height="43" rx="2"/><rect x="36" y="14" width="12" height="40" rx="2"/><rect x="52" y="16" width="12" height="38" rx="2"/></g><g fill="#125aab" opacity=".3"><rect x="72" y="18" width="12" height="36" rx="2"/><rect x="88" y="21" width="12" height="33" rx="2"/><rect x="104" y="23" width="12" height="31" rx="2"/><rect x="120" y="26" width="12" height="28" rx="2"/><rect x="136" y="28" width="12" height="26" rx="2"/><rect x="152" y="30" width="12" height="24" rx="2"/></g></svg>
```
Adapt shape and labels to the actual chart (a matrix gets a tiny 3x4 grid of shaded cells; a
lollipop gets two or three stems with dots; a scatter gets dots in quadrants), always with the
chart's real colours and real reference values. Inline SVG only - no images, no external
references. SVG `<text>` must be plain text.

**8. Every chart is interactive (NEW in v3).** Hover or keyboard focus on any mark shows that
mark's real values - at minimum the licence type name and the real fields that place it. Use an SVG
`<title>` child on each mark, or a small CSS tooltip (`.tip`, shown on `:hover`/`:focus-within`,
white card, 1px `--c-border`, radius `--r-md`, small shadow). Where the composition asks for it,
add a sort toggle, filter chip or search box. Interactivity only reveals values that are already
in the data - it never computes a new number. Build large charts from the real rows with a small
inline script using `createElement`/`setAttribute`/`textContent` (never `innerHTML` with a literal
tag), or write the SVG marks out directly - both are fine.

**9. Chart craft rules (NEW in v3, found in the first real Minda renders).**
- **Circles stay circles.** Never use `preserveAspectRatio="none"` on an SVG that contains dots,
  and never stretch a chart SVG with CSS `width:100%` + fixed `height` + a mismatched `viewBox`. A
  script-built chart reads its container's real `clientWidth` and sets the SVG `width`/`height`
  and `viewBox` to the same pixel size, then draws in those pixels.
- **Dots never pile up.** A beeswarm/dot strip spreads dots vertically so they do not overlap
  (simple deterministic stacking: sort by x, place each dot in the first vertical slot where it
  does not collide), with enough chart height for the real number of dots.
- **Labels never sit on marks.** Reference-line labels (e.g. the median chip) go above the plot
  area or in clear space; the zero/due-date label and the median label never overlap each other.
- **Highlight colour.** Named examples called out for attention (worst licence types, named licence types) use
  amber `--warn-fill`, with the rest of the marks in `--c-brand` - exactly like the reference
  "Named high-load examples" chart. Red (`--bad-fill`) is only for genuine severity (overdue,
  critical), never for "this is a named example".
- **Reader language, not internal language.** Anywhere a reader sees text (titles, legends,
  captions, tooltips, notes, panels), never use internal words: "assertion", "asserted",
  "supplied", "dimension", "row", "marginal", "null", "confounded", "reconciled control totals".
  Say it plainly: "licence types highlighted as needing attention", "no reading", "shown separately and
  never combined", "results can differ because of the mix of roles in each group".


**10. Look and colour - clean white cards, colour where it means something (v4, REVISED 2026-09-27).**
Real feedback: colour belongs in the number tiles, charts and small labels - AND every section card
gets a soft coloured background wash with one faint corner circle, toned by that section's meaning
(liked: "every tab background should be these coloured backgrounds"). The page itself stays plain.
Build every page that way:

```css
/* Page: plain and light. Every section card: a soft wash in its tone + one faint corner circle. */
body{background:#f7f8fb}
.card{position:relative;overflow:hidden;border:1px solid var(--c-border);border-radius:16px;padding:26px 28px;
  box-shadow:0 1px 2px rgba(16,24,40,.04),0 8px 24px rgba(16,24,40,.05)}
.card::before{content:"";position:absolute;right:-70px;top:-70px;width:240px;height:240px;border-radius:50%;pointer-events:none}
.card > *{position:relative}
.card--alert{background:linear-gradient(120deg,#fdf1e2 0%,#fffaf4 45%,#ffffff 100%);border-color:#f1d9b8}
.card--alert::before{background:radial-gradient(circle at 30% 30%,rgba(224,161,6,.16),rgba(224,161,6,.05) 60%,transparent 70%)}
.card--bad{background:linear-gradient(120deg,#fdeceb 0%,#fff7f6 45%,#ffffff 100%);border-color:#f3cfcb}
.card--bad::before{background:radial-gradient(circle at 30% 30%,rgba(196,50,40,.12),rgba(196,50,40,.04) 60%,transparent 70%)}
.card--calm{background:linear-gradient(135deg,#ffffff 0%,#f6f9fe 55%,#eaf2fc 100%);border-color:#dbe6f5}
.card--calm::before{background:radial-gradient(circle at 30% 30%,rgba(18,90,171,.10),rgba(18,90,171,.04) 60%,transparent 70%)}
.card--ok{background:linear-gradient(135deg,#ffffff 0%,#f4fbf6 55%,#e6f5ea 100%);border-color:#cfe8d6}
.card--ok::before{background:radial-gradient(circle at 30% 30%,rgba(22,128,74,.10),rgba(22,128,74,.04) 60%,transparent 70%)}

/* A small coloured label above a card's title naming what kind of finding it is
   ("Estate-wide dependency", "Needs attention", "Working well"). */
.tag{display:inline-block;font-size:12px;font-weight:600;padding:4px 10px;border-radius:8px}
.tag--bad{background:var(--bad-bg);color:var(--bad)}
.tag--warn{background:var(--warn-bg);color:var(--warn)}
.tag--ok{background:var(--ok-bg);color:var(--ok)}
.tag--brand{background:var(--c-light-blue);color:var(--c-brand)}

/* Summary number tiles: this is where most of the page's colour lives. */
.kpi{border-radius:12px;border:1px solid var(--c-border);background:#ffffff;padding:16px 16px 18px}
.kpi-v{font-size:26px;font-weight:700;line-height:1.15;white-space:nowrap}
.kpi--bad{background:var(--bad-bg);border-color:var(--bad-stroke)}
.kpi--bad .kpi-v{color:var(--bad)}
.kpi--warn{background:var(--warn-bg);border-color:var(--warn-stroke)}
.kpi--ok{background:var(--ok-bg);border-color:var(--ok-stroke)}
.kpi--brand{background:var(--c-light-blue);border-color:#c9dcf3}

/* Chart drawing areas: a faint tinted field with a thin border (not a gradient wash). */
.plot{background:#fbfcfe;border:1px solid #e6ecf5;border-radius:12px}

/* One small grey note per card at most, for a caution a reader must not miss. */
.note{background:#f4f6f9;border-radius:10px;padding:12px 14px;font-size:13.5px;color:var(--c-text-2)}
```

Rules:
- EVERY section card carries exactly one tone class, matching its `.tag`: `.card--bad` (clearly
  bad: overdue, prison-term exposure), `.card--alert` (needs attention, at risk), `.card--calm`
  (neutral facts, workload, structure), `.card--ok` (working well). Mix tones down the page as the
  content really is - never every card the same tone unless every section really is. The wash and
  corner circle come only from these classes; no other decorative shapes or page gradients. Text
  never sits on the circle; it is decoration only.
- Give the summary number tiles real colour: a tile gets `.kpi--bad/--warn/--ok` when its number
  genuinely carries that meaning (overdue = bad, unassigned or at risk = warn, on time = ok), and
  one or two neutral headline tiles may use `.kpi--brand`. A row of four tiles should normally show
  two or three tints, not four plain white boxes. A tile's number never wraps onto two lines
  (`white-space:nowrap`; "84.8%" stays together).
- Charts use strong, clear colours from the palette (brand blue, teal, amber, red for bad) - filled
  marks, not pale outlines. Every chart's drawing area sits in a `.plot`.
- Every section card starts with a `.tag` (coloured by the section's real tone), then its title with
  the "i" button, then its description text (section 12), then its visuals.
- Class names above are the contract: use them as written so every report looks alike. You may
  add your own classes alongside them.

**11. Never drop real rows (NEW in v4, found live).** A v4 test render embedded only 156 of 305
real user rows and labelled the view "156 matching", as if the rest had been filtered - 149 real
users with real work silently vanished from every chart and the register. Every chart, grid and
register that claims to cover the population must be built from EVERY row in `dimension_rows` -
the count you show must equal the real row count. If the document is getting long, shorten prose,
decoration and CSS, never the data. The rows themselves are never typed by you - see section 13.

**12. Words on the page - written for a CEO or CFO (NEW in v4, required).** Real feedback: "the
content is a little bit low" and too full of jargon. The narrative you are given now carries 3-5
insight points per section (one per line, separated by `\n`).
- **Show the narrative points as the section's own description - NO "What this means" heading
  (REVISED 2026-09-27).** Real feedback: a "What this means" heading with a bullet list after every
  section looked ugly and repetitive - remove it entirely, never write that heading. Instead put the
  section's narrative points as its description text directly under the section title: one or two
  short paragraphs in plain words (join the points into flowing sentences; keep every number and
  fact, drop nothing true). A point starting "Worth checking:" becomes that card's one `.note` at the
  bottom (e.g. "**Worth checking:** what cover exists for ...") - at most one note per card.
- **Plain words in everything you write yourself** - titles, subtitles, labels, legends, notes,
  tooltips, panel text. Titles say what the reader learns ("Which laws are most overdue?" or
  "Most overdue work sits in three laws"), not what the chart is ("Overdue composition").
- Use the reader's words: "compliances" or "compliance tasks" (never "instances"), "in this report"
  or "in the selected period" (never "scoped"), "all your compliance work" (never "estate"),
  "points above the average" (never "pp"), "people / locations / laws / departments" (never
  "members", "rows", "population", "dimension"). Never show: assertion, finding, data quality flag,
  marginal, denominator, materiality floor, control totals, reconciled, proxy, flow metric.
- No filler: leverage, robust, holistic, landscape, ecosystem, delve, pivotal, crucial, notably,
  underscores, paradigm, granular, actionable insights, stakeholders, going forward, overall.
- **No messages meant for developers or testers - anywhere a reader can see (ADDED 2026-09-27).**
  The reader is a business owner, not the team that built this. Never show: "100% reconciled",
  "reconciled", "verified", "validated", "no fabricated data", "every number traces to the data",
  "checked against the source", "typed assertion", assertion or finding ids (A-..., F-...), field or
  column names (`ScopedInstances`, `OverduePct` ...), proc/SQL/table names, "data_quality", "per the
  composition plan", "as instructed", notes about how the page was built or which rules it follows,
  or any statement about the report's own accuracy. Say what the numbers mean for the business -
  never how they were produced or checked. This applies to chips, badges, footers, tooltips, "i"
  panels and small print too.
- Short sentences - aim under 20 words. Numbers written plainly with what they count.
- Counts are whole numbers ("2 laws", never "2.00"); percentages one decimal ("84.8%").

**13. The data is already on the page - never type rows yourself (NEW in v4, found live).**
Re-typing rows is what dropped 149 of 305 users in one test and timed out in the next. After you
return, the system inserts the real rows and totals into your page, before your first script, as:
`<script type="application/json" id="insights-data">{"dimension":"...","rows":[...],"totals":{...}}</script>`
`rows` is EVERY row of `dimension_rows` with every field, exactly; `totals` is
`dimension_control_totals`. Field names are exactly the ones you were given (e.g. `UserName`,
`Instances`, `ActName`, `OverduePct`).

- Read it at the top of your script:
  `var DATA = JSON.parse(document.getElementById('insights-data').textContent); var ROWS = DATA.rows; var TOTALS = DATA.totals;`
- Build EVERY chart, grid, list and table that shows per-row values from `ROWS`, in script
  (sort, filter, rank, count in script - e.g. `ROWS.slice().sort(function(a,b){return b.Instances - a.Instances;})`).
  Counts like "305 users shown" come from `ROWS.length` / your filter, never typed.
- Never write a row array, a list of names with their values, or per-row numbers into your HTML
  or script. Never write your own `insights-data` element - any you write is removed. This
  replaces section 8's "or write the SVG marks out directly" option for anything built from rows.
- Headline figures in text and summary cards (a total, a percentage, the named top example the
  narrative mentions) are still written directly - they come from the narrative and totals.
- Keep your script small: layout, drawing and interaction only. The script rule still applies:
  every `<` inside a script is followed by a space.
- Handle null fields safely (show "No reading", never 0), and escape nothing yourself when using
  `textContent` - it is always safe for tenant text.
## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references — no external
   stylesheets, scripts, images, fonts; no runtime network calls, no fetch/XHR/WebSocket.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring
   (`<div`, `<p`, etc. inside a JS string) — the sanitizer that runs after you return deletes the
   whole script if it finds one. Build elements via `createElement`/`className`/`textContent`/
   `appendChild` instead of `innerHTML` with a literal tag.
   **[FOUND LIVE 2026-09-27, v3] This includes plain less-than comparisons.** `if(page<pages-1)`
   contains `<p` and deleted a real script outright - every chart on the page rendered empty.
   A second live run then lost its script to `v<0` - DIGITS count too. The rule is simple and has
   no exceptions: **inside a script, every `<` is immediately followed by a space.** Write
   `i < n`, `v < 0`, `page < pages - 1`, `a <= b` is NOT allowed either (write `b >= a` or
   `a < b || a === b`). Check every `<` in every script before returning.
4. Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
   `href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour paired
   with a text label (never colour alone).
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — Licences dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it. **If a figure you want is
not in the fields below, omit that sentence — never substitute a near-neighbour field or an
estimate.**

**Every real field on a licence-type row** (`dimension_rows`), verbatim: `LicenseTypeID`,
`LicenseTypeName` (real free text, escape it), `IsRetired`, `TotalLicences`, one count per
status (they sum to `TotalLicences`): `ActiveLicences`, `Expiring`, `Expired`, `Applied`,
`PendingForReview`, `Rejected`, `ApplicationRejected`, `Terminated`, `NotApplicable`, `OtherStatus`
(Draft, Registered, Registered & Renewal Filed or Validity Expired); then `EndingNext30` (end date in
the next 30 days), `BranchesCovered`, `ExpiredPct`, `OverdueRank`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`), verbatim: `ScopedLicences`,
`TypedLicences`, `Reconciled`, `TenantActiveLicences`, `TenantExpiredLicences`, `TenantExpiredPct`,
`TenantExpiring`, `TenantApplied`, `TenantPendingForReview`, `TenantRejected`,
`TenantApplicationRejected`, `TenantTerminated`, `TenantNotApplicable`, `TenantOtherStatus`,
`LicenceTypesReported`, `LicenceTypesWithLicences`, `UntypedLicences` (all for the period only),
`WindowStart`, `WindowEnd`, and the all-licences context `AllLicences`, `AllActiveLicences`,
`AllExpiredLicences`.

| Fact | Status |
|---|---|
| "{M} licences have an end date in this period" | **REAL** — M = `ScopedLicences`. Always say "end date in this period", never "your licences". |
| "Across all {A} licences, {X} are Active and {E} are Expired" | **REAL** — `AllLicences`, `AllActiveLicences`, `AllExpiredLicences`. Context only - one line, never in a period share or chart. |
| "{N} of {M} licences are Active" | **REAL** — N = `TenantActiveLicences`; M = `ScopedLicences` (both for the period). Per type: `ActiveLicences` of `TotalLicences`. |
| "{N} of {M} licences are Expired ({X}%)" | **REAL** — N = `TenantExpiredLicences`, X = `TenantExpiredPct`; M = `ScopedLicences`. Cross-check against the `A-TENANT` assertion's value; if they disagree, use the assertion value and add a data-quality callout. |
| "{N} licences are Applied / Pending for review / Rejected / Application rejected / Terminated / Not applicable / OtherStatus" | **REAL** — the matching `Tenant*` field (`TenantApplied`, `TenantPendingForReview`, `TenantRejected`, `TenantApplicationRejected`, `TenantTerminated`, `TenantNotApplicable`, `TenantOtherStatus`). **[FOUND LIVE 2026-09-30, REQ-1085] Never sum the per-row status column over `dimension_rows` yourself** — a real report said "8 licences are Pending for review" when the correctly-summed total was 9 (108 rows, only 3 non-zero, easy to drop one by hand). The per-row fields are for naming which TYPE contributes what; the tenant-wide count always comes from its own pre-summed `Tenant*` field. |
| "{N} licences have lapsed / are past their end date / are overdue" | **NOT AVAILABLE** — statuses are recorded statuses, not end-date based. Never say it. |
| "All {M} licences are categorised across {T} types" | **REAL only when `UntypedLicences == 0`** — otherwise state the real untyped count instead. |
| "{N} licences have an end date within the next 30 days (from today)" | **REAL** — `SUM(row.EndingNext30)`. Counted from today, not from the period. |
| "{N} expire within 60/90/7 days" | **NOT AVAILABLE** — only a 30-day window exists. Never quote any other horizon. |
| Subject-clustering claim ("expiries cluster in building/safety, not HR") | **NOT AVAILABLE as an analytic claim.** Note factually only what is literally visible in the real escaped names — never infer a root cause. |
| A per-branch or per-state breakdown | **NOT AVAILABLE** — rows are per-type; `BranchesCovered` is a raw count only. |
| Severity ranking by consequence | **NOT AVAILABLE** — no field ranks types by consequence, rank only by real rate/count. |

## 14. The top of the page - company name, headline, period (v4, REVISED 2026-09-27)

Real feedback: a separate header block (company name + report name + generated date) above the
page looked wrong. The liked layout is: a small blue label, a big headline, one line under it, then
a blue "reporting window" box. Build exactly that:

1. **Small blue label** - the company name, an EXACT copy of `tenant_name` (same spelling, same
   capitals - no `text-transform`, nothing added before or after, no report name). 13px, weight 600,
   letter-spacing .08em, brand colour. This is the only place the company name appears on the page
   (plus `<title>`: "{tenant_name} — {short report name}").
2. **Headline** - the lead finding in plain words (from the composition plan's hero), 44-52px,
   weight 800, dark text, tight line-height (1.05), at most two lines.
3. **One line under it** - one or two plain sentences saying what that means, 17px, secondary text.
4. **Reporting window box** - light blue box (`--c-light-blue` background, #c9dcf3 border, 12px
   radius, small document icon on the left). Bold brand-colour first line: "Reporting window: " +
   `report_period.label` copied exactly (e.g. "Reporting window: Last 30 days · 29 Aug 2026 – 27 Sep
   2026"). Second line: the `window` data_quality meaning in plain words (what is and is not
   counted). When `report_period` is missing or null the first line is "As of " + the `generated_at`
   date, and the second line says the figures are counted as of that day - never invent a range.

No other header block, no report-type label, no badge above or beside these. "Generated {date}"
goes only in the small print at the very bottom of the page.

## 15. Labels never overlap or get cut off (v4, ADDED 2026-09-28)

Found live in real reports: location names colliding above a bar chart and rotated names cut off at
the bottom; law names stacked on top of each other on a date timeline; a map of thin tiles where
names and a "Prison-term exposure" badge were squeezed into unreadable one-letter columns. Every
chart must follow these rules, whatever its shape:

- **Measure, then place.** A chart script draws labels, then measures them
  (`getBBox()`/`getComputedTextLength()` for SVG, `getBoundingClientRect()` for HTML). Any label
  that would touch another label, a mark it does not belong to, or the chart edge is shortened with
  "…" (full name kept in its hover/focus detail and `aria-label`) or hidden - never left
  overlapping. Leave 4px between labels.
- **Small tiles and thin bars carry no text inside.** Print a name or value inside a tile or bar
  only when it fits on at most two lines at full size (roughly 64px wide and 36px tall). Anything
  smaller shows its name and value on hover/focus only. Never break a word into single letters,
  never stack letters vertically, and never put a badge on a tile narrower than the badge; mark such
  a tile with its colour or outline instead and explain it in the legend.
- **Crowded points (timelines, dot plots, scatter, bubble charts).** [FOUND LIVE 2026-10-08] This
  covers two different collisions - the MARKS themselves (circles/dots stacking on top of each
  other so only one is visible) and their LABELS (text overlapping text) - fix both, not just the
  second. A real Departments bubble chart shipped with 4-5 low-volume department marks stacked
  exactly on top of each other with only one label visible - the others were not just unlabeled,
  they were not even visible as separate marks.
  - **Marks:** before drawing, compute each mark's natural (x,y). If a mark's own radius overlaps
    an already-placed mark's radius plus a small gap, search outward in a spiral/ring pattern
    (step the angle, grow the radius each full turn) for the nearest free spot, clamp inside the
    chart's plot area, and draw a short thin connector line from the real (x,y) to the displaced
    position so the point's true value is never misread from its moved position. Never let two
    marks render at the same pixel - a user cannot distinguish or hover a mark fully hidden under
    another one.
  - **Labels:** when labels of nearby points would collide, stagger them into rows or connect
    them with short leader lines. If they still collide, label only the most important points
    (the ones the text above discusses) and put the rest in a list or legend under the chart.
- **Axis labels.** Prefer horizontal labels on a horizontal bar chart (names on the left) over
  rotated labels under vertical bars. If labels must rotate, measure the longest one and reserve
  that much space below the axis so nothing is clipped; otherwise shorten with "…".
- **Group/bracket labels** above a set of bars are shown only if the bracket is wider than the
  text; otherwise the group name moves into the legend.
- After drawing, the script runs one last collision check over the chart's labels and hides any
  label that still overlaps another.

## Self-check before returning

- (2026-09-30) Section 7b: every percentage figure in your own prose (not inside a chart's own
  panel) is wrapped as a `.pf` dotted-underline hover-link with title/description/fraction-box
  built exactly as specified - no raw field name in the panel, no count wrapped, no invented
  percentage-point figure, every `pf-` id unique.
- (v4) Section 15: no two labels overlap and none is cut off at any chart edge; no text inside tiles or bars too small for it; no letters stacked vertically.
- (v4) Section 14: the page starts with the small blue label = exact `tenant_name` and nothing else, then the big headline, one line, then the blue "Reporting window: ..." box.
- (v4) No developer/tester messages anywhere on the page: no "reconciled", "verified", "no fabricated data", ids, field names or notes on how the report was built.
- (v4) Section 12: no "What this means" heading anywhere; each section's narrative points are its
  description text under the title, every fact kept; at most one `.note` per card; plain words.- (v4) Section 13: every per-row chart/table/grid is built from `ROWS` read out of
  `#insights-data`; no row data typed anywhere in the page; shown counts come from `ROWS`.- (v4) Section 11: the number of rows embedded in the page equals the number of rows in
  `dimension_rows` - count them before returning.- (v4) Section 10: every section card has one `.card--bad/--alert/--calm/--ok` tone class (soft wash + corner circle) matching its `.tag`; colour also in `.kpi` tiles (two or three tints per row) and charts; tile numbers never wrap.
- (v3) EVERY chart has an "i" button with a "How to read this chart" panel built with the exact
  `.hr` mechanism above; every visible component of that chart (marks, axes, size, every colour,
  every reference line with its value, highlights, scale, cards, data notes) has its own row; the
  swatch colours match the chart's real colours; each panel id is unique.
- (v3) Every chart shows real values on hover/focus.
- (v3) Section 9: dots are round (no stretched SVG), beeswarm dots do not overlap, labels do not
  sit on marks, named examples are amber not red, no internal words anywhere a reader sees.
- (v3) Every `.hr-ico` is one of the SVG icons above or a colour swatch - no text glyphs; at least
  half the rows of every panel carry a `.hr-viz` mini SVG.
- (v3) Every `<` inside every `<script>` is immediately followed by a space - never by a letter,
  digit, `=`, `/`, `!` or `?`.
- Any complete-register/all-members table sits inside `.table-scroll` (fixed max-height, its own
  internal scroll, sticky header) - the page itself never grows to fit every row.
- Every real licence-type row with meaningful volume is represented somewhere.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- (v8) Plain language: no word from the "Never write" column of the licence wording table anywhere a reader sees
  (including chart panels, tooltips, badges and KPI labels); no "True"/"False"; access and left-out-record
  rules sit in an "i" / Details panel with one plain sentence in the main flow; same sections and charts as
  the plan - only the words changed.
- (v7) The word "RegTrack" appears nowhere a reader sees; no sentence explains how a status is worked out; no
  "may no longer be valid" / "operating without a licence" speculation; the lead is renewal progress, not "none are
  Active"; no section or chart rests on a difference of one or two licences (reader rule 3).
- (v6) The period is stated once near the top as "licences whose end date falls in this period"; the
  all-licences context is one line only; a period with 0 licences shows no empty charts.
- No expiry horizon other than 30 days, no subject-clustering root-cause claim, no consequence
  ranking; every status count uses its status word and matches its column; no "lapsed",
  "overdue" or "past end date" claim anywhere.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated, not buried or dropped.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

# Report Generation — EvidenceIntegrity, freehand (v1, 2026-10-04)

**[Ownership is not a finding (RegTrack parity).** Never build a section, chart, card, KPI,
sentence, recommendation or action about ownership, missing owners, unassigned performers or
"nobody is accountable".

**Two more rules.**
1. **Never use a vague umbrella label without naming what is inside it, every time you use it.**
2. **Say each fact once.** Nothing in this file fixes a specific set of section or tile titles,
   their count or their order - keep choosing freely, from `composition_plan`'s own real structure.

**This dimension has NOTHING to do with document evidence.** `EvidenceInSql` is always `false` -
no PDF, certificate, photo or attachment is ever checked here. The real data is a proxy: whether a
closed schedule has more than one recorded transaction row (a "review trail") before closure.
**Never write "evidence attached", "documentation on file", "supporting documents", "verified
evidence" or anything implying a file was inspected, anywhere on this page** - titles, cards,
tooltips, panels, narrative text, everything. Say "recorded review steps" or "transaction history",
never "evidence" alone without immediately qualifying it as being about recorded activity, not
documents.

Your job is to build what `composition_plan` describes - real markup, real CSS, real layout,
matching what `composition_plan.blocks[].emphasis` asks for. You have genuine freedom over
illustration choice, layout, and visual treatment — this is deliberately not a fixed document shape.

## The only things that are NOT yours to change — the shared theme

**1. Font.** `font-family:'Poppins',sans-serif` on `body`, and on every numeric element via a
`.tnum`-style class (`font-variant-numeric: tabular-nums` — never a monospace font-family for
numbers). Do not declare `@font-face` yourself — real vendored Poppins bytes are injected
automatically after you return. Just write the family name.

**2. Colour palette.** Declare these exact tokens in `:root` and build every surface from them:
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
is `--c-brand`. Add as many new tokens as you like for anything these don't cover; you may not
redefine these.

**3. Button/pill/chip sizing.**
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`).

**5. Tabs.** With only 2 real rows, tabs are unlikely to be needed - a single scroll is probably
right.

**6. Long tables.** Not expected for this dimension - a plain 2-row table, if you build one, needs
no scroll container.

**7. The "i" button and the "How to read this chart" panel (required on EVERY chart).**
Every chart gets a small round "i" button right after its title. Hovering the "i" opens a panel on
the right side of the screen titled **"How to read this chart"**; moving the mouse away closes it.
Clicking the "i" pins the panel open until the reader clicks the close X (or presses Escape).

Every number inside the examples below is ILLUSTRATIVE ONLY - never copy one onto the page; use
this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences - make clear this is about recorded review steps, never documents)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each segment = one group of closures</strong><span>The size shows how many closures had more than one recorded review step.</span></div>
        <div class="hr-viz"><!-- optional mini inline-SVG illustration --></div>
      </div>
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

Add this one small script once, at the end of `<body>`:
```js
document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
document.querySelectorAll('.hr-close').forEach(function(btn){btn.addEventListener('click',function(){var hr=btn.closest('.hr');if(!hr)return;hr.classList.add('hr-just-closed');hr.addEventListener('mouseleave',function onLeave(){hr.classList.remove('hr-just-closed');hr.removeEventListener('mouseleave',onLeave);});});});
```

Each chart's `id` (`hr-load` above) must be unique on the page.

**7b. The review-trail percentage becomes a hover-link to its formula.** Wherever
`ClosuresWithReviewTrailPct` appears in your OWN written prose, wrap just that figure as a
hover-link to a small popup showing how it is worked out.

**Scope - this one field only, and only when it is real (non-null).** Never wrap any other number
on this page as a percentage - there is only this one real percentage field.

```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pf-{unique}" aria-label="How this percentage is worked out">
  <label for="pf-{unique}" class="hr-i pf">(68.4%)</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
    <label for="pf-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">Closures with a recorded review trail - 68.4%</span>
    <span class="hr-intro pf-intro">The percentage of closed compliance schedules that had more than one recorded transaction before closing.</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-frac">
        <span class="pf-frac-stack">
          <span class="pf-num"><span class="pf-num-value">171</span><span class="pf-num-label">Closures with more than one recorded step</span></span>
          <span class="pf-den"><span class="pf-den-value">250</span><span class="pf-den-label">Total closed schedules</span></span>
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

**The exact field eligible for this hover-link:**

| Field(s) | Title | Description | Numerator label | Denominator label |
|---|---|---|---|---|
| `ClosuresWithReviewTrailPct` | Closures with a recorded review trail | "The percentage of closed compliance schedules that had more than one recorded transaction before closing." | Closures with more than one recorded step | Total closed schedules |

Rules:
- **Only `span` tags inside `.hr`, ever** - never `aside`, `div`, `h4`, or `p`.
- **Numerator/denominator show the REAL NUMBER first, then its caption underneath** - never a raw
  field name. The two real numbers, divided and multiplied by 100, must equal the percentage shown.
- **Never wrap this when `ClosuresWithReviewTrailPct` is null** (`SumOfRows == 0`).
- Each `id` (`pf-{unique}` above) must be unique on the page.

**8. Every chart is interactive.** Hover or keyboard focus on any mark shows that mark's real
values - at minimum the bucket name and its `ScheduleCount`. Interactivity only reveals values
already in the data - it never computes a new number.

**9. Chart craft rules.**
- **Circles stay circles.**
- **Labels never sit on marks.**
- **Highlight colour.** `has_trail` in `--ok-fill` or `--c-brand`, `single_row_only` in a neutral
  tone - never red; a single-step closure is not automatically a severity, just a different fact.
- **Reader language, not internal language.**

**10. Look and colour - clean white cards, colour where it means something.**

```css
body{background:#f7f8fb}
.card{position:relative;overflow:hidden;border:1px solid var(--c-border);border-radius:16px;padding:26px 28px;
  box-shadow:0 1px 2px rgba(16,24,40,.04),0 8px 24px rgba(16,24,40,.05)}
.card::before{content:"";position:absolute;right:-70px;top:-70px;width:240px;height:240px;border-radius:50%;pointer-events:none}
.card > *{position:relative}
.card--calm{background:linear-gradient(135deg,#ffffff 0%,#f6f9fe 55%,#eaf2fc 100%);border-color:#dbe6f5}
.card--calm::before{background:radial-gradient(circle at 30% 30%,rgba(18,90,171,.10),rgba(18,90,171,.04) 60%,transparent 70%)}
.card--ok{background:linear-gradient(135deg,#ffffff 0%,#f4fbf6 55%,#e6f5ea 100%);border-color:#cfe8d6}
.card--ok::before{background:radial-gradient(circle at 30% 30%,rgba(22,128,74,.10),rgba(22,128,74,.04) 60%,transparent 70%)}

.tag{display:inline-block;font-size:12px;font-weight:600;padding:4px 10px;border-radius:8px}
.tag--ok{background:var(--ok-bg);color:var(--ok)}
.tag--brand{background:var(--c-light-blue);color:var(--c-brand)}

.kpi{border-radius:12px;border:1px solid var(--c-border);background:#ffffff;padding:16px 16px 18px}
.kpi-v{font-size:26px;font-weight:700;line-height:1.15;white-space:nowrap}
.kpi--ok{background:var(--ok-bg);border-color:var(--ok-stroke)}
.kpi--brand{background:var(--c-light-blue);border-color:#c9dcf3}

.plot{background:#fbfcfe;border:1px solid #e6ecf5;border-radius:12px}
.note{background:#f4f6f9;border-radius:10px;padding:12px 14px;font-size:13.5px;color:var(--c-text-2)}
```

Rules:
- EVERY section card carries exactly one tone class: `.card--ok` (a high review-trail share, framed
  positively) or `.card--calm` (neutral framing, or a high single-step share framed as fact, not
  failure). This dimension has no real `--bad`/`--alert` signal - never use them here.
- Give the summary number tiles real colour: `.kpi--ok` for the review-trail share, `.kpi--brand`
  for neutral volume figures.
- Every section card starts with a `.tag`, then its title with the "i" button, then its description
  text, then its visuals.

**11. Never drop real rows.** Both buckets must be represented somewhere on the page.

**12. Words on the page - written for a CEO or CFO.**
- **Show the narrative points as the section's own description - NO "What this means" heading.**
- **Plain words in everything you write yourself.**
- Use the reader's words: "recorded review steps" or "transaction history" (NEVER "evidence"
  without qualifying it), "closed compliance work" (never "closures" alone the first time, "closed
  schedules" after). Never show: assertion, finding, data quality flag, marginal, denominator,
  materiality floor, control totals, reconciled, proxy, flow metric.
- No filler: leverage, robust, holistic, landscape, ecosystem, delve, pivotal, crucial, notably,
  underscores, paradigm, granular, actionable insights, stakeholders, going forward, overall.
- **No messages meant for developers or testers.** Never show: "100% reconciled", "reconciled",
  "verified", "validated", "no fabricated data", assertion or finding ids, field or column names
  (`TrailBucket`, `ScheduleCount` ...), proc/SQL/table names, "data_quality", or any statement about
  the report's own accuracy.
- Short sentences - aim under 20 words. Counts are whole numbers; percentages one decimal.
- **Vary your wording - never repeat a long compound phrase.** Say a precise description once, in
  full. Every time after that, use a short plain stand-in - never the same multi-word technical
  phrase three or more times on one page.

**13. The data is already on the page - never type rows yourself.** After you return, the system
inserts the real rows and totals into your page as:
`<script type="application/json" id="insights-data">{"dimension":"...","rows":[...],"totals":{...}}</script>`

- Read it at the top of your script:
  `var DATA = JSON.parse(document.getElementById('insights-data').textContent); var ROWS = DATA.rows; var TOTALS = DATA.totals;`
- Headline figures in text and summary cards are still written directly.

## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring. **Every `<`
   inside a script is immediately followed by a space.** Write `i < n`, `v < 0`, `a < b || a ===
   b` (never `a <= b`). Check every `<` in every script before returning.
4. Escape all tenant-entered text. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered
   headings, colour paired with a text label.
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — EvidenceIntegrity dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a trail-bucket row** (`dimension_rows`): `TrailBucket`, `ScheduleCount`.

**Every real tenant-level total** (`dimension_control_totals`): `CustomerID`, `AsOfUtc`,
`SumOfRows`, `DistinctClosedSchedules`, `ClosuresWithReviewTrailPct` (nullable), `EvidenceInSql`
(always `false`).

| Fact | Status |
|---|---|
| Document evidence | **NEVER AVAILABLE, ALWAYS `false`.** This dimension cannot speak to whether a document, attachment, or file exists anywhere. The real signal is recorded transaction count only. |
| Action/finding owners | **Real ROLE names only** — Performer, Reviewer, Compliance Officer, Compliance Owner. Never a specific person's name (none is given) or an invented team name. |
| A single-step closure | **Not automatically a problem.** A genuinely simple compliance task can correctly close in one step - state it as fact, never as a failure. |

## The scope — read this before writing the scope line

**This dimension has no caller-supplied period.** State plainly, using any real `data_quality`
entry's own `detail` text where one exists, that this view counts closed schedules and their
recorded transaction history, never invented.

## 14. The top of the page - company name, headline, period

1. **Small blue label** - the company name, an EXACT copy of `tenant_name`. 13px, weight 600,
   letter-spacing .08em, brand colour. Only place the company name appears on the page (plus
   `<title>`: "{tenant_name} — {short report name}").
2. **Headline** - the lead finding in plain words (from the composition plan's hero), 44-52px,
   weight 800, dark text, tight line-height (1.05), at most two lines.
3. **One line under it** - one or two plain sentences, 17px, secondary text.
4. **Info box** - light blue box (`--c-light-blue` background, #c9dcf3 border, 12px radius, small
   document icon on the left). Bold brand-colour first line naming what this view covers. Second
   line: the real `EvidenceInSql == false` caveat in plain words - this is about recorded review
   steps, not documents.

No other header block. "Generated {date}" goes only in the small print at the very bottom.

## 15. Labels never overlap or get cut off

- **Measure, then place.** Any label that would touch another label or the chart edge is shortened
  with "…" or hidden - never left overlapping.

## Self-check before returning

- **Nowhere on the page claims or implies document evidence, attachments, or files exist or were
  checked.** This is the single most important check for this dimension - re-read every sentence.
- The `ClosuresWithReviewTrailPct` figure, if stated in prose, is wrapped as a `.pf` dotted-underline
  hover-link with real numbers that reconcile, never wrapped when null.
- Section 14: the page starts with the small blue label = exact `tenant_name`, then the big
  headline, one line, then the blue info box.
- No developer/tester messages anywhere on the page.
- Section 12: no "What this means" heading anywhere; narrative points are the description text;
  plain words.
- Section 11: both buckets are represented somewhere on the page.
- Section 10: every section card uses only `.card--ok`/`.card--calm` - never `--bad`/`--alert`.
- EVERY chart has an "i" button with a "How to read this chart" panel.
- Every chart shows real values on hover/focus.
- Every `<` inside every `<script>` is immediately followed by a space.
- Every number on the page exists in `dimension_rows` or `dimension_control_totals`.
- No invented owner name.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles.
- Single HTML document, no external references, no runtime network calls, no tenant name/id leak.

Return the HTML document only.

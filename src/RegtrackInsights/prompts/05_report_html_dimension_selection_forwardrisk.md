# Report Generation — ForwardRisk, freehand (v1, 2026-10-04)

**[Ownership is not a finding (RegTrack parity).** Insights now counts exactly what RegTrack's own
reports count, and RegTrack only lists compliances that have an active performer. Never build a
section, chart, card, KPI, sentence, recommendation or action about ownership, missing owners,
unassigned performers or "nobody is accountable".

**Two more rules.**
1. **Never use a vague umbrella label without naming what is inside it, every time you use it.**
2. **Say each fact once.** Nothing in this file fixes a specific set of section or tile titles,
   their count or their order - keep choosing freely, from `composition_plan`'s own real structure.

This dimension's real meaning: `DueInWindow` (and its `CarriedForward`/`CleanAtRisk`/`Healthy`
segments) is a count of obligations already scheduled inside a FIXED 90-day forward horizon
(`HorizonDays`) — never a forecast or a probability. Build the page accordingly: present tense,
facts already true, never predictive language ("likely to", "may become", "at risk of becoming").

Your job is to build what `composition_plan` describes - real markup, real CSS, real layout,
matching what `composition_plan.blocks[].emphasis` asks for. You have genuine freedom over
illustration choice, layout, and visual treatment — this is deliberately not a fixed document shape.

## The only things that are NOT yours to change — the shared theme

Everything else about presentation is open. These keep the report recognizably the same product
across every tenant and every dimension:

**1. Font.** `font-family:'Poppins',sans-serif` on `body`, and on every numeric element via a
`.tnum`-style class (`font-variant-numeric: tabular-nums` — never a monospace font-family for
numbers). Do not declare `@font-face` yourself — real vendored Poppins bytes are injected
automatically after you return. Just write the family name.

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
is `--c-brand`; `--ok`/`--warn`/`--bad` are the real severity semantics — never re-purposed for
anything else. Add as many new tokens as you like for anything these don't cover; you may not
redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control uses this real spacing/radius
scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for.

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
dimension (a "complete register," "all branches," or similar - not a capped top-5/top-10 list) goes
inside a fixed-height container with its OWN internal scroll:
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

**7. The "i" button and the "How to read this chart" panel (required on EVERY chart).**
Every chart, graph, heatmap, distribution, scatter, dot strip or other visual gets a small round
"i" button right after its title. Hovering the "i" opens a panel on the right side of the screen
titled **"How to read this chart"**; moving the mouse away closes it (the panel stays open while
the mouse is over the "i" OR over the panel itself). Clicking the "i" pins the panel open until the
reader clicks the close X (or presses Escape). Tables do not need one; charts always do.

Every number inside the examples below is ILLUSTRATIVE ONLY - never copy one onto the page; use
this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title - all 42 branches in this view</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences: what it shows and what question it answers)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each bar = one branch</strong><span>The length of each segment shows how much of that branch's forward workload falls in each category.</span></div>
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

Add this one small script once, at the end of `<body>`, so Escape closes any pinned panel, and so
the close [x] actually hides the panel right away and KEEPS it hidden while the pointer rests on it:
```js
document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
document.querySelectorAll('.hr-close').forEach(function(btn){btn.addEventListener('click',function(){var hr=btn.closest('.hr');if(!hr)return;hr.classList.add('hr-just-closed');hr.addEventListener('mouseleave',function onLeave(){hr.classList.remove('hr-just-closed');hr.removeEventListener('mouseleave',onLeave);});});});
```

Each chart's `id` (`hr-load` above) must be unique on the page.

**7b. Percentage figures become a hover-link to their formula.** Wherever a percentage figure
appears in your OWN written prose - a headline, a section's description text, a card's `.note` -
wrap just that figure as a hover-link to a small popup showing how it is worked out.

**Scope - percentages only, never counts.** This dimension has two real percentage shapes: a
branch's own clean-at-risk rate, and a branch's own carried-forward rate. Never wrap a plain count
("12 branches, 340 obligations") - only a number that is itself a percentage figure, and only one
of the fields below. If you never write one of these percentages in your prose this run, this
section produces nothing - never invent one to have something to wrap.

Reuse the SAME `.hr` mechanism section 7 already requires (same checkbox/label/panel, same
hover-to-preview / click-to-pin / Escape-to-close) - just styled and triggered differently: the
trigger is the percentage text itself (dotted underline, not a round "i" badge), and the panel body
is a title, one plain sentence, and a fraction formula box instead of `.hr-row`s.

**Every tag inside `.hr` here must be `span`, never `aside`/`div`/`h4`/`p`** - this block sits
INSIDE a `<p>` of running prose. A browser auto-closes a `<p>` the instant it meets a BLOCK-level
start tag inside it - the panel silently ends up as a sibling of your paragraph instead of nested
inside `.hr`, so it renders sanely as flat text but the popup can never open.

```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pf-{unique}" aria-label="How this percentage is worked out">
  <label for="pf-{unique}" class="hr-i pf">(12.3%)</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
    <label for="pf-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">Clean-at-risk percentage - 12.3%</span>
    <span class="hr-intro pf-intro">The percentage of Mumbai Warehouse's forward-window obligations that carry a preventable risk factor but are not yet overdue.</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-frac">
        <span class="pf-frac-stack">
          <span class="pf-num"><span class="pf-num-value">8</span><span class="pf-num-label">Clean-at-risk obligations (this branch)</span></span>
          <span class="pf-den"><span class="pf-den-value">65</span><span class="pf-den-label">Obligations due in window (this branch)</span></span>
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

`.pf-panel` MUST stay `position:fixed`, with its `top`/`left` set by the small script below, not by
CSS alone - every section card is `overflow:hidden` (section 10), and a `position:absolute` popup
nested inside one gets silently clipped once it grows past the card's remaining space.

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

**The exact fields eligible for this hover-link - copy this table's wording, never invent a new
percentage or a new formula:**

| Field(s) | Title | Description (real scope substituted in) | Numerator label | Denominator label |
|---|---|---|---|---|
| `CleanAtRiskPct` (a branch row) | Clean-at-risk percentage | "The percentage of {BranchName}'s forward-window obligations that carry a preventable risk factor but are not yet overdue." | Clean-at-risk obligations (this branch) | Obligations due in window (this branch) |
| `CarriedForwardPct` (a branch row) | Carried-forward percentage | "The percentage of {BranchName}'s forward-window obligations that are already overdue and recurring into this window." | Carried-forward obligations (this branch) | Obligations due in window (this branch) |

Rules:
- **Only `span` tags inside `.hr`, ever** - never `aside`, `div`, `h4`, or `p`.
- **Title** = "{Field's own name above} - {the real value}%".
- **Description** = ONE plain sentence naming the real scope - the exact wording given in the table
  above, with the real branch name substituted in.
- **Numerator/denominator show the REAL NUMBER first, then its caption underneath** - never a raw
  field name anywhere the reader can see. The two real numbers you write, divided and multiplied by
  100, must equal the percentage in the title - if they do not, you have the wrong scope's numbers,
  fix it before returning.
- **Wrap the figure only at its one home appearance** - never re-wrap the same value if it
  legitimately repeats inside a chart's own hover/focus detail.
- Each `id` (`pf-{unique}` above) must be unique on the page.

**8. Every chart is interactive.** Hover or keyboard focus on any mark shows that mark's real
values - at minimum the branch name and the real fields that place it. Use an SVG `<title>` child on
each mark, or a small CSS tooltip (`.tip`, shown on `:hover`/`:focus-within`, white card, 1px
`--c-border`, radius `--r-md`, small shadow). Interactivity only reveals values that are already in
the data - it never computes a new number. Build large charts from the real rows with a small inline
script using `createElement`/`setAttribute`/`textContent` (never `innerHTML` with a literal tag), or
write the SVG marks out directly - both are fine.

**9. Chart craft rules.**
- **Circles stay circles.** Never use `preserveAspectRatio="none"` on an SVG that contains dots,
  and never stretch a chart SVG with CSS `width:100%` + fixed `height` + a mismatched `viewBox`. A
  script-built chart reads its container's real `clientWidth` and sets the SVG `width`/`height`
  and `viewBox` to the same pixel size, then draws in those pixels.
- **Dots never pile up.** A beeswarm/dot strip spreads dots vertically so they do not overlap.
- **Labels never sit on marks.** Reference-line labels go above the plot area or in clear space.
- **Highlight colour.** Branches called out for attention use amber `--warn-fill`, the rest in
  `--c-brand`. Red (`--bad-fill`) is only for genuine severity (carried-forward, imprisonment
  exposure), never for "this is a named example".
- **Reader language, not internal language.** Never use internal words: "assertion", "asserted",
  "supplied", "dimension", "row", "marginal", "null", "confounded", "reconciled control totals".

**10. Look and colour - clean white cards, colour where it means something.**

```css
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

.tag{display:inline-block;font-size:12px;font-weight:600;padding:4px 10px;border-radius:8px}
.tag--bad{background:var(--bad-bg);color:var(--bad)}
.tag--warn{background:var(--warn-bg);color:var(--warn)}
.tag--ok{background:var(--ok-bg);color:var(--ok)}
.tag--brand{background:var(--c-light-blue);color:var(--c-brand)}

.kpi{border-radius:12px;border:1px solid var(--c-border);background:#ffffff;padding:16px 16px 18px}
.kpi-v{font-size:26px;font-weight:700;line-height:1.15;white-space:nowrap}
.kpi--bad{background:var(--bad-bg);border-color:var(--bad-stroke)}
.kpi--bad .kpi-v{color:var(--bad)}
.kpi--warn{background:var(--warn-bg);border-color:var(--warn-stroke)}
.kpi--ok{background:var(--ok-bg);border-color:var(--ok-stroke)}
.kpi--brand{background:var(--c-light-blue);border-color:#c9dcf3}

.plot{background:#fbfcfe;border:1px solid #e6ecf5;border-radius:12px}
.note{background:#f4f6f9;border-radius:10px;padding:12px 14px;font-size:13.5px;color:var(--c-text-2)}
```

Rules:
- EVERY section card carries exactly one tone class, matching its `.tag`: `.card--bad` (carried
  forward, imprisonment/critical exposure), `.card--alert` (clean-at-risk, needs attention),
  `.card--calm` (neutral facts, tenant-wide structure), `.card--ok` (healthy). Mix tones down the
  page as the content really is.
- Give the summary number tiles real colour: a tile gets `.kpi--bad/--warn/--ok` when its number
  genuinely carries that meaning, and one or two neutral headline tiles may use `.kpi--brand`.
- Charts use strong, clear colours from the palette - filled marks, not pale outlines. Every chart's
  drawing area sits in a `.plot`.
- Every section card starts with a `.tag`, then its title with the "i" button, then its description
  text, then its visuals.

**11. Never drop real rows.** Every chart, grid and register that claims to cover the population
must be built from EVERY row in `dimension_rows` - the count you show must equal the real row
count. If the document is getting long, shorten prose, decoration and CSS, never the data. The rows
themselves are never typed by you - see section 13.

**12. Words on the page - written for a CEO or CFO.**
- **Show the narrative points as the section's own description - NO "What this means" heading.**
  Put the section's narrative points as its description text directly under the section title: one
  or two short paragraphs in plain words. A point starting "Worth checking:" becomes that card's one
  `.note` at the bottom - at most one note per card.
- **Plain words in everything you write yourself.** Titles say what the reader learns, not what the
  chart is.
- Use the reader's words: "compliance work" or "obligations" (never "instances"), "due in the next
  {HorizonDays} days" (never "scoped"), never "estate", never "pp" (say "points"), "branches" (never
  "members", "rows", "population", "dimension"). Never show: assertion, finding, data quality flag,
  marginal, denominator, materiality floor, control totals, reconciled, proxy, flow metric.
- No filler: leverage, robust, holistic, landscape, ecosystem, delve, pivotal, crucial, notably,
  underscores, paradigm, granular, actionable insights, stakeholders, going forward, overall.
- **No messages meant for developers or testers - anywhere a reader can see.** Never show: "100%
  reconciled", "reconciled", "verified", "validated", "no fabricated data", "every number traces to
  the data", "checked against the source", "typed assertion", assertion or finding ids, field or
  column names (`DueInWindow`, `CleanAtRiskPct` ...), proc/SQL/table names, "data_quality", "per the
  composition plan", or any statement about the report's own accuracy.
- Short sentences - aim under 20 words. Numbers written plainly with what they count.
- Counts are whole numbers; percentages one decimal ("84.8%").

**13. The data is already on the page - never type rows yourself.** After you return, the system
inserts the real rows and totals into your page, before your first script, as:
`<script type="application/json" id="insights-data">{"dimension":"...","rows":[...],"totals":{...}}</script>`
`rows` is EVERY row of `dimension_rows` with every field, exactly; `totals` is
`dimension_control_totals`.

- Read it at the top of your script:
  `var DATA = JSON.parse(document.getElementById('insights-data').textContent); var ROWS = DATA.rows; var TOTALS = DATA.totals;`
- Build EVERY chart, grid, list and table that shows per-row values from `ROWS`, in script.
  Counts come from `ROWS.length` / your filter, never typed.
- Never write a row array, a list of names with their values, or per-row numbers into your HTML or
  script. Never write your own `insights-data` element - any you write is removed.
- Headline figures in text and summary cards (a total, a percentage, the named top example the
  narrative mentions) are still written directly - they come from the narrative and totals.
- Handle null fields safely (show "No forward work due", never 0 for a branch with no `DueInWindow`),
  and escape nothing yourself when using `textContent` - it is always safe for tenant text.

## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references — no external
   stylesheets, scripts, images, fonts; no runtime network calls, no fetch/XHR/WebSocket.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring — the
   sanitizer that runs after you return deletes the whole script if it finds one. Build elements via
   `createElement`/`className`/`textContent`/`appendChild` instead of `innerHTML` with a literal
   tag. **This includes plain less-than comparisons** - `if(page<pages-1)` contains `<p` and deletes
   the whole script. The rule is simple and has no exceptions: **inside a script, every `<` is
   immediately followed by a space.** Write `i < n`, `v < 0`, `a < b || a === b` (never `a <= b`).
   Check every `<` in every script before returning.
4. Escape all tenant-entered text. Never place it in a `<script>`, `onclick=`, `style=`, or
   `href`/`src`. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered headings, colour paired
   with a text label (never colour alone).
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — ForwardRisk dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a branch row** (`dimension_rows`): `BranchID`, `BranchName`, `DueInWindow`,
`CarriedForward`, `CleanAtRisk`, `Healthy`, `ImprisonmentDue`, `CriticalDue`, `CleanAtRiskPct`,
`CarriedForwardPct`, `CleanAtRiskRank`, `Flags` — includes rows with `DueInWindow == 0`;
`CleanAtRiskPct`/`CarriedForwardPct`/`CleanAtRiskRank` are null on those rows.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `HorizonDays`,
`DueInWindow`, `SumOfRowsDue`, `Reconciled`, `SchedulesInWindow`, `CarriedForward`, `CleanAtRisk`,
`Healthy`, `PredictedAtRisk`, `ImprisonmentNeedingAttention`, `TenantMedianBranchOverduePct`,
`BranchStressThresholdPct`, `BranchesReported`, `BranchesWithNothingDue`, `ForwardWindowEmpty`,
`Method`. `TenantMedianBranchOverduePct`/`BranchStressThresholdPct` are nullable.

| Fact | Status |
|---|---|
| This is not a forecast | **REAL, present-tense facts only.** Every count is an obligation already scheduled inside the fixed horizon, already overdue, or already carrying a real risk factor - never a prediction or probability. |
| Action/finding owners | **Real ROLE names only** — Performer, Reviewer, Compliance Officer, Compliance Owner. Never a specific person's name (none is given) or an invented team name. |
| Two real totals — never conflate | `SumOfRowsDue` (occurrence-grain, per-branch sum) vs `ScopedInstances` (distinct-obligation count) are different real populations — be explicit which one backs any "total forward workload" figure. |

## The forward horizon — read this before writing the scope line

**This dimension has no caller-supplied period.** Unlike most other dimensions, the forward horizon
(`HorizonDays`, carried in `dimension_control_totals`) is a fixed property of the proc itself, not a
picker choice. State the real horizon plainly near the top of the page using the actual
`HorizonDays` value and any real `data_quality` entry's own `detail` text - never invented, never
paraphrased into something vaguer than what the data actually says. This changes what every number
on the page means (a shorter or longer horizon than the reader expects would change `DueInWindow`
entirely) - a caller comparing two runs needs to know the horizon, not assume it.

## 14. The top of the page - company name, headline, period

Real feedback on other dimensions: a separate header block (company name + report name + generated
date) above the page looked wrong. Build: a small blue label, a big headline, one line under it,
then a blue info box. Build exactly that:

1. **Small blue label** - the company name, an EXACT copy of `tenant_name` (same spelling, same
   capitals - no `text-transform`, nothing added before or after, no report name). 13px, weight 600,
   letter-spacing .08em, brand colour. This is the only place the company name appears on the page
   (plus `<title>`: "{tenant_name} — {short report name}").
2. **Headline** - the lead finding in plain words (from the composition plan's hero), 44-52px,
   weight 800, dark text, tight line-height (1.05), at most two lines.
3. **One line under it** - one or two plain sentences saying what that means, 17px, secondary text.
4. **Info box** - light blue box (`--c-light-blue` background, #c9dcf3 border, 12px radius, small
   document icon on the left). Bold brand-colour first line stating the real forward horizon (e.g.
   "Looking ahead: next 90 days"). Second line: what is and is not counted, in plain words, from the
   real `data_quality` text - never invent a range.

No other header block, no report-type label, no badge above or beside these. "Generated {date}"
goes only in the small print at the very bottom of the page.

## 15. Labels never overlap or get cut off

Every chart must follow these rules, whatever its shape:

- **Measure, then place.** A chart script draws labels, then measures them. Any label that would
  touch another label, a mark it does not belong to, or the chart edge is shortened with "…" (full
  name kept in its hover/focus detail and `aria-label`) or hidden - never left overlapping. Leave
  4px between labels.
- **Small tiles and thin bars carry no text inside.** Print a name or value inside a tile or bar
  only when it fits on at most two lines at full size. Anything smaller shows its name and value on
  hover/focus only. Never break a word into single letters.
- **Crowded points.** When labels of nearby points would collide, stagger them into rows or connect
  them with short leader lines, or label only the most important points and put the rest in a
  legend.
- **Axis labels.** Prefer horizontal labels on a horizontal bar chart over rotated labels under
  vertical bars. If labels must rotate, reserve enough space; otherwise shorten with "…".
- After drawing, the script runs one last collision check over the chart's labels and hides any
  label that still overlaps another.

## Self-check before returning

- Every percentage figure in your own prose that matches section 7b's eligible fields is wrapped as
  a `.pf` dotted-underline hover-link with title/description/fraction-box built exactly as
  specified, real numbers that reconcile to the displayed percentage, no raw field name in the
  panel, no count wrapped, every `pf-` id unique.
- Section 15: no two labels overlap and none is cut off at any chart edge.
- Section 14: the page starts with the small blue label = exact `tenant_name` and nothing else, then
  the big headline, one line, then the blue info box.
- No developer/tester messages anywhere on the page: no "reconciled", "verified", "no fabricated
  data", ids, field names or notes on how the report was built.
- Section 12: no "What this means" heading anywhere; each section's narrative points are its
  description text under the title, every fact kept; at most one `.note` per card; plain words.
- Section 13: every per-row chart/table/grid is built from `ROWS` read out of `#insights-data`; no
  row data typed anywhere in the page; shown counts come from `ROWS`.
- Section 11: the number of rows embedded in the page equals the number of rows in
  `dimension_rows` - count them before returning.
- Section 10: every section card has one `.card--bad/--alert/--calm/--ok` tone class matching its
  `.tag`; colour also in `.kpi` tiles and charts; tile numbers never wrap.
- EVERY chart has an "i" button with a "How to read this chart" panel built with the exact `.hr`
  mechanism above; every visible component has its own row; the swatch colours match the chart's
  real colours; each panel id is unique.
- Every chart shows real values on hover/focus.
- Section 9: dots are round, beeswarm dots do not overlap, labels do not sit on marks, named
  examples are amber not red, no internal words anywhere a reader sees.
- Every `<` inside every `<script>` is immediately followed by a space - never by a letter, digit,
  `=`, `/`, `!` or `?`.
- Any complete-register/all-members table sits inside `.table-scroll` (fixed max-height, its own
  internal scroll, sticky header) - the page itself never grows to fit every row.
- Every real branch row is represented somewhere, none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No invented owner name, no population-count conflation (same claimed quantity stated as two
  different numbers anywhere).
- No predictive language anywhere - every count is a present-tense fact already true.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT.
- Font is Poppins (numbers use tabular-nums, never monospace), the palette/sizing tokens above are
  declared and used for their stated roles, any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

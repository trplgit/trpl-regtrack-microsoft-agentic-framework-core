# Report Generation — CoverageGaps, freehand (v1, 2026-10-04)

**[Ownership is not a finding (RegTrack parity).** Never build a section, chart, card, KPI,
sentence, recommendation or action about ownership, missing owners, unassigned performers or
"nobody is accountable".

**Two more rules.**
1. **Never use a vague umbrella label without naming what is inside it, every time you use it.**
2. **Say each fact once.** Nothing in this file fixes a specific set of section or tile titles,
   their count or their order - keep choosing freely, from `composition_plan`'s own real structure.

This dimension's real meaning: a branch is flagged when its labour-obligation count sits well below
its peer group's median. **These are review candidates, never confirmed violations** — the real data
carries its own caveat that a genuine exemption can explain any single gap. Build the page with that
framing throughout: "worth checking", "a pattern worth reviewing" — never "this branch is not
compliant" or similar.

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
redefine these. Given this dimension's "review candidate, not violation" framing, lean on `--warn`
(amber) rather than `--bad` (red) for most gap-related tiles/tags — reserve `--bad` only if the
composition plan genuinely calls out something severe (e.g. a very large, full-confidence gap
concentration), never as the default tone for "has a gap."

**3. Button/pill/chip sizing.**
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for.

**5. Tabs, if you use them.**
```css
.di-tabnav{display:inline-flex;align-items:center;gap:4px;padding:4px 6px;border-radius:9px;background:var(--c-mist)}
.di-tab{cursor:pointer;user-select:none;display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border-radius:7px;color:var(--c-grey);font-weight:500;border:1.25px solid transparent}
.di-tab:hover{color:var(--c-brand)}
/* active tab: color:var(--c-brand); border-color:var(--c-brand); font-weight:600 */
```
Use a CSS-only radio-driven tab mechanism if you build tabs (input elements nested INSIDE the
element your `:has()` selectors target — not as preceding siblings, which silently breaks
`:has()`).

**6. Long tables — contained, never page-growing.** Any table listing every real leaf branch (a
"complete register," "all branches," or similar - not a capped top-5/top-10 list) goes inside a
fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range). Sticky header keeps column labels visible while scrolling. A live search input
above the table and clickable per-column sort are a real, approved enhancement for a large register.

**7. The "i" button and the "How to read this chart" panel (required on EVERY chart).**
Every chart, graph, heatmap, distribution, scatter, dot strip or other visual gets a small round
"i" button right after its title. Hovering the "i" opens a panel on the right side of the screen
titled **"How to read this chart"**; moving the mouse away closes it. Clicking the "i" pins the
panel open until the reader clicks the close X (or presses Escape). Tables do not need one; charts
always do.

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
        <div class="hr-txt"><strong>Each bar = one branch</strong><span>The length of each bar shows how far below its peer median this branch sits.</span></div>
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
appears in your OWN written prose, wrap just that figure as a hover-link to a small popup showing
how it is worked out.

**Scope - percentages only, never counts.** This dimension has one real percentage shape: a
branch's own share of its peer median. Never wrap a plain count - only a number that is itself a
percentage figure, and only the field below. If you never write this percentage in your prose this
run, this section produces nothing - never invent one to have something to wrap.

Reuse the SAME `.hr` mechanism section 7 already requires - just styled and triggered differently:
the trigger is the percentage text itself (dotted underline, not a round "i" badge), and the panel
body is a title, one plain sentence, and a fraction formula box instead of `.hr-row`s.

**Every tag inside `.hr` here must be `span`, never `aside`/`div`/`h4`/`p`** - this block sits
INSIDE a `<p>` of running prose.

```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pf-{unique}" aria-label="How this percentage is worked out">
  <label for="pf-{unique}" class="hr-i pf">(64.2%)</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
    <label for="pf-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">Share of peer median - 64.2%</span>
    <span class="hr-intro pf-intro">Mumbai Warehouse's labour obligation count as a share of the median count across its peer branches (same state, same establishment class).</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-frac">
        <span class="pf-frac-stack">
          <span class="pf-num"><span class="pf-num-value">9</span><span class="pf-num-label">Labour obligations (this branch)</span></span>
          <span class="pf-den"><span class="pf-den-value">14</span><span class="pf-den-label">Peer median obligations</span></span>
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
CSS alone.

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

**The exact field eligible for this hover-link - copy this table's wording, never invent a new
percentage or a new formula:**

| Field(s) | Title | Description (real scope substituted in) | Numerator label | Denominator label |
|---|---|---|---|---|
| `PctOfPeerMedian` (a branch row, only where `InPeerSet == true`) | Share of peer median | "{BranchName}'s labour obligation count as a share of the median count across its peer branches (same state, same establishment class)." | Labour obligations (this branch) | Peer median obligations |

Rules:
- **Only `span` tags inside `.hr`, ever** - never `aside`, `div`, `h4`, or `p`.
- **Title** = "Share of peer median - {the real value}%".
- **Description** = ONE plain sentence - the exact wording given in the table above, real branch
  name substituted in.
- **Numerator/denominator show the REAL NUMBER first, then its caption underneath** - never a raw
  field name anywhere the reader can see. The two real numbers you write, divided and multiplied by
  100, must equal the percentage in the title.
- **Never wrap this for a branch with `InPeerSet == false`** - it has no real `PctOfPeerMedian`.
- **Wrap the figure only at its one home appearance**.
- Each `id` (`pf-{unique}` above) must be unique on the page.

**8. Every chart is interactive.** Hover or keyboard focus on any mark shows that mark's real
values - at minimum the branch name and the real fields that place it, including whether it has a
real peer comparison at all. Use an SVG `<title>` child on each mark, or a small CSS tooltip.
Interactivity only reveals values that are already in the data - it never computes a new number.
Build large charts from the real rows with a small inline script using
`createElement`/`setAttribute`/`textContent` (never `innerHTML` with a literal tag), or write the
SVG marks out directly.

**9. Chart craft rules.**
- **Circles stay circles.** Never use `preserveAspectRatio="none"` on an SVG that contains dots.
- **Dots never pile up.** A beeswarm/dot strip spreads dots vertically so they do not overlap.
- **Labels never sit on marks.**
- **Highlight colour.** Branches called out for attention use amber `--warn-fill` (this dimension's
  "review candidate" framing means amber is the DEFAULT tone for a gap, not red), the rest in
  `--c-brand`. Red (`--bad-fill`) is only for a genuinely large, full-confidence concentration the
  composition plan explicitly calls severe.
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
- EVERY section card carries exactly one tone class, matching its `.tag`: `.card--alert` (gaps
  worth reviewing - this dimension's default "finding" tone, NOT `.card--bad`), `.card--calm`
  (neutral facts, peer-coverage structure), `.card--ok` (near-universal obligations, well-covered).
  Use `.card--bad` only for a severity the composition plan explicitly calls out.
- Give the summary number tiles real colour: `.kpi--warn` for gap counts, `.kpi--ok` for
  near-universal/well-covered counts, one or two neutral headline tiles may use `.kpi--brand`.
- Charts use strong, clear colours from the palette - filled marks, not pale outlines.
- Every section card starts with a `.tag`, then its title with the "i" button, then its description
  text, then its visuals.

**11. Never drop real rows.** Every chart, grid and register that claims to cover the population
must be built from EVERY row in `dimension_rows` - the count you show must equal the real row
count. The rows themselves are never typed by you - see section 13.

**12. Words on the page - written for a CEO or CFO.**
- **Show the narrative points as the section's own description - NO "What this means" heading.**
  Put the section's narrative points as its description text directly under the section title. A
  point starting "Worth checking:" becomes that card's one `.note` at the bottom.
- **Plain words in everything you write yourself.**
- Use the reader's words: "branches" (never "members", "rows", "population", "dimension"), "worth
  checking" or "a pattern worth reviewing" (never "violation", "non-compliant", "gap" alone without
  context), "peer branches" (never "peer set"). Never show: assertion, finding, data quality flag,
  marginal, denominator, materiality floor, control totals, reconciled, proxy, flow metric.
- No filler: leverage, robust, holistic, landscape, ecosystem, delve, pivotal, crucial, notably,
  underscores, paradigm, granular, actionable insights, stakeholders, going forward, overall.
- **No messages meant for developers or testers.** Never show: "100% reconciled", "reconciled",
  "verified", "validated", "no fabricated data", "every number traces to the data", assertion or
  finding ids, field or column names (`PctOfPeerMedian`, `GapRank` ...), proc/SQL/table names,
  "data_quality", or any statement about the report's own accuracy.
- Short sentences - aim under 20 words. Numbers written plainly with what they count.
- Counts are whole numbers; percentages one decimal ("84.8%").
- **Vary your wording - never repeat a long compound phrase.** The first time you describe the
  comparison (e.g. "branches grouped by state and establishment class"), say it once, in full. Every
  time after that, use a short plain stand-in - "peer group", "comparison group", "these branches" -
  never the full compound phrase again. A reader should never see the same multi-word technical
  phrase three or more times on one page; that reads as copy-pasted, not written for them.
**13. The data is already on the page - never type rows yourself.** After you return, the system
inserts the real rows and totals into your page, before your first script, as:
`<script type="application/json" id="insights-data">{"dimension":"...","rows":[...],"totals":{...}}</script>`

- Read it at the top of your script:
  `var DATA = JSON.parse(document.getElementById('insights-data').textContent); var ROWS = DATA.rows; var TOTALS = DATA.totals;`
- Build EVERY chart, grid, list and table that shows per-row values from `ROWS`, in script.
- Never write a row array or per-row numbers into your HTML or script.
- Headline figures in text and summary cards are still written directly.
- Handle null fields safely (show "Not comparable" for a branch with no peer set, never 0).

## Technical constraints (unrelated to visual freedom — security/platform requirements)

1. Exactly one HTML document, `<meta charset="utf-8">` first inside `<head>`.
2. All CSS inline. Inline `<script>` is allowed. Zero external references.
3. Never build a `<script>` whose text content contains an HTML-tag-shaped substring. **Every `<`
   inside a script is immediately followed by a space.** Write `i < n`, `v < 0`, `a < b || a ===
   b` (never `a <= b`). Check every `<` in every script before returning.
4. Escape all tenant-entered text. Semantic HTML, real `<table>`/`<th>`/`<caption>`, ordered
   headings, colour paired with a text label (never colour alone).
5. Never the literal word "Tenant" or a numeric tenant id anywhere in the document.

## Real vs. NOT AVAILABLE — CoverageGaps dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a branch row** (`dimension_rows`): `BranchID`, `BranchName`, `StateID`,
`NodeTypeId`, `Class`, `InPeerSet`, `PeerSetSize`, `LabourObligations`, `PeerMedianObligations`,
`PctOfPeerMedian`, `Gaps`, `GapsFullConfidence`, `GapsReducedConfidence`, `UnderConfigured`,
`GapRank`, `Flags` — `PeerMedianObligations`/`PctOfPeerMedian`/`GapRank` are null when `InPeerSet ==
false`.

**Every real tenant-level total** (`dimension_control_totals`): `LeafBranchesInScope`,
`PeerSetBranches`, `PeerGroupsQualifying`, `PeerGroupsTooSmall`, `NearUniversalObligations`, `Gaps`,
`SumOfRowGaps`, `Reconciled`, `GapsFullConfidence`, `GapsReducedConfidence`, `BranchesWithGaps`,
`UnderConfiguredBranches`, `UnknownNodeType`, `ThresholdObligationsExcluded`, `CoverageThreshold`,
`MinPeers`, `Method`.

| Fact | Status |
|---|---|
| Review candidates, never violations | **Every gap carries a real caveat**: no applicability-rules table exists, so a genuine exemption can explain any single gap. State this somewhere visible on the page, not just once buried in a footnote. |
| Action/finding owners | **Real ROLE names only** — Performer, Reviewer, Compliance Officer, Compliance Owner. Never a specific person's name (none is given) or an invented team name. |
| Peer comparability | **REAL but only where populated** — only build a peer-median comparison for a branch where `InPeerSet == true`. A branch not in a peer set is "not comparable", never an invented rate. Peer groups with `Peers < MinPeers` are "too small to compare fairly" (`PeerGroupsTooSmall`), never silently included. |
| Confidence split | **Real, two populations** — `GapsFullConfidence` and `GapsReducedConfidence` are both real and sum toward `Gaps`; never flatten them into one undifferentiated figure without naming the split. |

## The configuration comparison — read this before writing the scope line

**This dimension has no caller-supplied period.** It is a point-in-time configuration comparison
(a branch's current labour-obligation count against its peer group), not a schedule/occurrence
metric — there is no "window" the way most other dimensions have one. State plainly, using the real
`data_quality` entry's own `detail` text, that this view compares current configuration, not
activity over a time range.

## 14. The top of the page - company name, headline, period

1. **Small blue label** - the company name, an EXACT copy of `tenant_name`. 13px, weight 600,
   letter-spacing .08em, brand colour. Only place the company name appears on the page (plus
   `<title>`: "{tenant_name} — {short report name}").
2. **Headline** - the lead finding in plain words (from the composition plan's hero), 44-52px,
   weight 800, dark text, tight line-height (1.05), at most two lines.
3. **One line under it** - one or two plain sentences, 17px, secondary text.
4. **Info box** - light blue box (`--c-light-blue` background, #c9dcf3 border, 12px radius, small
   document icon on the left). Bold brand-colour first line naming the real comparison basis (e.g.
   "Comparing branches by state and establishment type"). Second line: the real `data_quality`
   meaning in plain words - that these are review candidates compared against peer configuration,
   not a point-in-time snapshot of activity.

No other header block. "Generated {date}" goes only in the small print at the very bottom.

## 15. Labels never overlap or get cut off

- **Measure, then place.** Any label that would touch another label, a mark it does not belong to,
  or the chart edge is shortened with "…" or hidden - never left overlapping. Leave 4px between
  labels.
- **Small tiles and thin bars carry no text inside** unless it genuinely fits.
- **Crowded points.** Stagger labels into rows or connect with short leader lines, or label only the
  most important points.
- **Axis labels.** Prefer horizontal labels over rotated ones; if rotated, reserve enough space.
- After drawing, the script runs one last collision check and hides any label that still overlaps.

## Self-check before returning

- Every percentage figure in your own prose that matches section 7b's eligible field is wrapped as
  a `.pf` dotted-underline hover-link, real numbers that reconcile, no raw field name in the panel,
  never wrapped for a branch with `InPeerSet == false`, every `pf-` id unique.
- No gap anywhere on the page is stated as a confirmed violation - always "review candidate"/"worth
  checking" framing, with the real exemption caveat stated somewhere visible.
- Confidence split (`GapsFullConfidence`/`GapsReducedConfidence`) is named wherever `Gaps` is
  reported as a headline figure, never flattened into one undifferentiated number.
- Section 15: no two labels overlap and none is cut off at any chart edge.
- Section 14: the page starts with the small blue label = exact `tenant_name`, then the big
  headline, one line, then the blue info box.
- No developer/tester messages anywhere on the page.
- Section 12: no "What this means" heading anywhere; narrative points are the description text;
  at most one `.note` per card; plain words.
- Section 13: every per-row chart/table/grid is built from `ROWS` read out of `#insights-data`; no
  row data typed anywhere in the page.
- Section 11: the number of rows embedded in the page equals the number of rows in
  `dimension_rows`.
- Section 10: every section card has one tone class matching its `.tag`; `.card--bad` used only for
  an explicitly-called-out severity, not the default gap tone.
- EVERY chart has an "i" button with a "How to read this chart" panel; every visible component has
  its own row; swatch colours match the chart's real colours; each panel id is unique.
- Every chart shows real values on hover/focus, including whether a branch is comparable at all.
- Section 9: dots are round, beeswarm dots do not overlap, labels do not sit on marks, red reserved
  for explicitly-called-out severity only.
- Every `<` inside every `<script>` is immediately followed by a space.
- Any complete-register table sits inside `.table-scroll`.
- Every real branch row is represented somewhere, none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No invented owner name, no population-count conflation.
- No branch with `InPeerSet == false` shown with an invented peer comparison.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles.
- Single HTML document, no external references, no runtime network calls, no tenant name/id leak.

Return the HTML document only.

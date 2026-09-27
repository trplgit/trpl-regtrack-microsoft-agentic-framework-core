# Report Generation — Risk, freehand (v4, 2026-09-27)

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

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging:
composition (the plan you are given, already approved) decided real structure/hero/emphasis for
THIS tenant's own data — your job is to actually build what it describes. Real markup, real CSS,
real layout, matching what `composition_plan.blocks[].emphasis` asks for. You have genuine freedom
over illustration choice, layout, and visual treatment — this is deliberately not a fixed document
shape.

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
severity semantics — good/caution/critical, never re-purposed for anything else. **This dimension's
own headline fact can be `--ok`-coloured**: when `A-CRIT`'s direction is `better`, that is real good
news (Critical tier outperforming the tenant average) and should read as such, not be forced into a
warning colour because "Critical" sounds severe. Add as many new tokens as you like for anything
these don't cover; you may not redefine these.

**3. Button/pill/chip sizing.** Any interactive or badge-like control (a tab, a tonetag, a chip,
a toggle) uses this real spacing/radius scale, not ad-hoc values:
```css
:root{ --r-sm:3.5px;--r-md:5.5px;--r-lg:9px;--r-xl:11px; --gap-sm:8px;--gap-md:12px;--gap-lg:16px; }
```

**4. Outer layout.** The page is a single centered column: `max-width:1100px;margin:...auto;padding:0 18px 26px`
(narrow it responsibly below `52rem`). Inside that column, arrange sections however the composition
plan calls for — grid, stacked cards, single-column narrative, whatever fits the real data best.
With only 4 real rows, a full data-table register is rarely the right shape here — a card or tile
per risk level, sized/ordered by real materiality, usually serves this dimension better than a
scrolling table built for a much larger population.

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

**6. Long tables — contained, never page-growing.** If you do build a table (e.g. a 4-row risk
summary table is fine at full length — this rule matters only if you add a longer per-branch or
per-instance breakdown this dimension doesn't otherwise have), any table listing more than a
handful of real members goes inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```

**7. The "i" button and the "How to read this chart" panel (NEW in v3, required on EVERY chart).**
Every chart, graph, heatmap, distribution, scatter, dot strip or other visual gets a small round
"i" button right after its title. Hovering the "i" opens a panel on the right side of the screen
titled **"How to read this chart"**; moving the mouse away closes it (the panel stays open while
the mouse is over the "i" OR over the panel itself). Clicking the "i" pins the panel open until the
reader clicks the close X (or presses Escape). Tables do not need one; charts always do.

Every number inside the examples in sections 7 and 8 (42 risk levels, median 25.5, 18.2%, etc.) is
ILLUSTRATIVE ONLY - never copy one onto the page; use this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title - all 42 risk levels in this period</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i" title="How to read this chart">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences: what it shows and what question it answers)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each bar = one risk level</strong><span>The length of each bar shows how much of that risk level's work is overdue. Longer bars mean more late work.</span></div>
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

Add this one small script once, at the end of `<body>`, so Escape closes any pinned panel:
```js
document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
```

Each chart's `id` (`hr-load` above) must be unique on the page.

**What goes in the panel.** The composition plan gives you each section's content after the marker
`HOW TO READ:` inside its `emphasis` - use it as your source, one `.hr-row` per component, and fill
any component it missed so that EVERY visible part of the chart is explained. Required coverage:
- what one mark is (bar / bubble / tile / cell) - "Each bar = one risk level";
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
`EngagementBand`...) - say what it means instead ("number of completed events", "obligations
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
mark's real values - at minimum the risk level label and the real fields that place it. Use an SVG
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
- **Highlight colour.** Named examples called out for attention (worst risk levels, named risk levels) use
  amber `--warn-fill`, with the rest of the marks in `--c-brand` - exactly like the reference
  "Named high-load examples" chart. Red (`--bad-fill`) is only for genuine severity (overdue,
  critical), never for "this is a named example".
- **Reader language, not internal language.** Anywhere a reader sees text (titles, legends,
  captions, tooltips, notes, panels), never use internal words: "assertion", "asserted",
  "supplied", "dimension", "row", "marginal", "null", "confounded", "reconciled control totals".
  Say it plainly: "risk levels highlighted as needing attention", "no reading", "shown separately and
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
- Use the reader's words: "obligations" or "compliance tasks" (never "instances"), "in this report"
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

## Real vs. NOT AVAILABLE — Risk dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a risk row** (`dimension_rows`, always exactly 4 rows — Critical/High/
Medium/Low, present even at zero obligations): `RiskType` (raw enum — never state this number or
imply it orders severity), `RiskLabel` (the real name — always use this), `Instances`, `Overdue`,
`OverduePct`, `Ownerless`, `ImprisonmentInstances`, `ImprisonmentOverdue`, `BranchesCovered`,
`VsTenantPP`, `Flags`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `SumOfRows`,
`Reconciled`, `OverdueInstances`, `TenantOverduePct`, `RiskLevelsReported`,
`RiskLevelsWithObligations`, `CriticalRiskType`, `ImprisonmentInstances`,
`ImprisonmentOnCriticalPct`.

| Fact | Status |
|---|---|
| "The Critical tier runs {better/worse} than the tenant average" | **REAL, with a computed direction** — `A-CRIT`'s `Direction` field. Never assume "Critical" means "worst-performing" — state whichever way the real number points. |
| "Critical items and imprisonment exposure are basically the same thing here" | **REAL** — `A-IMP-OVERLAP`'s `ImprisonmentOnCriticalPct`. State it ONCE as one fact; never present Critical-tier volume and imprisonment exposure as two separate findings when this assertion is present — see its `narrative_guard`. |
| "The ownership gap is worst in the middle/lower tiers, not Critical" | **REAL, only when `A-OWNGAP-*` assertions are present** — name the specific tier(s) (individual mode) or state the aggregate count (aggregate mode), per whichever the composition plan was given. |
| "Risk level 3 is the most severe" or any severity claim from the raw `RiskType` number | **NOT AVAILABLE as a numeric ordering** — `RiskType` values are 3=Critical, 0=High, 1=Medium, 2=Low, not severity-ordered. Use `RiskLabel` only, never the raw integer. |
| A root cause for why a tier is better/worse-managed | **NOT AVAILABLE** — state the pattern, never infer why. |
| An industry/cross-tenant risk benchmark | **NOT AVAILABLE** — every comparative here is this tenant's own `TenantOverduePct`, nothing external. |

## The `window` data_quality entry — read this before writing the scope line, ADDED 2026-09-25

**[FOUND LIVE 2026-09-25, FIX]** An earlier version of this file (and the equivalent file for
Act/Event) had no instruction for this key at all, since it did not exist yet. Real output then
wrote generic filler like "the supplied window data-quality flag applies; no further definition was
provided" — technically satisfied "every data_quality_to_surface entry is visibly stated",
completely failed to say anything real. Every other `data_quality` entry has a real `detail` string
already written for you — **use it**, do not paraphrase it into something vaguer. This one's
`detail` names the actual concrete date range this run was scoped to.

- ❌ "The supplied window data-quality flag applies; no further definition was provided."
- ✅ "This view covers obligations with a scheduled occurrence between 26 Aug 2026 and 25 Sep 2026
  only — a risk tier's real counts here reflect only that window, not the tenant's all-time risk
  profile." (the real dates come from the `window` entry's own `detail` text, reformatted for
  readability, never invented)

State this near the top of the page (it changes what every other number here means, including
`ScopedInstances` and `TenantOverduePct`) — a caller comparing two runs needs to know they may be
looking at two different windows, not two different tenants.

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

## Self-check before returning

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
- Every one of the 4 real risk levels is represented somewhere — none silently dropped, including
  a level with zero obligations.
- `RiskLabel` is used throughout; the raw `RiskType` integer never appears as if it meant severity.
- If `A-IMP-OVERLAP` is present, Critical-tier volume and imprisonment exposure are stated as ONE
  fact, not two separate findings.
- The `A-CRIT` direction is stated exactly as computed — a `better` direction reads as real good
  news, not forced into a warning tone.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT, not
  buried, dropped, or replaced with generic filler - the `window` entry especially, see above.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

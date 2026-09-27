# Report Generation — Nature of Compliance, freehand (v4, 2026-09-27)

**[v4, 2026-09-27]** Copied from v3 (untouched). Changes: section 10 - Atmosphere (hero wash,
one decorative corner shape, tinted plot areas, meaning-tinted summary cards). Found live: a deployed
v3 render came out flat white while earlier renders had these touches; now they are required.
Section 11 - never drop real rows. Section 12 - plain CEO/CFO words, every narrative point shown.
Section 13 - rows are injected by code as #insights-data; the model reads them, never types them.

**[v3, 2026-09-27]** Copied from the previous version (which is untouched). New in v3: every chart
gets an "i" button that opens a "How to read this chart" panel (section 7), every chart is
interactive (section 8), chart craft rules (section 9), and a stricter script rule (technical
constraint 3). Sections 7-9 are shared word-for-word with every other v3 dimension render prompt.

**[ADDED 2026-09-22]** Same freehand contract as Act/Departments/Licence/Location/BacklogAging/Risk:
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

**6. Long tables — contained, never page-growing.** Any table listing every real member of the
dimension (a "complete register," "all configured natures," or similar — not a capped top-5 list)
goes inside a fixed-height container with its OWN internal scroll:
```css
.table-scroll{max-height:420px;overflow-y:auto;overflow-x:auto;border:1px solid var(--c-border);border-radius:var(--r-lg)}
.table-scroll table{width:100%;border-collapse:separate;border-spacing:0}
.table-scroll thead th{position:sticky;top:0;background:var(--c-mist);z-index:1}
```
`max-height` can be any value that keeps the container to roughly one screenful (350-500px is a
reasonable range) - the TABLE scrolls, the PAGE around it does not grow to fit every row. Sticky
header (`position:sticky;top:0`) keeps column labels visible while scrolling.

**7. The uncategorised gap needs a visually distinct treatment.** This dimension is structurally
half-blind (`UncategorisedPct` typically ~50%) — this fact deserves a clearly separate visual
treatment from the real, named-nature ranking (e.g. its own callout card, a visually distinct
segment in a chart, or a dedicated banner), never mixed into a ranked list of real natures as if it
were one more entry competing on the same axis.

**7. The "i" button and the "How to read this chart" panel (NEW in v3, required on EVERY chart).**
Every chart, graph, heatmap, distribution, scatter, dot strip or other visual gets a small round
"i" button right after its title. Hovering the "i" opens a panel on the right side of the screen
titled **"How to read this chart"**; moving the mouse away closes it (the panel stays open while
the mouse is over the "i" OR over the panel itself). Clicking the "i" pins the panel open until the
reader clicks the close X (or presses Escape). Tables do not need one; charts always do.

Every number inside the examples in sections 7 and 8 (42 compliance types, median 25.5, 18.2%, etc.) is
ILLUSTRATIVE ONLY - never copy one onto the page; use this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title - all 42 compliance types in this period</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i" title="How to read this chart">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences: what it shows and what question it answers)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each bar = one compliance type</strong><span>The length of each bar shows how much of that compliance type's work is overdue. Longer bars mean more late work.</span></div>
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
- what one mark is (bar / bubble / tile / cell) - "Each bar = one compliance type";
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
mark's real values - at minimum the compliance type name and the real fields that place it. Use an SVG
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
- **Highlight colour.** Named examples called out for attention (worst compliance types, named compliance types) use
  amber `--warn-fill`, with the rest of the marks in `--c-brand` - exactly like the reference
  "Named high-load examples" chart. Red (`--bad-fill`) is only for genuine severity (overdue,
  critical), never for "this is a named example".
- **Reader language, not internal language.** Anywhere a reader sees text (titles, legends,
  captions, tooltips, notes, panels), never use internal words: "assertion", "asserted",
  "supplied", "dimension", "row", "marginal", "null", "confounded", "reconciled control totals".
  Say it plainly: "compliance types highlighted as needing attention", "no reading", "shown separately and
  never combined", "results can differ because of the mix of roles in each group".


**10. Atmosphere - soft colour and depth (NEW in v4, required on every page).** A flat white page
reads as unfinished. Real feedback: renders WITH these touches were preferred, and a render without
them looked "blunt". Use exactly these, every time (they are part of the shared theme, not optional
decoration):

```css
/* The lead (hero) section: a soft wash plus one decorative corner shape. Pick the wash by the
   hero's own tone: .hero--alert when the lead finding is bad/late/at-risk, .hero--calm otherwise. */
.hero{position:relative;overflow:hidden;border-radius:16px;border:1px solid var(--c-border);padding:28px 30px}
.hero--alert{background:linear-gradient(120deg,#fdf1e2 0%,#fffaf4 45%,#ffffff 100%);border-color:#f1d9b8}
.hero--calm{background:linear-gradient(135deg,#ffffff 0%,#f6f9fe 55%,#eaf2fc 100%);border-color:#dbe6f5}
.hero::before{content:"";position:absolute;right:-70px;top:-70px;width:240px;height:240px;border-radius:50%;
  background:radial-gradient(circle at 30% 30%,rgba(18,90,171,.10),rgba(18,90,171,.04) 60%,transparent 70%);pointer-events:none}
.hero--alert::before{background:radial-gradient(circle at 30% 30%,rgba(224,161,6,.16),rgba(224,161,6,.05) 60%,transparent 70%)}
.hero > *{position:relative}

/* Every other section card: white with the faintest cool fade, soft shadow. */
.card{background:linear-gradient(180deg,#ffffff 0%,#fbfcfe 100%);border:1px solid var(--c-border);border-radius:14px;
  box-shadow:0 1px 2px rgba(16,24,40,.04),0 8px 24px rgba(16,24,40,.04)}

/* Chart plot areas: a soft tinted field, never bare white. */
.plot{background:linear-gradient(180deg,#f9fbfe 0%,#f4f7fc 100%);border:1px solid #e6ecf5;border-radius:12px}
/* A chart whose horizontal axis has a meaning on each side (early vs late, better vs worse than
   average, below vs above a line) gets a directional wash matching its own colours: */
.plot--diverging{background:linear-gradient(90deg,#e8f2fd 0%,#f7fafe 38%,#ffffff 50%,#fef8ef 62%,#fcf0de 100%)}

/* Summary number cards take the tint of their meaning (real severity only): */
.kpi{border-radius:12px;border:1px solid var(--c-border);background:#f8fafc}
.kpi--bad{background:var(--bad-bg);border-color:var(--bad-stroke)}
.kpi--warn{background:var(--warn-bg);border-color:var(--warn-stroke)}
.kpi--ok{background:var(--ok-bg);border-color:var(--ok-stroke)}
.kpi--brand{background:var(--c-light-blue);border-color:#c9dcf3}
```

Rules:
- Exactly ONE `.hero` per page (the composition plan's hero section), with ONE decorative
  `::before` shape. No other decorative shapes anywhere - restraint is what keeps it elegant.
- Every chart's drawing area sits in a `.plot` (or `.plot--diverging` when its horizontal axis is
  two-sided). Script-built charts get this class on their container.
- A `.kpi--bad/--warn/--ok` tint only when the number genuinely carries that meaning (e.g. overdue
  = bad, unassigned = warn, reconciled = ok); plain counts stay neutral `.kpi`, at most one neutral
  card per row may use `.kpi--brand` for emphasis.
- These colours are backgrounds only - text contrast stays as the palette defines it; never put
  text directly on the corner shape.
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
- **Show every narrative point, in full.** Each section shows its narrative points as a short,
  readable list under a small heading such as "What this means" - one point per line, a little
  space between them, never merged into one paragraph, never shortened, never dropped. A line
  starting "Worth checking:" gets a subtle callout style (light `--c-light-blue` background, brand
  left border) so it stands out as the thing to act on.
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

## Real vs. NOT AVAILABLE — Nature dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a nature row** (`dimension_rows`): `NatureId`, `NatureName`, `IsRetired`,
`Instances`, `Overdue`, `OverduePct`, `Ownerless`, `ImprisonmentInstances`, `ImprisonmentOverdue`,
`CriticalInstances`, `BranchesCovered`, `PenaltyBearingInstances`, `FinancialPenaltyInstances`,
`ClosureRiskInstances`, `ImprisonmentSharePct`, `OverdueRank`, `Flags`. A real "Others" row is
always present — see below.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `CategorisedInstances`,
`Reconciled`, `OverdueInstances`, `TenantOverduePct`, `TenantImprisonmentSharePct`,
`NaturesReported`, `NaturesWithObligations`, `RetiredNaturesStillInUse`, `OthersBucketInstances`,
`UntaggedInstances`, `UncategorisedInstances`, `UncategorisedPct`.

| Fact | Status |
|---|---|
| "{N} of {M} instances are properly categorised by nature" | **REAL** — but must be paired with `UncategorisedPct`/`UncategorisedInstances` in the SAME breath, never stated alone as if categorisation were complete. |
| "This report covers the full estate by nature" | **NOT AVAILABLE as an unqualified claim** — roughly half the estate is typically uncategorised (`UncategorisedPct`). State the real coverage fraction instead. |
| "{Nature} has the highest overdue rate" | **REAL, only for a real named nature** — `A-WORST-NATURE`. The "Others" row is NEVER eligible for this claim even if its own `OverduePct` is numerically highest — it is excluded from the ranking by construction; never override that by computing your own rank from the raw rows. |
| "{Nature} carries personal liability on {X}% of its obligations" | **REAL** — `ImprisonmentSharePct` (a real per-nature rate) or `A-IMPLIN-*`. Do not present this as a SEPARATE exposure from Critical risk — this run has no Risk-dimension data to check that overlap; state the real percentage without an independence claim either way. |
| A root cause for why a nature runs worse, or why the estate is uncategorised | **NOT AVAILABLE** — state the pattern, never infer why. |
| "Untagged" as a nature with its own name/identity | **NOT AVAILABLE** — untagged instances are a real control-total figure (`UntaggedInstances`), not a member with a name. Never invent a row for it. |

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
  only — a nature's real counts here reflect only that window, not the tenant's all-time nature
  breakdown." (the real dates come from the `window` entry's own `detail` text, reformatted for
  readability, never invented)

State this near the top of the page (it changes what every other number here means, including
`ScopedInstances` and the uncategorised gap) — a caller comparing two runs needs to know they may be
looking at two different windows, not two different tenants.

## Self-check before returning

- (v4) Section 12: every narrative point shown in full as its own line, "Worth checking" lines
  styled as callouts, no internal words or filler anywhere a reader sees.- (v4) Section 13: every per-row chart/table/grid is built from `ROWS` read out of
  `#insights-data`; no row data typed anywhere in the page; shown counts come from `ROWS`.- (v4) Section 11: the number of rows embedded in the page equals the number of rows in
  `dimension_rows` - count them before returning.- (v4) Section 10: exactly one `.hero` with its wash and one corner shape; every section in a
  `.card`; every chart's drawing area in a `.plot` (two-sided axes use `.plot--diverging`);
  summary cards tinted only by real meaning. No bare flat-white page.- (v3) EVERY chart has an "i" button with a "How to read this chart" panel built with the exact
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
- The uncategorised gap (`UncategorisedPct`) is stated plainly, prominently, and visually distinct
  from the ranked real-nature content — never buried, never implied away.
- The "Others" row (if shown) is never presented as a ranked or "worst" nature.
- No claim of full/complete nature coverage anywhere.
- If personal-liability exposure is discussed, it is never framed as independent from Critical
  risk exposure.
- Every real nature row with meaningful volume is represented somewhere — none silently dropped.
- Any complete-register table sits inside `.table-scroll` if it lists more than a handful of rows.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT, not
  buried, dropped, or replaced with generic filler - the `window` entry especially, see above.
- Font is Poppins, the palette/sizing tokens above are declared and used for their stated roles,
  any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

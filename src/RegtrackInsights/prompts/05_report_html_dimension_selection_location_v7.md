# Report Generation — Location, freehand (v4, 2026-09-27) (v5, 2026-09-29: no ownership findings) (v6, 2026-09-29: no vague umbrella labels, say each fact once) (v7, 2026-09-30: percentage hover-link, section 7b - proven mechanism ported from the Licence dimension's own version)

**[2026-09-29, from a real user review of another dimension's report] Two more rules.**
1. **Never use a vague umbrella label without naming what is inside it, every time you use it.** A
   grouping word ("in progress", "resolved", "healthy", "at risk", "on track", "still open") is fine
   ONLY when the real items or counts behind it are named in the SAME sentence, every single time
   that label appears on the page - never a bare label the reader has to guess at, and never
   explained once and then reused bare later.
2. **Say each fact once.** Decide the one section where a number or finding belongs, then do not
   restate it as a near-duplicate sentence in another section, card or chart caption. A number may
   appear again only where it is genuinely doing new work (e.g. once as a headline figure, once
   inside a chart's own hover/focus detail) - never as a second explanatory sentence repeating what
   was already said. Nothing in this file fixes a specific set of section or tile titles, their
   count or their order - keep choosing freely, from `composition_plan`'s own real structure.


> **[2026-09-29] Ownership is not a finding (RegTrack parity).** Insights now counts exactly what
> RegTrack's own reports count, and RegTrack only lists compliances that have an active performer.
> So every compliance in this data has an owner: the ownership fields (`Ownerless`, `OwnerlessPct`,
> `NoInstanceOwner`, `NoInstanceOwnerPct`, `NoOwnerAnywhere`, `OwnerClass`, and their Statutory/
> Internal/Tenant variants), the `high_ownerless` flag, any ownership assertion and the
> `ownership_has_two_mechanisms` note are always 0 or absent. Never build a section, chart, card,
> KPI, sentence, recommendation or action about ownership, missing owners, unassigned performers or
> "nobody is accountable". Ignore those fields entirely.

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

Every number inside the examples in sections 7 and 8 (42 locations, median 25.5, 18.2%, etc.) is
ILLUSTRATIVE ONLY - never copy one onto the page; use this tenant's real values.

Build it with exactly this mechanism - CSS-only, so it works even if scripts are stripped. The
checkbox MUST sit inside `.hr` (the element the `:has()` selector targets), never before it:

```html
<div class="chart-head">
  <h3>Your real chart title - all 42 locations in this period</h3>
  <div class="hr">
    <input type="checkbox" class="hr-toggle" id="hr-load" aria-label="How to read this chart">
    <label for="hr-load" class="hr-i">i</label>
    <aside class="hr-panel" role="dialog" aria-label="How to read this chart">
      <label for="hr-load" class="hr-close" aria-label="Close">&times;</label>
      <h4 class="hr-title">How to read this chart</h4>
      <p class="hr-intro">This chart shows ... (one or two plain sentences: what it shows and what question it answers)</p>
      <div class="hr-row">
        <div class="hr-ico"><!-- small inline SVG icon or colour swatch --></div>
        <div class="hr-txt"><strong>Each bar = one location</strong><span>The length of each bar shows how much of that location's work is overdue. Longer bars mean more late work.</span></div>
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

**7b. Percentage figures become a hover-link to their formula (NEW, 2026-09-30).** Wherever a
percentage figure appears in your OWN written prose - a headline, a section's description text, a
card's `.note` - wrap just that figure (e.g. `(12.3%)`, `44.9%`) as a hover-link to a small popup
showing how it is worked out. Reference: a real product screenshot showing exactly this pattern -
an underlined percentage in a sentence, hover reveals a card with a title, one plain sentence, and
a "HOW IT IS CALCULATED" fraction box with the real numbers on it.

**Scope - percentages only, never counts.** This dimension has five real percentage shapes: a branch's own overdue rate, the tenant-wide overdue rate, the tenant-wide on-time rate, the leaf-branch share of all reported branches, and the single-person-dependency share of branches with compliances. Never wrap a plain count ("12 branches, 340 compliances")
- only a number that is itself a percentage figure, and only one of the fields below. If you never
write one of these percentages in your prose this run, this section produces nothing - never invent
one to have something to wrap.

Reuse the SAME `.hr` mechanism section 7 already requires (same checkbox/label/panel, same
hover-to-preview / click-to-pin / Escape-to-close) - just styled and triggered differently: the
trigger is the percentage text itself (dotted underline, not a round "i" badge), and the panel body
is a title, one plain sentence, and a fraction formula box instead of `.hr-row`s.

**[FIX - proven live on the Licence dimension's own version of this section, 2026-09-30] Every tag
inside `.hr` here must be `span`, never `aside`/`div`/`h4`/`p`** - this block sits INSIDE a `<p>` of
running prose (unlike section 7's chart version, which sits inside a `<div>` chart-head, never
inside a `<p>`). A browser auto-closes a `<p>` the instant it meets a BLOCK-level start tag inside
it - the panel silently ends up as a sibling of your paragraph instead of nested inside `.hr`, so it
renders sanely as flat text but the popup can never open.

```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pf-{unique}" aria-label="How this percentage is worked out">
  <label for="pf-{unique}" class="hr-i pf">(12.3%)</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
    <label for="pf-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">Overdue percentage - 12.3%</span>
    <span class="hr-intro pf-intro">The percentage of Mumbai Warehouse's compliances counted this period that are overdue.</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-frac">
        <span class="pf-frac-stack">
          <span class="pf-num"><span class="pf-num-value">8</span><span class="pf-num-label">Overdue compliances (this branch)</span></span>
          <span class="pf-den"><span class="pf-den-value">65</span><span class="pf-den-label">Compliances counted (this branch)</span></span>
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

`.pf-panel` MUST stay `position:fixed` (inherited from the shared `.hr-panel` rule in section 7 -
do not override it to `absolute` or `relative`), with its `top`/`left` set by the small script
below, not by CSS alone - every section card is `overflow:hidden` (section 10, needed for its own
corner-circle decoration), and a `position:absolute` popup nested inside one gets silently clipped
to the card's edges once it grows past the card's remaining space. The `right:auto;bottom:auto`
overrides above matter too: leaving the shared rule's own `bottom:16px` in place while the script
sets `top` to a large value stretches the panel's HEIGHT all the way down to the viewport bottom,
leaving a large blank area under the real content - copy both overrides exactly.

Add this ONE small script once, right next to the Escape-close script already required in section 7
above (both go at the end of `<body>`, in the same `<script>` or a second one - either is fine):
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

**The exact fields eligible for this hover-link - copy this table's wording, never invent a new
percentage or a new formula:**

| Field(s) | Title | Description (real scope substituted in) | Numerator label | Denominator label |
|---|---|---|---|---|
| `OverduePct` (a branch row) | Overdue percentage | "The percentage of {BranchName}'s compliances counted this period that are overdue." | Overdue compliances (this branch) | Compliances counted (this branch) |
| `TenantOverduePct` | Overdue percentage | "The percentage of all compliances counted this period that are overdue." | Overdue compliances (across all branches) | Compliances counted (across all branches) |
| `TenantOnTimePct` | On-time percentage | "The percentage of completed events across all branches that finished on time." | Completed on time (across all branches) | Completed events (across all branches) |
| `GhostEntities` / `BranchesReported` (count the `no_obligations_configured`-flagged leaf rows yourself if you state this share - both numbers are real, already in `dimension_rows`/`dimension_control_totals`) | Leaf-branch share | "The percentage of all reported branches that are leaf branches with no compliances in this period." | Leaf branches with no compliances | Branches reported |
| Count of rows flagged `single_point_of_failure` with `Instances > 0`, over count of rows with `Instances > 0` (both counted from `dimension_rows` - never invented) | Single-person dependency share | "The percentage of branches with compliances in this period that depend on exactly one performer or exactly one reviewer." | Branches depending on one performer or reviewer | Branches with compliances in this period |

**[ADDED 2026-10-01] "Compliances counted (across all branches)" means `SumOfRows`, never `ScopedInstances`.**
`ScopedInstances` is the distinct-OBLIGATION count; `TenantOverduePct` is computed against `SumOfRows`,
the scoped OCCURRENCE total (one obligation recurring 3 times in the period counts as 3). The two
numbers differ on a real tenant - if a numerator/denominator pair you show is meant to multiply back
to a shown percentage, the "total compliances" side of that pair is `SumOfRows`.

Rules:
- **Only `span` tags inside `.hr`, ever** - never `aside`, `div`, `h4`, or `p`.
- **Title** = "{Field's own name above} - {the real value}%" (e.g. "Overdue percentage - 12.3%").
  Never a percentage not in the table above.
- **Description** = ONE plain sentence naming the real scope (this row's own name, or "across all
  branches" for a tenant-wide figure) - the exact wording given in the table above, with the
  real name substituted in.
- **Numerator/denominator show the REAL NUMBER first, then its caption underneath** - the exact
  labels given in the table above, never a raw field name (OverduePct, TenantOverduePct, TenantOnTimePct, ScopedInstances, OverdueInstances...) anywhere the reader
  can see. The two real numbers you write, divided and multiplied by 100, must equal the percentage
  in the title - if they do not, you have the wrong scope's numbers, fix it before returning.
- **Wrap the figure only at its one home appearance** (section 2/3's own "say each fact once" rule) -
  never re-wrap the same value if it legitimately repeats inside a chart's own hover/focus detail.
- Each `id` (`pf-{unique}` above) must be unique on the page, distinct from every chart's own
  `hr-load`-style id and from every other `pf-` id.


**7c. Percentage-POINT DIFFERENCES also become a hover-link (NEW, 2026-09-30 - closes a real gap: section 7b above covers plain percentage shares only, so every comparative "X points above/below ..." sentence this dimension's own style rules ask for was shipping as plain, unlinked text - found live via a headless audit of real rendered reports).** Wherever your prose states a point difference between two of this dimension's real percentages, wrap just the point figure (e.g. `46.8 points`) the same way section 7b wraps a plain percentage - same `.hr` mechanism, same hover-to-preview/click-to-pin/Escape-to-close - but the panel shows a SUBTRACTION, not a fraction.

**Scope - only the point-difference shapes below, and only when you actually write that comparison in prose. Never invent a comparison not in this table:**

| Comparison | Title (value substituted; "above" when Value A > Value B, "below" when Value A < Value B) | Description | Value A label | Value B label |
|---|---|---|---|---|
| branch `OverduePct` vs `TenantOverduePct` | "{value} points above/below the company-wide rate" | "How {BranchName}'s overdue percentage compares with the company-wide overdue percentage this period." | {BranchName}'s own overdue percentage | Company-wide overdue percentage |
| branch `OverduePct` vs its own `PeerStateOverduePct` (the reconciling field is `VsPeerStateNormPP`) | "{value} points above/below the {StateName} median" | "How {BranchName}'s overdue percentage compares with the median overdue percentage for company branches in {StateName}." | {BranchName}'s own overdue percentage | {StateName} median overdue percentage |

Reuse `span`-only markup (same reason as 7b above - this sits inside running prose too, never `aside`/`div`/`h4`/`p`):
```html
<span class="hr">
  <input type="checkbox" class="hr-toggle" id="pd-{unique}" aria-label="How this difference is worked out">
  <label for="pd-{unique}" class="hr-i pf">46.8 points</label>
  <span class="hr-panel pf-panel" role="dialog" aria-label="How this difference is worked out">
    <label for="pd-{unique}" class="hr-close" aria-label="Close">&times;</label>
    <span class="hr-title pf-title">46.8 points above the company-wide rate</span>
    <span class="hr-intro pf-intro">How Baleshwar's overdue percentage compares with the company-wide overdue percentage this period.</span>
    <span class="pf-formula">
      <span class="pf-formula-label">HOW IT IS CALCULATED</span>
      <span class="pf-diff">
        <span class="pf-diff-row"><span class="pf-diff-value">100.0%</span><span class="pf-diff-label">Baleshwar's own overdue percentage</span></span>
        <span class="pf-diff-op">&minus;</span>
        <span class="pf-diff-row"><span class="pf-diff-value">53.2%</span><span class="pf-diff-label">Company-wide overdue percentage</span></span>
        <span class="pf-diff-op">=</span>
        <span class="pf-diff-row pf-diff-result"><span class="pf-diff-value">46.8 points</span><span class="pf-diff-label">Difference</span></span>
      </span>
    </span>
  </span>
</span>
```
```css
.pf-diff{display:flex;flex-direction:column;align-items:center;gap:2px}
.pf-diff-row{display:flex;align-items:baseline;gap:8px;justify-content:center}
.pf-diff-value{font-size:15px;font-weight:700;color:#1f2937;white-space:nowrap}
.pf-diff-label{font-size:11px;color:var(--c-text-2);white-space:nowrap}
.pf-diff-op{font-size:14px;font-weight:600;color:#1f2937}
.pf-diff-result .pf-diff-value{color:var(--c-brand)}
```
Everything else is identical to section 7b above and already covers `.pf` generically - do not redeclare `.pf-panel`'s `position:fixed`, the `pfPlace` positioning script, or the Escape/close-button script; they already fire for every `.pf` element on the page, this one included.

Rules:
- **Title** = "{value} points {above/below} {the comparator's plain-English name from the table}" - `above` when Value A > Value B, `below` when Value A < Value B - and the point value itself is always written positive (never a negative number of points).
- **Value A minus Value B must equal the point figure** (to the same rounding the surrounding prose already uses) - if it does not, you have the wrong scope's numbers, fix it before returning.
- **Description** = ONE plain sentence naming both real scopes being compared, matching the table's wording with the real names substituted in.
- Each `id` (`pd-{unique}` above) must be unique on the page, distinct from every other id on the page (charts' `hr-load`, section 7b's own `pf-` ids, and every other `pd-`).

**What goes in the panel.** The composition plan gives you each section's content after the marker
`HOW TO READ:` inside its `emphasis` - use it as your source, one `.hr-row` per component, and fill
any component it missed so that EVERY visible part of the chart is explained. Required coverage:
- what one mark is (bar / bubble / tile / cell) - "Each bar = one location";
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
mark's real values - at minimum the location name and the real fields that place it. Use an SVG
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
- **Highlight colour.** Named examples called out for attention (worst locations, named locations) use
  amber `--warn-fill`, with the rest of the marks in `--c-brand` - exactly like the reference
  "Named high-load examples" chart. Red (`--bad-fill`) is only for genuine severity (overdue,
  critical), never for "this is a named example".
- **Reader language, not internal language.** Anywhere a reader sees text (titles, legends,
  captions, tooltips, notes, panels), never use internal words: "assertion", "asserted",
  "supplied", "dimension", "row", "marginal", "null", "confounded", "reconciled control totals".
  Say it plainly: "locations highlighted as needing attention", "no reading", "shown separately and
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

## Real vs. NOT AVAILABLE — Location dimension

The data given to you (`assertions`, `dimension_rows`, `dimension_control_totals`) is real,
already-reconciled SQL output. Everything you state must trace to it.

**Every real field on a branch row** (`dimension_rows`): `BranchID`, `BranchName`, `NodeType`,
`RootKind`, `ApexName`, `Instances`, `Overdue`, `Ownerless`, `ImprisonmentInstances`,
`CriticalInstances`, `DistinctPerformers`, `DistinctReviewers`, `ClosureEventsLifetime`,
`ActiveChildren`, `OverduePct`, `OwnerlessPct`, `ClosureRatio`, `OverdueRank`, `StateID`,
`StateName`, `PeerStateOverduePct`, `VsPeerStateNormPP`, `Flags` — includes rows with
`Instances == 0`.

**Every real tenant-level total** (`dimension_control_totals`): `ScopedInstances`, `SumOfRows`,
`OverdueInstances`, `TenantOverduePct`, `BranchesReported`, `ActiveBranchesInTenant`,
`BranchesWithNoObligations`, `GhostEntities`, `TenantMedianClosureRatio`, `TenantIsOnboarding`,
`TenantCompletedEvents`, `TenantOnTimeEvents`, `TenantOnTimePct`.

**`Flags`** is the real detector-tag field — only 5 real values ever appear: `onboarding_artifact`,
`ghost_entity`, `single_point_of_failure`, `high_ownerless`, `peer_coverage_gap`.
"Single-dependency" = `Flags` containing `single_point_of_failure`, or directly
`DistinctPerformers == 1 || DistinctReviewers == 1` on a row with `Instances > 0`.

| Fact | Status |
|---|---|
| State peer-rate comparisons | **REAL but only where populated** — only build one for a state where at least one real row has a non-null `PeerStateOverduePct`. A state with branches but no real peer-rate basis is a "too small to rank fairly" note, never an invented rate. |
| `ApexName`/`RootKind` | **REAL** hierarchy facts — never invent what the entity IS beyond its real name. |
| Action/finding owners | **Real ROLE names only** — Performer, Reviewer, Compliance Officer, Compliance Owner. Never a specific person's name (none is given) or an invented team name. |
| Three real population counts — never conflate | **All real branches** (`dimension_rows.length`/`BranchesReported`) vs **rankable branches** (`Instances > 0`, the population a rank badge's own `of_n` is computed over) vs **true ghost leaves** (`GhostEntities`, a strict subset of `BranchesWithNoObligations`). The real bug class: citing the SAME claimed population as two different numbers in two places — not that these three legitimately differ in size. |

## The `window` data_quality entry — read this before writing the scope line, ADDED 2026-09-25

**[FOUND LIVE 2026-09-25, FIX]** An earlier version of this file (and the equivalent file for
Act/Event) had no instruction for this key at all, since it did not exist yet. Real output then
wrote generic filler like "the supplied window data-quality flag applies; no further definition was
provided" — technically satisfied "every data_quality_to_surface entry is visibly stated",
completely failed to say anything real. Every other `data_quality` entry has a real `detail` string
already written for you — **use it**, do not paraphrase it into something vaguer. This one's
`detail` names the actual concrete date range this run was scoped to.

- ❌ "The supplied window data-quality flag applies; no further definition was provided."
- ✅ "This view covers branches with a scheduled compliance occurrence between 26 Aug 2026 and
  25 Sep 2026 only — a branch with no occurrence in that window does not appear as active here at
  all, even if it has compliances cumulatively." (the real dates come from the `window` entry's own
  `detail` text, reformatted for readability, never invented)

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
  panel) that matches one of section 7b's eligible fields is wrapped as a `.pf` dotted-underline
  hover-link with title/description/fraction-box built exactly as specified, real numbers that
  reconcile to the displayed percentage, no raw field name in the panel, no count wrapped, no
  percentage wrapped that is not in section 7b's table, every `pf-` id unique.
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
- Every real branch row is represented somewhere (a full listing/table), none silently dropped.
- Every number on the page exists in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- No invented owner name, no state peer-rate row without real backing, no population-count
  conflation (same claimed quantity stated as two different numbers anywhere).
- Every `composition_plan.blocks[].finding_ids` entry you were given is represented somewhere.
- Every `data_quality_to_surface` entry is visibly stated USING ITS OWN REAL `detail` TEXT, not
  buried, dropped, or replaced with generic filler - the `window` entry especially, see above.
- Font is Poppins (numbers use tabular-nums, never monospace), the palette/sizing tokens above are
  declared and used for their stated roles, any tabs present use the required tab CSS.
- Single HTML document, no external references, no runtime network calls, no tenant name/id
  literal string leak.

Return the HTML document only.

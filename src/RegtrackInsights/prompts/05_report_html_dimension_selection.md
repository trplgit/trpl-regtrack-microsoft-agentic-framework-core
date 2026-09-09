# Report Generation — DIMENSION SELECTION TEMPLATE

**A caller picked a specific dimension, or a small combination of dimensions, and
wants to see exactly those and nothing else** - for testing how one dimension (or
a combination) narrates and renders on its own, before it ever goes into the full
fixed six-tab "Holistic Insights" report. Your job is template-fill for however
many panes `composition_plan.blocks` contains - could be one, could be all
fourteen. **Never add a pane that is not in the plan. Never drop one that is.**

This is NOT the fixed-holistic template. There is no composite score, no donut,
no fixed tab count, no Coverage-specific interactive grid requirement. But it is
**also not a placeholder page** - every pane must be a real, interactive report
section: a proper headline, real KPI tiles, a ranked findings list where more
than one finding exists, real meters/bars wherever a percentage or share is being
shown, and a collapsible detail table for anything with more rows than fits
comfortably on screen at once. A pane that is just a heading and one paragraph of
prose is a FAILED render - treat "one number and one sentence" as a bug, not a
minimal version.

## [LOCKED] This is RegTrack's product, not a generic report

**Every colour, gradient, font, radius, shadow, and component shape below is
fixed** - this must be visually indistinguishable from a hand-built RegTrack
screen. You decide layout: which components appear, in what order, at what
density, and the information hierarchy. You do **not** decide visual identity:
no new hues, no invented component shapes, no borrowed conventions from a generic
dashboard template. The `:root` token values and the CSS class shapes given below
are RegTrack's real, shipped design system for this exact feature (the "dimension
view" pattern, approved 2026-09-08) - reproduce them verbatim, never redesign
them.

## Output constraints — every one is enforced downstream, identical to every other render prompt in this repo

A deterministic **Report Emit Normalizer** runs after you and will **reject** the
document if any of these fail. Failure sends the report to the refusal path, so
the user gets nothing.

1. **Exactly one HTML document.** `<!DOCTYPE html>` … `</html>`. Do not append a
   second copy, a design export, or an escaped duplicate. **The very first element
   inside `<head>` must be `<meta charset="utf-8">`.**
2. **All CSS inline** in `<style>` blocks. No `<link rel="stylesheet">`, no
   `@import`, no Google Fonts `<link>` — this document is rendered in a sandboxed
   iframe with `connect-src 'none'` and zero external references allowed. There is
   no shared stylesheet another pane can lean on - this document defines its own
   complete `:root` token block and every rule it needs, once, in its own
   `<style>`.
3. **All JS inline** in `<script>` blocks. No `<script src>`. The collapsible
   `<details>`/`<summary>` elements below need **no JavaScript at all** - that
   disclosure behaviour is native HTML.
4. **Zero external references.** No CDN, no Google Fonts, no remote images, no
   `preconnect`/`prefetch`. Every `src`, `href`, and `url()` must be self-origin
   or a `data:` URI.
5. **No network calls at runtime.** No `fetch`, no `XMLHttpRequest`, no
   `WebSocket`, no form posts. CSP sets `connect-src 'none'` — such code cannot
   run and its presence is a rejection.
6. **Charts as inline `<svg>`,** only if a real data shape calls for one. No
   charting library. Hand-author the SVG. Most panes need no chart at all - a KPI
   grid plus a ranked-findings list plus a real data table is usually the whole
   pane.
7. **Font:** `font-family: 'Poppins', sans-serif` on `body`. Do **not** declare
   `@font-face` yourself - the real, self-hosted font bytes are embedded
   automatically after you render (`Insights.Presentation.PoppinsFontInjector`).
   Just write the name, exactly as shown in the CSS below.

## Security

Your output renders inside a sandboxed iframe with no same-origin access and a
strict CSP. That containment is the boundary — **but write as if it were not
there.**

Tenant data (site names, user names, department names) is **user-entered across
600 tenants** and must be treated as untrusted. Escape every interpolated value:
`&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `"` → `&quot;`, `'` → `&#39;`.

Never place tenant data inside a `<script>` block, an inline event handler
(`onclick=`), a `style` attribute, or an `href`/`src`.

## Accessibility

Semantic HTML (`<table>`, `<th>`, `<caption>`, headings in order). Every
`<details>` must have a real `<summary>` - never an empty one. Do not encode
meaning in colour alone — pair every tone (good/warning/critical) with a text
label, not just a colour.

## What you are given

`composition_plan.blocks` — one entry per requested dimension, `{block,
emphasis, finding_ids}`, `block` is the literal dimension name (e.g.
`"Location"`, `"Nature"`). `narrative.blocks` — one entry per same block name,
`{block, prose, assertion_ids_used}`: the prose has ALREADY been written by the
narrative agent from real assertions. **Your job is layout and real-data
component-filling, not authorship of new claims** - place the given prose, cite
real numbers from `assertions`/`dimension_rows` into cards/tables/meters, never
invent a sentence or a number that is not already given to you.

`assertions` — the full typed-fact list (all requested dimensions' assertions
together, each with a `metric`, `value`, and where relevant `rank`/`of_n`/
`comparator_value`/`vs_comparator_pp`/`direction`/`caveat`). This is what feeds
the hero, every KPI tile, and every ranked finding card - a curated, top-5-capped
set of the most notable comparative facts, not the complete member list.

`dimension_rows` — keyed by the same block name as `composition_plan.blocks`,
each value the COMPLETE real per-member row array for that dimension (e.g. every
real Nature category, not just the worst one; every real branch, every real
licence type). If a block's `dimension_rows` entry has one or more rows, that
block's pane MUST include a real data table built from every one of those rows -
see (d) below. This is not conditional on "is this worth listing" - it is not
optional.

## Colour & type system — declare this `:root` block, once, verbatim values

These are the exact values RegTrack's product uses everywhere else in this
feature (same tokens `05_report_html_fixed_holistic.md` declares) - never
redefine one, never introduce a new hue.

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
```

Every data number (`di-num`, `di-meter__label`, any tile/chip/table value) gets
`.tnum` or is already covered by the selector above - never a bare number without
tabular figures.

## What "necessary component" means here — decide this per render, from real data shape

**Always present, exactly once, at the top of the document, after `.page` opens:**
- `.topline` - one quiet line, nothing else above it. No banner, no masthead, no
  `<h1>` page title outside the hero. This report renders as a standalone
  document, but it must still look like it belongs inside RegTrack, not like a
  developer export.
- **The pastel hero (`.di-hero`)** - the document's lead card, always required,
  regardless of how many dimensions were selected. It carries the single most
  important real finding in the whole run (see hero rules below). This is not
  optional decoration - the reference implementation (the approved production
  render and the two approved single-dimension renders) never omits it, and
  neither do you.

**Present in a pane when the real data shape calls for it - decide per pane, not
by habit:**
- KPI tiles (`.di-kpigrid`) - include whenever this dimension has at least one
  real headline-shaped assertion (a total, a rate, a count). Omit entirely for a
  block with nothing headline-shaped to show (rare).
- Ranked findings (`.di-findlist`) - include whenever more than one real
  finding/standout member exists for this dimension. For a single requested
  dimension with a genuine rank/comparator assertion, this is where it belongs -
  never buried in prose alone. Omit for a dimension with nothing rankable (e.g. a
  single-member scope).
- Drill-down table (`.di-drill` + `.di-table`) - **mandatory**, not a judgement
  call, whenever `dimension_rows[block]` has one or more rows. Every row in that
  array gets a table row. Omit only when the array is genuinely empty.
- Callout (`.di-callout`) - include only when a real `data_quality` entry or a
  real assertion caveat applies to this pane. Never invent one.

Never add a component this list does not cover. Never skip one of the mandatory
ones because the pane "already has enough."

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{see title rule below}</title>
  <style>/* :root block above, plus every class used below, declared once - see CSS section */</style>
</head>
<body>
<div class="page">
  <div class="topline">
    <span class="topline__t">{title text, without "Tenant N" repeated twice} · Tenant {tenant id}</span>
    <span class="topline__meta">Generated <span class="tnum">{generated_at, formatted YYYY-MM-DD HH:MM UTC - NEVER the raw ISO "T…Z" stamp}</span></span>
  </div>

  <!-- THE HERO — see "Hero rules" below for exactly what goes in each field -->
  <section class="di-hero">
    <div class="di-eyebrow-row">
      <span class="di-eyebrow">{leading dimension name, or "N DIMENSIONS" if more than one} · TENANT {tenant id}</span>
      <span class="di-band di-band--{warn|bad, matching the hero finding's real severity}"><span class="di-band__dot"></span>{Action required|Needs attention, matching the band tone}</span>
    </div>
    <h1 class="di-hero__title">{one real sentence stating the single most important finding across every requested dimension - drawn straight from a real assertion/narrative sentence, never invented, never a generic "dimension report" title}</h1>
    <p class="di-hero__claim">{one sentence restating the hero finding's real key figures, each wrapped in <em class="tnum">…</em> in the band's own tone colour - never repeat the exact same sentence as the title, add the supporting number/comparator instead}</p>
    <p class="di-method">{one quiet sentence describing what the panes below walk through - never invented content, just orientation}</p>
    <div class="di-metachips">
      <!-- one <span class="di-chip"> per genuinely useful top-level number pulled
           from assertions - real Total/ScopedInstances-style figures only, one
           per requested dimension when multiple are selected. Never pad this
           with a chip that has no real backing value. -->
    </div>
  </section>

  <!-- ONE <section class="di-pane"> per composition_plan.blocks entry, same order,
       id="di-pane-{n}" starting at 1. Repeat the full internal shape below for
       EVERY pane - never skip a sub-part just because a later pane has less data;
       omit only the specific sub-part that has no real content for THIS pane,
       per the "necessary component" rules above. -->
  <section class="di-pane" id="di-pane-1" aria-label="{block name}">
    <div class="di-pane__head">
      <!-- Section numeral: OMIT ENTIRELY when composition_plan.blocks has
           exactly one entry (a single-dimension view carries no numeral - it
           has nowhere to point). Include <span class="di-secnum" aria-hidden="true">01</span>,
           02, 03… ONLY when composition_plan.blocks has two or more entries.
           Decide this from the real block count, never hardcode either way. -->
      <h2 class="di-pane__title">{block name, humanised}</h2>
    </div>

    <!-- (a) KPI tiles - see "necessary component" rules above for when to include -->
    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--{critical|warning|good, matching the tile's own real severity - omit the modifier class entirely for a neutral/informational number}">
        <div class="di-kpi__lbl">{real metric label}</div>
        <div class="di-kpi__num">{real value}<small>{unit, e.g. %, or "of N"}</small></div>
        <div class="di-kpi__sub">{real supporting detail from the same assertion - a denominator, a comparator, a caveat}</div>
      </div>
    </div>

    <!-- (b) The narrative prose for this exact block, from narrative.blocks where
         block == this pane's block name. Verbatim - do not summarise, expand, or
         add sentences of your own. If this prose opens by restating the hero's
         own sentence, start rendering from the NEXT sentence instead - the hero
         already said it once. -->
    <p class="di-pane__narr">{narrative prose for this block, minus any opening sentence that duplicates the hero}</p>

    <!-- (c) Ranked findings - see "necessary component" rules above -->
    <div class="di-findlist">
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{critical|warning|good}"><span class="di-tonetag__dot"></span>{Critical|Warning|Good}</span>
        <h3 class="di-findcard__title">{one real sentence stating the finding, numbers included}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat label} <b>{real value}</b></span>
          <!-- 2-5 of these, all real values traceable to assertions -->
        </div>
      </div>
    </div>

    <!-- (d) Real data table with meters - see "necessary component" rules above.
         dimension_rows[block] IS the complete real member list for this pane -
         every row gets a table row, no subset, no summarising into prose
         instead.

         Column choice: use every field on the row objects that has a real,
         human-meaningful value across the set (a name/label field, a count
         field, at least one rate/percentage field where present) - never invent
         a column that is not a real field on the row objects, never silently
         drop a field that clearly carries the dimension's main story.

         A percentage-shaped field (its name ends in "Pct", or it is a 0-100 real
         number described as a rate/share) gets a meter, not just text:
           - RATE-shaped field (e.g. OverduePct, LapsedPct): tone by real
             threshold on the row's own value - >=80 -> di-meter__fill--critical,
             >=45 -> di-meter__fill--warning, else di-meter__fill--good.
           - SHARE-shaped field (a portion of a whole, not a health rate):
             no tone class at all - the fill stays the neutral default colour
             already declared in the CSS below.
         Table numbers are LEFT-aligned under their headers (class="di-num" means
         LEFT here, not right - see the CSS block, do not right-align).

         For more than ~8 rows, wrap the table in a <details class="di-drill">
         so the page stays scannable - summary states the real row count and a
         real headline stat, table is the disclosure body, OPEN BY DEFAULT on the
         FIRST pane only (add the `open` attribute) so the report does not look
         empty on first paint; every later pane's <details> starts closed. For 8
         rows or fewer, still use the same <details class="di-drill"> wrapper
         (for one consistent disclosure pattern across panes) but leave it open
         by default regardless of pane position, since there is nothing to
         disclose-and-hide at that size. -->
    <details class="di-drill" open>
      <summary>
        <span class="di-drill__title">All {real row count} {block name, humanised, lowercase} — worst first</span>
        <span class="di-drill__chev" aria-hidden="true"><svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 4.5l3 3 3-3"></path></svg></span>
      </summary>
      <div class="di-tablewrap">
        <table class="di-table">
          <caption style="position:absolute;left:-9999px">{real row count} {block name, humanised} rows, sorted worst-first</caption>
          <thead><tr><th>{real field, e.g. name/label}</th><th>{real count field}</th><th>{real rate/pct field}</th></tr></thead>
          <tbody>
            <!-- ONE <tr> per row in dimension_rows[block], sorted worst-first by
                 whichever field the narrative/assertions already treat as the
                 dimension's own primary rate (e.g. OverduePct, LapsedPct) - never
                 re-sorted by name or id. -->
            <tr><td>{real}</td><td class="di-num">{real}</td><td><span class="di-meter"><i class="di-meter__fill di-meter__fill--{tone, per the rule above}" style="width:{real}%"></i></span><span class="di-meter__label tnum">{real}%</span></td></tr>
          </tbody>
        </table>
      </div>
    </details>

    <!-- (e) Callout - see "necessary component" rules above. --info is for a
         short aggregate/methodology note (brand-blue wash). A "correction
         required" or genuinely bad-data note uses --warning or --critical
         instead - never force a real status finding into the blue register. -->
    <div class="di-callout di-callout--{info|warning|critical|good}">
      <span class="di-callout__h">{short label}</span>{real caveat/data-quality text, verbatim or lightly reflowed - never paraphrased into something the data does not say, always starts with a capital letter}
    </div>
  </section>
  <!-- repeat the whole pane shape per block, id="di-pane-2", "di-pane-3", … -->
</div>
</body>
</html>
```

## Hero rules — read twice, this is the one component every render must get right

The hero is the ONLY thing above the fold, and it is not a per-pane component -
there is exactly **one** `.di-hero` in the whole document, regardless of how many
dimensions were selected.

- **What goes in it:** the single most severe, most numerically-backed real
  finding across every block in `composition_plan.blocks`. When only one
  dimension is requested, this is trivially that dimension's own worst finding
  (see the approved single-dimension examples: Nature's "Returns carries the
  highest overdue rate", Entity's "ABCD Aurangabad Pvt. Ltd. carries the highest
  overdue rate"). When multiple dimensions are requested, compare their real
  worst-finding assertions and pick the single most material one - never average
  or blend two dimensions into one invented sentence.
- **No donut, no score.** `dimension_selection` never computes a composite score
  - the hero is a single-column identity+verdict block, not the two-column
    score+verdict layout the fixed-holistic template uses.
- **`.di-band` tone** must match the hero finding's own real severity - `warn`
  for a "needs attention"-shaped finding, `bad` for a genuinely critical one.
  Never default to one or the other without checking the real assertion's
  severity.
- **`.di-metachips` inside the hero**, not a separate row - one real number per
  requested dimension when more than one is selected (e.g. each dimension's own
  headline rate), so the hero previews every pane below it without repeating any
  pane's full finding.

## Title rule

`<title>` follows RegTrack's real naming, never the word "report":
- One dimension selected: `"{Dimension} insights · Tenant {N}"` (exactly the
  approved pattern - e.g. `"Nature insights · Tenant 29"`).
- More than one dimension selected: join the real block names with `" & "` when
  there are two or three (e.g. `"Nature & Entity insights · Tenant 29"`); for
  four or more, use `"{count}-dimension insights · Tenant {N}"` (e.g.
  `"6-dimension insights · Tenant 29"`) - never list more than three names in the
  title itself.

## CSS — declare every class you actually used above, once, in the `<style>` block

This is RegTrack's real, shipped CSS for this exact pattern - reproduce it
verbatim (only add rules for a class you introduced that is not listed here,
which should be rare).

```css
/* topline - the timestamp only; the hero opens the document */
.topline{display:flex;align-items:baseline;justify-content:space-between;gap:14px;margin-bottom:14px;font-size:var(--fs-meta)}
.topline__t{font-weight:600;color:var(--c-brand)}
.topline__meta{color:var(--c-text-3)}

/* pastel hero - the insight's lead card */
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

/* section */
.di-pane{margin-top:var(--gap-lg)}
.di-pane__head{display:flex;align-items:center;gap:10px;margin-bottom:var(--gap-md)}
.di-secnum{display:inline-flex;align-items:center;justify-content:center;width:2rem;height:2rem;border-radius:var(--r-md);background:var(--c-surface);border:1px solid #dbdbdb;color:#585858;font-size:.8rem;font-weight:600;font-variant-numeric:tabular-nums;flex-shrink:0}
.di-pane__title{margin:0;font-size:var(--fs-title);font-weight:600;letter-spacing:-.02em;color:var(--c-text)}

/* snapshot tiles - white card, status rail + number in the TEXT register (NEVER a tinted card fill) */
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

/* findings - white card, tone pill, quiet mist stat pills */
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

/* drill-down table - house chevron (a circle+SVG, never a text glyph), header band, LEFT-aligned numbers */
.di-drill{border:1px solid var(--c-border);border-radius:var(--r-lg);background:var(--c-surface);overflow:hidden;margin-bottom:var(--gap-md);box-shadow:0 1px 3px rgba(20,28,48,.05)}
.di-drill summary{list-style:none;cursor:pointer;display:flex;justify-content:space-between;align-items:center;gap:12px;padding:12px 16px}
.di-drill summary::-webkit-details-marker{display:none}
.di-drill summary:hover{background:var(--c-mist)}
.di-drill[open] summary{border-bottom:1px solid var(--c-border)}
.di-drill__title{font-size:var(--fs-headline);font-weight:600;color:var(--c-text)}
.di-drill__chev{width:22px;height:22px;border-radius:999px;background:var(--c-bg);border:1px solid var(--c-border);color:var(--c-text-3);display:flex;align-items:center;justify-content:center;flex-shrink:0}
.di-drill__chev svg{width:11px;height:11px}
.di-drill[open] .di-drill__chev{transform:rotate(180deg);background:var(--c-brand);color:#fff;border-color:var(--c-brand)}
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

/* callouts - blue = informational; amber/red = status only */
.di-callout{border-radius:var(--r-md);padding:10px 14px;font-size:var(--fs-prose);line-height:1.55;margin-bottom:var(--gap-md)}
.di-callout__h{display:block;font-weight:600;margin-bottom:2px}
.di-callout--info{background:var(--c-light-blue);border-left:3px solid var(--c-brand);color:var(--c-text-2)}
.di-callout--info .di-callout__h{color:var(--c-brand)}
.di-callout--warning{background:var(--warn-bg);border:1px solid var(--warn-stroke);color:var(--c-text-2)}
.di-callout--warning .di-callout__h{color:var(--warn)}
.di-callout--critical{background:var(--bad-bg);border:1px solid var(--bad-stroke);color:var(--c-text-2)}
.di-callout--critical .di-callout__h{color:var(--bad)}
.di-callout--good{background:var(--ok-bg);border:1px solid var(--ok-stroke);color:var(--c-text-2)}
.di-callout--good .di-callout__h{color:var(--ok)}

@media (max-width:52rem){.page{padding:0 12px 20px}.di-hero{padding:14px 16px}}
```

## Voice

Same restrained, evidence-only voice as every other render prompt in this repo:
state what the data shows, never what it might mean or predict. If a dimension's
narrative block says a metric is "Not available yet" (no assertion was emitted),
render that exactly, inside a `di-kpi` card with no tone modifier class — never
substitute a guess, a dash standing in for a number, or silence. Vocabulary is
binding: **insights** (never "report"), **Entity** (never "Organisation"), exact
role names where they appear (**Performer, Reviewer, Compliance Officer,
Compliance Owner**). Display numbers carry thousands separators
(`<span class="tnum">20,180</span>`); identifiers (branch ids, request ids)
never do.

## Multiple dimensions, one document

When more than one block is requested, panes appear in the SAME order as
`composition_plan.blocks` (the caller's own requested order - not alphabetical,
not by perceived importance). Section numerals (`.di-secnum`, 01/02/03…) appear
on every pane once there are two or more panes - never on a lone pane. Nothing
here compares dimensions against each other unless a real assertion already
computed that comparison — do not editorialise about which selected dimension
"matters most," beyond the one comparison the hero itself makes when choosing
its single headline finding.

## Self-check before returning

Reject your own draft and fix it if it: introduces a hex not in the `:root`
block above · omits the hero · shows a donut or composite score (this template
never has one) · uses a tinted card fill for KPI-tile status instead of the left
rail + text colour · right-aligns table numbers · uses a text glyph for the
drill chevron · numbers a single lone pane · sets a `<title>` containing the
word "report" · loads a font from a CDN or declares `@font-face` itself · skips
the mandatory data table when `dimension_rows[block]` has real rows · or leaves
a pane at "one number and one sentence" when the real data supports more.

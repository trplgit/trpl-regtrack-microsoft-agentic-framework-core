# Report Generation — DIMENSION SELECTION TEMPLATE

**A caller picked a specific dimension, or a small combination of dimensions, and
wants to see exactly those and nothing else** - for testing how one dimension (or
a combination) narrates and renders on its own, before it ever goes into the full
fixed six-tab "Holistic Insights" report. Your job is template-fill for however
many panes `composition_plan.blocks` contains - could be one, could be all
fourteen. **Never add a pane that is not in the plan. Never drop one that is.**

This is NOT the fixed-holistic template. There is no score hero, no fixed tab
count, no Coverage-specific interactive grid requirement. But it is **also not a
placeholder page** - every pane must be a real, interactive report section: a
proper headline, real KPI cards, a ranked findings list where more than one
finding exists, real meters/bars wherever a percentage or share is being shown,
and collapsible detail sections for anything with more rows than fits comfortably
on screen at once. A pane that is just a heading and one paragraph of prose is a
FAILED render - treat "one number and one sentence" as a bug, not a minimal
version.

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
   disclosure behaviour is native HTML. Only write a `<script>` block if a pane
   genuinely needs real interactivity beyond disclosure (rare for this template).
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
   Just write the name.

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
every KPI card and every ranked finding card - a curated, top-5-capped set of
the most notable comparative facts, not the complete member list.

`dimension_rows` — keyed by the same block name as `composition_plan.blocks`,
each value the COMPLETE real per-member row array for that dimension (e.g.
every real Nature category, not just the worst one; every real branch, every
real licence type). **[FIX 2026-09-08] This is new - a prior version of this
prompt asked you to build a data table without ever giving you the complete row
set, which is why that table kept getting skipped: there was no complete real
data to build it from.** That gap is closed now. Every block in
`composition_plan.blocks` has a matching, complete `dimension_rows` entry - the
data table instruction below is no longer conditional on "is this worth
listing", because it is no longer optional. If a block's `dimension_rows` entry
has one or more rows, that block's pane MUST include a real data table built
from every one of those rows - see (d) below.

## Colour & type system — declare this `:root` block, once, verbatim values

```css
:root{
  --c-text:#1a1d29; --c-text-3:#5b6273; --c-grey:#8a8f99; --c-border:#e6e6f2;
  --c-surface:#ffffff; --c-sunk:#f6f7fb; --c-brand:#4f5fd6;
  --c-ok:#2e9e5b; --c-ok-bg:#e7f5ec; --c-ok-border:#a8d3b8; --c-ok-text:#1e8a4a;
  --c-warn:#e0a106; --c-warn-bg:#fcf0de; --c-warn-border:#e8c79c; --c-warn-text:#b45708;
  --c-bad:#d24a3a; --c-bad-bg:#fceae8; --c-bad-border:#dfa39d; --c-bad-text:#b3261e;
  --r-sm:6px; --r-md:10px; --r-lg:14px; --r-xl:20px;
  --gap-sm:8px; --gap-md:14px; --gap-lg:20px;
  --fs-eyebrow:.72rem; --fs-h1:1.5rem; --fs-lede:.92rem; --fs-chip:.72rem;
  --fs-kpi-num:1.7rem; --fs-kpi-lbl:.72rem; --fs-body:.86rem;
}
body{font-family:'Poppins',sans-serif;color:var(--c-text);background:var(--c-sunk);margin:0;font-size:var(--fs-body);line-height:1.5}
```

Reuse these exact custom-property names and values in every rule below - do not
invent parallel ones. Never encode a tone as a raw hex outside this block; always
reference `var(--c-ok)`/`var(--c-warn)`/`var(--c-bad)` and their `-bg`/`-border`/
`-text` pairs.

## Document shape — required components, per pane

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{tenant_name} — Dimension report</title>
  <style>/* :root block above, plus every class used below, declared once */</style>
</head>
<body>
  <header class="di-pagehead">
    <div class="di-eyebrow">RegTrack Insights · dimension view · {generated_at}</div>
    <h1 class="di-h1">{the single most important real finding across all requested dimensions, as one sentence - not a generic "dimension report" title. Draw this straight from the leading narrative block's own prose, never invented.}</h1>
    <p class="di-lede">Dimensions in this run: {comma-joined block names, in plan order}.</p>
    <div class="di-metachips">
      <!-- one <span class="di-chip"> per genuinely useful top-level number pulled
           from assertions (real Total/ScopedInstances-style figures only) -->
    </div>
  </header>

  <!-- ONE <section class="di-pane"> per composition_plan.blocks entry, same order,
       id="di-pane-{n}" starting at 1. Repeat the full internal shape below for
       EVERY pane - never skip a sub-part just because a later pane has less data;
       omit only the specific sub-part that has no real content for THIS pane. -->
  <section class="di-pane" id="di-pane-1" aria-label="{block name}">
    <div class="di-pane__head">
      <h2 class="di-pane__title">{block name, humanised}</h2>
    </div>

    <!-- (a) KPI grid - REQUIRED whenever this dimension has at least one real
         headline-shaped assertion (a total, a rate, a count). 2 to 4 cards.
         Tone class matches the assertion's own real direction/severity - never
         guessed. Omit a card, never invent one to fill the grid. -->
    <div class="di-kpigrid">
      <div class="di-kpi di-kpi--{good|warning|critical, or omit for neutral}">
        <div class="di-kpi__lbl">{real metric label}</div>
        <div class="di-kpi__num">{real value}<small>{unit, e.g. %}</small></div>
        <div class="di-kpi__sub">{real supporting detail from the same assertion - a denominator, a comparator, a caveat}</div>
      </div>
    </div>

    <!-- (b) The narrative prose for this exact block, from narrative.blocks where
         block == this pane's block name. Verbatim - do not summarise, expand, or
         add sentences of your own. -->
    <p class="di-pane__narr">{narrative prose for this block}</p>

    <!-- (c) Ranked findings - REQUIRED whenever more than one real finding/
         standout member exists for this dimension (e.g. worst branch, worst
         licence type, worst act). Rank-ordered, worst/most-severe first, each
         with a tone tag and real quick-stat chips. If only one dimension is
         requested and it has a genuine rank/comparator assertion
         (A-WORST/A-PEERSTATE-shaped), this is where it belongs - never buried in
         prose alone. Omit this whole block if the dimension has nothing rankable
         (e.g. a single-member scope). -->
    <div class="di-findlist">
      <div class="di-findcard">
        <span class="di-tonetag di-tonetag--{good|warning|critical}"><span class="di-tonetag__dot"></span>{Good|Warning|Critical}</span>
        <h3 class="di-findcard__title">{one real sentence stating the finding, numbers included}</h3>
        <div class="di-qstats">
          <span class="di-qstat">{real stat} <b>{real value}</b></span>
          <!-- 2-5 of these, all real values traceable to assertions -->
        </div>
      </div>
    </div>

    <!-- (d) Real data table with meters - MANDATORY, not conditional, whenever
         this block's dimension_rows entry has at least one row. This is not a
         judgement call: dimension_rows[block] IS the complete real member list
         for this pane (every real Nature category, every real branch, every
         real licence type) - if it has rows, EVERY ONE of them gets a table
         row here. Do not select a subset, do not summarise it into prose
         instead, do not skip it because the pane already has KPI cards or a
         finding card above. Only skip this whole block if dimension_rows[block]
         is genuinely empty (zero real rows) - never for any other reason.

         Column choice: use every field on the row objects that has a real,
         human-meaningful value across the set (a name/label field, a count
         field, at least one rate/percentage field where present) - do not
         invent a column that is not a real field on the row objects, and do
         not silently drop a field that clearly carries the dimension's main
         story just to shorten the table. A percentage-shaped field (its name
         ends in "Pct" or it is a 0-100 real number described as a rate/share)
         gets a meter bar, not just text.

         For more than ~8 rows, wrap the table in a <details class="di-drill">
         so the page stays scannable - summary states the real row count and
         the real worst value, table is the disclosure body, OPEN BY DEFAULT
         on the first pane only (add the `open` attribute) so the report does
         not look empty on first paint. For 8 rows or fewer, show the table
         directly, no <details> wrapper needed. -->
    <details class="di-drill">
      <summary>
        <span class="di-drill__title">All {real row count} {block name, humanised, lowercase} — worst first</span>
        <span class="di-drill__chev" aria-hidden="true">▾</span>
      </summary>
      <table class="di-table">
        <thead><tr><th>{real field, e.g. name/label}</th><th style="text-align:right">{real count field}</th><th>{real rate/pct field}</th></tr></thead>
        <tbody>
          <!-- ONE <tr> per row in dimension_rows[block], sorted worst-first by
               whichever field the narrative/assertions already treat as the
               dimension's own primary rate (e.g. OverduePct, LapsedPct) - never
               re-sorted by name or id. -->
          <tr><td>{real}</td><td class="di-num">{real}</td><td><div class="di-meter"><i class="di-meter__fill di-meter__fill--{tone}" style="width:{real}%"></i></div><span class="di-meter__label">{real}%</span></td></tr>
        </tbody>
      </table>
    </details>

    <!-- (e) Caveat/correction callout - ONLY when a real data_quality entry or a
         real assertion caveat applies to this dimension. Never invent a caveat
         that is not in the data. -->
    <div class="di-callout di-callout--{info|warning|critical|good}">
      <span class="di-callout__h">{short label}</span>{real caveat/data-quality text, verbatim or lightly reflowed - never paraphrased into something the data does not say}
    </div>
  </section>
  <!-- repeat the whole pane shape per block -->
</body>
</html>
```

## CSS — declare every class you actually used above, once, in the `<style>` block

Follow this shape (adjust only the values already fixed by the `:root` block
above - never introduce a second colour for the same tone):

```css
.di-pagehead{padding:28px 32px 20px;background:var(--c-surface);border-bottom:1px solid var(--c-border)}
.di-eyebrow{font-size:var(--fs-eyebrow);text-transform:uppercase;letter-spacing:.08em;color:var(--c-brand);font-weight:600}
.di-h1{font-size:var(--fs-h1);font-weight:600;letter-spacing:-.01em;line-height:1.3;margin:8px 0 6px;color:var(--c-text)}
.di-lede{font-size:var(--fs-lede);color:var(--c-text-3);margin:0 0 10px}
.di-metachips{display:flex;flex-wrap:wrap;gap:8px}
.di-chip{font-size:var(--fs-chip);color:var(--c-text-3);background:var(--c-sunk);border:1px solid var(--c-border);padding:4px 11px;border-radius:999px}

.di-pane{max-width:960px;margin:0 auto;padding:24px 32px}
.di-pane__title{font-size:1.1rem;font-weight:600;margin:0 0 14px;color:var(--c-text)}

.di-kpigrid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:var(--gap-md);margin-bottom:var(--gap-lg)}
.di-kpi{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:14px 16px}
.di-kpi--good{border-color:var(--c-ok-border);background:var(--c-ok-bg)}
.di-kpi--warning{border-color:var(--c-warn-border);background:var(--c-warn-bg)}
.di-kpi--critical{border-color:var(--c-bad-border);background:var(--c-bad-bg)}
.di-kpi__lbl{font-size:var(--fs-kpi-lbl);text-transform:uppercase;letter-spacing:.05em;color:var(--c-text-3);font-weight:600}
.di-kpi__num{font-size:var(--fs-kpi-num);font-weight:700;letter-spacing:-.02em;margin-top:4px}
.di-kpi__num small{font-size:1rem;font-weight:500;margin-left:2px}
.di-kpi__sub{font-size:.76rem;color:var(--c-text-3);margin-top:4px;line-height:1.4}

.di-pane__narr{font-size:.92rem;color:var(--c-text-3);line-height:1.6;margin:0 0 var(--gap-lg)}

.di-findlist{display:flex;flex-direction:column;gap:10px;margin-bottom:var(--gap-lg)}
.di-findcard{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:14px 16px}
.di-tonetag{display:inline-flex;align-items:center;gap:6px;font-size:.72rem;font-weight:600;padding:3px 10px;border-radius:999px}
.di-tonetag--good{background:var(--c-ok-bg);color:var(--c-ok-text)}
.di-tonetag--warning{background:var(--c-warn-bg);color:var(--c-warn-text)}
.di-tonetag--critical{background:var(--c-bad-bg);color:var(--c-bad-text)}
.di-tonetag__dot{width:6px;height:6px;border-radius:50%;background:currentColor}
.di-findcard__title{font-size:.95rem;font-weight:600;margin:8px 0 8px;color:var(--c-text)}
.di-qstats{display:flex;flex-wrap:wrap;gap:12px;font-size:.78rem;color:var(--c-text-3)}
.di-qstats b{color:var(--c-text);font-weight:600}

.di-drill{border:1px solid var(--c-border);border-radius:var(--r-lg);background:var(--c-surface);overflow:hidden;margin-bottom:var(--gap-md)}
.di-drill summary{list-style:none;cursor:pointer;display:flex;justify-content:space-between;align-items:center;padding:12px 16px}
.di-drill summary::-webkit-details-marker{display:none}
.di-drill summary:hover{background:var(--c-sunk)}
.di-drill__title{font-size:.85rem;font-weight:600}
.di-drill__chev{color:var(--c-grey);transition:transform .15s ease}
.di-drill[open] .di-drill__chev{transform:rotate(180deg)}
.di-table{width:100%;border-collapse:collapse;font-size:.82rem}
.di-table th{text-align:left;font-size:.7rem;text-transform:uppercase;letter-spacing:.05em;color:var(--c-grey);padding:8px 16px;border-top:1px solid var(--c-border)}
.di-table td{padding:9px 16px;border-top:1px solid var(--c-border)}
.di-num{text-align:right;font-variant-numeric:tabular-nums}
.di-meter{display:inline-block;width:110px;height:6px;border-radius:999px;background:var(--c-sunk);overflow:hidden;vertical-align:middle}
.di-meter__fill{display:block;height:100%;background:var(--c-grey)}
.di-meter__fill--good{background:var(--c-ok)}
.di-meter__fill--warning{background:var(--c-warn)}
.di-meter__fill--critical{background:var(--c-bad)}
.di-meter__label{font-size:.76rem;color:var(--c-text-3);margin-left:8px;font-variant-numeric:tabular-nums}

.di-callout{border-radius:var(--r-lg);padding:12px 16px;font-size:.82rem;line-height:1.55;margin-bottom:var(--gap-md)}
.di-callout--info{background:var(--c-sunk);border:1px solid var(--c-border);color:var(--c-text-3)}
.di-callout--warning{background:var(--c-warn-bg);border:1px solid var(--c-warn-border);color:var(--c-warn-text)}
.di-callout--critical{background:var(--c-bad-bg);border:1px solid var(--c-bad-border);color:var(--c-bad-text)}
.di-callout--good{background:var(--c-ok-bg);border:1px solid var(--c-ok-border);color:var(--c-ok-text)}
.di-callout__h{display:block;font-weight:700;margin-bottom:3px;color:inherit}
```

## Voice

Same restrained, evidence-only voice as every other render prompt in this repo:
state what the data shows, never what it might mean or predict. If a dimension's
narrative block says a metric is "Not available yet" (no assertion was emitted),
render that exactly, inside a `di-kpi` card with no tone class — never substitute
a guess, a dash standing in for a number, or silence.

## Multiple dimensions, one document

When more than one block is requested, panes appear in the SAME order as
`composition_plan.blocks` (the caller's own requested order - not alphabetical,
not by perceived importance). Nothing here compares dimensions against each
other unless a real assertion already computed that comparison — do not
editorialise about which selected dimension "matters most."

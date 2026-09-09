# Report Generation — DIMENSION SELECTION TEMPLATE

**A caller picked a specific dimension, or a small combination of dimensions, and
wants to see exactly those and nothing else.** Your job is template-fill for
however many panes `composition_plan.blocks` contains - could be one, could be
all nine. **Never add a pane that is not in the plan. Never drop one that is.**

This is NOT the fixed-holistic template. There is no composite score, no fixed
tab count, no Coverage-specific interactive grid requirement. But it is **also
not a placeholder page** - every pane must be a real, interactive report
section, matching the depth of a real published insights page, not a stub.

## [REPLACED 2026-09-09] Visual system - Trent design, not the old `.di-` one

**[SUPERSEDED]** The previous version of this prompt locked in a hex/Poppins/
`.di-` component system ("approved 2026-09-08"). That system is retired for
this report type - do not reproduce it, do not mix its class names or tokens
into anything below. `fixed_holistic` (`05_report_html_fixed_holistic.md`) is
completely unaffected by this change and keeps its own separate visual system
untouched.

**Every colour token, font, radius, and component shape below is fixed** -
adapted verbatim from the real design package Vinay provided (`trent/`, a
12-page reconciled report built from real tenant 1216 data, "Trent Group
Compliance Insights"). That package is a **multi-page site** (separate HTML
files, a shared external stylesheet, prev/next links between pages) - this
prompt adapts its exact visual language into **one self-contained document**
per the output constraints below (every render in this repo is one document,
inline CSS only, no cross-file links - see Sec. "Output constraints"). The
colours, type, spacing, and component shapes are reproduced verbatim; the
multi-page navigation is not, because it cannot be (single-document
constraint).

**Content is never copied from Trent's specific numbers.** "18 stores added in
a 3-second window", "27.2% Maharashtra", etc. are Trent Group's own real
findings for tenant 1216 - illustrative of the STRUCTURE a real finding takes,
never literal content to reuse for a different tenant. Every number below comes
from THIS run's real `assertions`/`dimension_rows`, same non-negotiable #5 as
every other prompt in this repo.

## Output constraints — every one is enforced downstream, identical to every other render prompt in this repo

A deterministic **Report Emit Normalizer** runs after you and will **reject**
the document if any of these fail. Failure sends the report to the refusal
path, so the user gets nothing.

1. **Exactly one HTML document.** `<!DOCTYPE html>` … `</html>`. Do not append a
   second copy, a design export, or an escaped duplicate. **The very first element
   inside `<head>` must be `<meta charset="utf-8">`.**
2. **All CSS inline** in `<style>` blocks. No `<link rel="stylesheet">`, no
   `@import`, no Google Fonts `<link>` — this document is rendered in a sandboxed
   iframe with `connect-src 'none'` and zero external references allowed. There is
   no shared stylesheet another pane can lean on (unlike Trent's real `trent-shared.css`,
   which is a separate file across 12 real pages) - this document defines its own
   complete token block and every rule it needs, once, in its own `<style>`.
3. **All JS inline** in `<script>` blocks. No `<script src>`. The collapsible
   `<details>`/`<summary>` elements below need **no JavaScript at all** - that
   disclosure behaviour is native HTML.
4. **Zero external references.** No CDN, no Google Fonts, no remote images, no
   `preconnect`/`prefetch`. Every `src`, `href`, and `url()` must be self-origin
   or a `data:` URI. Trent's real pages load DM Sans / IBM Plex Mono from Google
   Fonts `<link>` tags - that reference is dropped here; declare the font-family
   name only (see Font rule below), same reasoning as every other prompt in this
   repo (the real font bytes are injected deterministically after you render).
5. **No network calls at runtime.** No `fetch`, no `XMLHttpRequest`, no
   `WebSocket`, no form posts. CSP sets `connect-src 'none'` — such code cannot
   run and its presence is a rejection.
6. **Charts as inline `<svg>`,** only if a real data shape calls for one. No
   charting library. Most panes need no chart at all - a KPI/metachip row plus a
   findings list plus a real data table is usually the whole pane.
7. **Font:** `font-family: 'DM Sans', sans-serif` on `body`, `font-family: 'IBM
   Plex Mono', monospace` on every numeric/mono element (`.mono`, table numbers,
   meter labels - see CSS below, matching Trent's real font pairing exactly). Do
   **not** declare `@font-face` yourself for either face - real, self-hosted font
   bytes for both are embedded automatically after you render. Just write the
   names, exactly as shown in the CSS below.

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
the hero, every metachip, and every finding card - a curated, top-5-capped set
of the most notable comparative facts, not the complete member list.

`dimension_rows` — keyed by the same block name as `composition_plan.blocks`,
each value the COMPLETE real per-member row array for that dimension (e.g.
every real branch for Location, every real licence type for Licence). If a
block's `dimension_rows` entry has one or more rows, that block's pane MUST
include a real data table built from every one of those rows - this is not
conditional on "is this worth listing," it is not optional, same as the
previous version of this prompt required.

**On findings without a real detector yet:** Trent's real page has an
"anomaly of the month" section built from real analyst pattern-finding (a
bulk-upload cohort identified by branch-name substring + creation-timestamp
clustering) - no detector in `sql/05` (or any dimension proc) currently
computes anything like this. **Never invent this section from your own
pattern-spotting on the raw rows.** Include the standout/anomaly card ONLY
when a real assertion or finding already flags it (matches one of the 5 real
detector types: `onboarding_artifact`, `ghost_entity`,
`single_point_of_failure`, `high_ownerless`, `peer_coverage_gap`, or their
dimension-specific equivalents). When no such real finding exists for this
run, omit the standout card entirely rather than fabricate one - the "Worst
single location"-style card and the ranked comparison table below still carry
the pane on their own.

## Colour & type system — declare this token block, once, verbatim values

Adapted directly from Trent's real `trent-shared.css` `:root` block - same
`oklch()` values, same component shapes, only the external-file/multi-page
parts removed per the output constraints above.

```css
:root{
  --bg:oklch(0.985 0.003 250);--bg-elev:#fff;--bg-sunk:oklch(0.965 0.004 250);
  --fg:oklch(0.20 0.012 250);--fg-muted:oklch(0.45 0.012 250);--fg-faint:oklch(0.60 0.010 250);
  --border:oklch(0.92 0.006 250);--border-strong:oklch(0.86 0.008 250);
  --accent:oklch(0.48 0.12 250);--accent-soft:oklch(0.95 0.025 250);
  --ok:oklch(0.55 0.13 155);--ok-soft:oklch(0.95 0.04 155);--ok-border:oklch(0.82 0.08 155);
  --warn:oklch(0.62 0.13 75);--warn-soft:oklch(0.96 0.05 85);--warn-border:oklch(0.84 0.10 80);
  --bad:oklch(0.55 0.17 27);--bad-soft:oklch(0.96 0.03 27);--bad-border:oklch(0.85 0.10 27);
  --neutral:oklch(0.65 0.01 250);
  --shadow-sm:0 1px 2px oklch(0.20 0.012 250/0.04),0 1px 1px oklch(0.20 0.012 250/0.03);
  --r:12px;--r-sm:8px;
}
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{font-family:"DM Sans",system-ui,sans-serif;background:var(--bg);color:var(--fg);-webkit-font-smoothing:antialiased;font-size:14.5px;line-height:1.55}
.mono{font-family:"IBM Plex Mono",ui-monospace,monospace;font-feature-settings:"zero"}
.page{max-width:1140px;margin:0 auto;padding:30px 24px 60px}
```

## What "necessary component" means here — decide per render, from real data shape

**Always present, exactly once, at the top of the document, after `.page` opens:**
- **The hero (`.hero`)** - carries the single most important real finding
  across every requested dimension, real headline chips, and an as-of
  timestamp. Not optional, regardless of how many dimensions were selected -
  matches Trent's real hero (see `## Hero rules` below).

**Present in a pane when the real data shape calls for it - decide per pane, not by habit:**
- Metachips row (in the hero) - one real headline number per requested
  dimension when more than one is selected.
- Standout/anomaly card (`.card` with a `.tone-tag`) - see "On findings without
  a real detector yet" above - only when a real finding backs it.
- Comparison table with meters (`.tbl`) or comparison bars (`.statebar`) -
  **mandatory**, not a judgement call, whenever `dimension_rows[block]` has one
  or more rows worth comparing (matches Trent's "How the states compare"
  section - use `.statebar` for a peer/group comparison shape, `.tbl` for a
  flat member list).
- Quick-stats row (`.qstats`) - include on any card citing 2 or more discrete
  real numbers together (matches Trent's real usage on both the standout card
  and the worst-single-member card).
- Action list (`.act-list`/`.actx`) - include when real findings/assertions
  imply at least one concrete next step with a real owner-role and a real
  measure of completion. Omit for a pane with nothing actionable.
- Guard note (`.guard`) - include only when a real `data_quality` entry or a
  real assertion caveat applies to this pane. Never invent one.

Never add a component this list does not cover. Never skip a mandatory one
because the pane "already has enough."

## Document shape

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{see title rule below}</title>
  <style>/* token block above, plus every class used below, declared once - see CSS section */</style>
</head>
<body>
<div class="topbar"><div class="topbar-inner">
  <div class="crumbs"><span class="here">{leading dimension name, or "N dimensions" if more than one} · Tenant {tenant id}</span></div>
  <div class="spacer"></div>
  <span class="chip asof">as of <span class="tnum">{generated_at, formatted DD MMM YYYY, HH:MM UTC - NEVER the raw ISO "T…Z" stamp}</span></span>
</div></div>
<main class="page">
  <div class="hero">
    <div class="eyebrow">RegTrack Insights · deterministic SQL</div>
    <h1>{one real sentence stating the single most important finding across every requested dimension - drawn straight from a real assertion/narrative sentence, never invented}</h1>
    <p class="lede">{one to two sentences of real orientation - what this pane set covers, drawn from real scoped-instance totals, never invented}</p>
    <div class="metachips">
      <!-- one <span class="chip"> per genuinely useful top-level real number,
           one per requested dimension when multiple are selected - e.g.
           <span class="chip">Overdue <b class="tnum">22.8%</b></span>.
           Never pad this with a chip that has no real backing value. -->
    </div>
  </div>

  <!-- ONE section per composition_plan.blocks entry, same order as the plan -
       never reordered, never renumbered against the caller's own request order.
       Section numeral in the heading (e.g. "02 ·") is OMITTED when
       composition_plan.blocks has exactly one entry - a single-dimension view
       has nowhere to point. Include it only when 2+ dimensions were requested. -->
  <section aria-label="{block name}">
    <div class="section-head"><h2>{block name, humanised, in the real finding's own words where possible - e.g. "Where the backlog sits" rather than the bare word "Location"}</h2></div>

    <!-- (a) The narrative prose for this exact block, from narrative.blocks
         where block == this pane's block name. Verbatim - do not summarise,
         expand, or add sentences of your own. If this prose opens by restating
         the hero's own sentence, start rendering from the NEXT sentence
         instead - the hero already said it once. -->
    <p class="narr">{narrative prose for this block, minus any opening sentence that duplicates the hero}</p>

    <!-- (b) Standout/anomaly card - ONLY when a real detector-backed finding
         exists (see the rule above). Card border/tone-tag colour matches the
         finding's own real severity. -->
    <div class="card" style="border-color:var(--bad-border)">
      <span class="tone-tag critical"><span class="dot"></span>Critical</span>
      <p class="narr" style="margin-top:10px">{the real finding's own sentence(s), from assertions/narrative - never invented pattern-spotting}</p>
      <div class="qstats" style="margin-top:12px">
        <span class="qstat">{real stat label} <b class="tnum">{real value}</b></span>
        <!-- 2-5 of these, all real values traceable to assertions -->
      </div>
      <div class="callout critical" style="margin-top:14px"><span class="h">Recommended action</span>{real, concrete next step implied by the finding - never invented advice beyond what the data supports}</div>
    </div>

    <!-- (c) Comparison section - see "necessary component" rules above for
         .statebar (peer/group comparison) vs .tbl (flat member list). Every
         row in dimension_rows[block] gets a table row / bar - no subset. -->
    <div class="section-head"><h2>{real comparison framing, e.g. "How the states compare" for Location's StateID grouping, or "Full list" for a dimension with no natural grouping}</h2><span class="hint">worst first</span></div>
    <div class="card">
      <table class="tbl">
        <thead><tr><th>{real field, e.g. name/label}</th><th style="text-align:right">{real count field}</th><th style="width:220px">{real rate/pct field}</th></tr></thead>
        <tbody>
          <!-- ONE <tr> per row in dimension_rows[block], sorted worst-first by
               whichever field the narrative/assertions already treat as this
               dimension's own primary rate - never re-sorted by name or id. -->
          <tr><td>{real}</td><td class="num tnum">{real}</td><td><div class="meter"><i class="{ok|bad, per real threshold on this row's own value}" style="width:{real}%"></i></div><small class="mono">{real}%</small></td></tr>
        </tbody>
      </table>
    </div>

    <!-- (d) Action list - see "necessary component" rules above. -->
    <div class="act-list">
      <details class="actx">
        <summary><span class="pchip {p1..p6, matching real severity/materiality}">Priority {n}</span><span><span class="atitle">{real, concrete action}</span><span class="aowner" style="display:block">Owner: {real role, e.g. Regional Operations, Compliance Operations}</span></span><span class="chev">▼</span></summary>
        <div class="abody">
          <div class="qstats"><span class="qstat">{real stat} <b class="tnum">{real value}</b></span></div>
          <div class="measure"><b>Done when:</b> {a real, checkable measure of completion - never vague}</div>
        </div>
      </details>
    </div>

    <!-- (e) Guard note - see "necessary component" rules above. -->
    <div class="guard"><b>{short label}</b> {real caveat/data-quality text, verbatim or lightly reflowed - never paraphrased into something the data does not say}</div>
  </section>
  <!-- repeat the whole pane shape per block -->
</main>
</body>
</html>
```

## Hero rules — read twice, this is the one component every render must get right

The hero is the ONLY thing above the fold, and it is not a per-pane component -
there is exactly **one** hero in the whole document, regardless of how many
dimensions were selected.

- **What goes in it:** the single most severe, most numerically-backed real
  finding across every block in `composition_plan.blocks`. When only one
  dimension is requested, this is trivially that dimension's own worst
  finding. When multiple dimensions are requested, compare their real
  worst-finding assertions and pick the single most material one - never
  average or blend two dimensions into one invented sentence.
- **No donut, no score.** `dimension_selection` never computes a composite
  score - `fixed_holistic` is the only report type that shows one.
- **Metachips** - one real number per requested dimension when more than one
  is selected, so the hero previews every pane below it without repeating any
  pane's full finding.

## Title rule

`<title>` follows RegTrack's real naming, never the word "report":
- One dimension selected: `"{Dimension} insights · Tenant {N}"` (e.g.
  `"Nature insights · Tenant 29"`).
- More than one dimension selected: join the real block names with `" & "`
  when there are two or three (e.g. `"Nature & Entity insights · Tenant 29"`);
  for four or more, use `"{count}-dimension insights · Tenant {N}"` - never
  list more than three names in the title itself.

## CSS — declare every class you actually used above, once, in the `<style>` block

Adapted directly from Trent's real `trent-shared.css`, verbatim where the
component is reused (only add a rule for a class you introduced that is not
listed here, which should be rare).

```css
.topbar{position:sticky;top:0;z-index:30;background:color-mix(in oklch,var(--bg-elev) 92%,transparent);backdrop-filter:saturate(140%) blur(10px);border-bottom:1px solid var(--border)}
.topbar-inner{max-width:1140px;margin:0 auto;padding:11px 24px;display:flex;align-items:center;gap:12px}
.crumbs{display:flex;align-items:center;gap:8px;font-size:13px;color:var(--fg-muted);min-width:0}
.crumbs .here{color:var(--fg);font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.spacer{flex:1}
.chip{font-size:12px;color:var(--fg-muted);background:var(--bg-elev);border:1px solid var(--border);padding:4px 11px;border-radius:999px}
.chip b{color:var(--fg);font-weight:600}
.chip.asof{background:var(--warn-soft);border-color:var(--warn-border);color:oklch(0.42 0.10 70)}

.hero .eyebrow{font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:var(--fg-faint);font-weight:600}
.hero h1{font-size:27px;font-weight:600;letter-spacing:-0.02em;line-height:1.2;margin:8px 0 8px;max-width:60ch;text-wrap:balance}
.hero .lede{font-size:15.5px;color:var(--fg-muted);max-width:78ch;margin:0 0 12px}
.metachips{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}

.section-head{display:flex;align-items:baseline;gap:12px;margin:34px 0 12px;flex-wrap:wrap}
.section-head h2{font-size:18px;font-weight:600;letter-spacing:-0.01em;margin:0}
.section-head .hint{font-size:12.5px;color:var(--fg-faint)}
.card{background:var(--bg-elev);border:1px solid var(--border);border-radius:var(--r);box-shadow:var(--shadow-sm);padding:18px 20px}
.card+.card{margin-top:12px}

.meter{height:8px;border-radius:999px;background:var(--bg-sunk);overflow:hidden}
.meter i{display:block;height:100%;border-radius:999px;background:var(--warn)}
.meter i.ok{background:var(--ok)}.meter i.bad{background:var(--bad)}.meter i.neutral{background:var(--neutral)}

.tone-tag{display:inline-flex;align-items:center;gap:6px;font-size:11px;font-weight:600;letter-spacing:0.04em;text-transform:uppercase;padding:3px 10px;border-radius:999px}
.tone-tag .dot{width:6px;height:6px;border-radius:999px}
.tone-tag.good{background:var(--ok-soft);color:var(--ok);border:1px solid var(--ok-border)}.tone-tag.good .dot{background:var(--ok)}
.tone-tag.warning{background:var(--warn-soft);color:oklch(0.45 0.12 70);border:1px solid var(--warn-border)}.tone-tag.warning .dot{background:var(--warn)}
.tone-tag.critical{background:var(--bad-soft);color:var(--bad);border:1px solid var(--bad-border)}.tone-tag.critical .dot{background:var(--bad)}

.tbl{width:100%;border-collapse:collapse;font-size:13.5px}
.tbl th{text-align:left;font-size:11px;letter-spacing:0.05em;text-transform:uppercase;color:var(--fg-faint);font-weight:600;padding:8px 10px;border-bottom:1.5px solid var(--border-strong)}
.tbl td{padding:9px 10px;border-bottom:1px solid var(--border);vertical-align:middle}
.tbl tr:last-child td{border-bottom:0}
.tbl .num{text-align:right;font-family:"IBM Plex Mono",monospace;font-weight:600}
.pct{font-family:"IBM Plex Mono",monospace;font-weight:700}
.pct.bad{color:var(--bad)}.pct.warn{color:oklch(0.5 0.13 70)}.pct.ok{color:var(--ok)}

.callout{border-radius:var(--r);padding:15px 18px;margin-top:12px;font-size:13.5px;line-height:1.55}
.callout b{font-weight:600}
.callout.critical{background:var(--bad-soft);border:1px solid var(--bad-border);color:oklch(0.38 0.13 27)}
.callout.warning{background:var(--warn-soft);border:1px solid var(--warn-border);color:oklch(0.40 0.09 70)}
.callout.good{background:var(--ok-soft);border:1px solid var(--ok-border);color:oklch(0.34 0.10 155)}
.callout .h{font-size:11px;letter-spacing:0.07em;text-transform:uppercase;font-weight:700;margin-bottom:6px;display:block}
.guard{margin-top:26px;background:var(--bg-sunk);border:1px dashed var(--border-strong);border-radius:var(--r);padding:13px 16px;font-size:12.5px;color:var(--fg-muted)}
.guard b{color:var(--fg)}

.statebar{display:grid;grid-template-columns:150px 1fr 120px 70px;gap:12px;align-items:center;padding:7px 0;border-bottom:1px solid var(--border)}
.statebar:last-child{border-bottom:0}
.statebar .sn{font-size:13px;font-weight:500}
.statebar .sn small{color:var(--fg-faint);font-weight:400;margin-left:6px}
.statebar .meta{font-size:11.5px;color:var(--fg-muted);text-align:right;font-family:"IBM Plex Mono",monospace}

.narr{font-size:13.5px;color:var(--fg-muted);line-height:1.6;margin:12px 0 0;max-width:88ch}
.narr b{color:var(--fg)}

.qstats{display:flex;gap:8px;flex-wrap:wrap;margin:0 0 10px}
.qstat{font-size:12px;background:var(--bg-sunk);border:1px solid var(--border);border-radius:999px;padding:3px 10px;color:var(--fg-muted)}
.qstat b{font-family:"IBM Plex Mono",monospace;color:var(--fg)}

.act-list{margin-top:10px}
.actx{border:1px solid var(--border);border-radius:var(--r);background:var(--bg-elev);box-shadow:var(--shadow-sm);margin-top:10px;overflow:hidden}
.actx summary{list-style:none;cursor:pointer;display:grid;grid-template-columns:86px 1fr auto;gap:14px;align-items:center;padding:13px 16px}
.actx summary::-webkit-details-marker{display:none}
.actx summary:hover{background:var(--bg-sunk)}
.actx .pchip{font-size:10px;font-weight:700;letter-spacing:0.05em;text-transform:uppercase;text-align:center;padding:4px 8px;border-radius:999px;background:var(--bg-sunk);border:1px solid var(--border);color:var(--fg-muted)}
.actx .pchip.p1,.actx .pchip.p2,.actx .pchip.p3{background:var(--bad-soft);border-color:var(--bad-border);color:var(--bad)}
.actx .pchip.p4,.actx .pchip.p5,.actx .pchip.p6{background:var(--warn-soft);border-color:var(--warn-border);color:oklch(0.45 0.12 70)}
.actx .atitle{font-size:13.5px;font-weight:600;line-height:1.4}
.actx .aowner{font-size:11.5px;color:var(--fg-faint);margin-top:2px}
.actx .chev{color:var(--fg-faint);transition:transform .18s ease;font-size:11px}
.actx[open] .chev{transform:rotate(180deg)}
.actx .abody{border-top:1px solid var(--border);padding:13px 16px;background:var(--bg-sunk)}
.actx .abody .measure{font-size:12.5px;color:var(--fg-muted);margin-top:8px}
.actx .abody .measure b{color:var(--fg)}

.tnum{font-variant-numeric:tabular-nums}

@media(max-width:900px){.page{padding:0 12px 20px}}
```

## Voice

Same restrained, evidence-only voice as every other render prompt in this
repo: state what the data shows, never what it might mean or predict. If a
dimension's narrative block says a metric is "Not available yet" (no
assertion was emitted), render that exactly, inside a plain `.card` with no
tone class — never substitute a guess, a dash standing in for a number, or
silence. Vocabulary is binding: **insights** (never "report"), **Entity**
(never "Organisation"), exact role names where they appear (**Performer,
Reviewer, Compliance Officer, Compliance Owner**). Display numbers carry
`class="tnum"` and thousands separators (`<b class="tnum">20,180</b>`);
identifiers (branch ids, request ids) never do.

## Multiple dimensions, one document

When more than one block is requested, panes appear in the SAME order as
`composition_plan.blocks` (the caller's own requested order - not
alphabetical, not by perceived importance). Nothing here compares dimensions
against each other unless a real assertion already computed that comparison -
do not editorialise about which selected dimension "matters most," beyond the
one comparison the hero itself makes when choosing its single headline
finding.

## Self-check before returning

Reject your own draft and fix it if it: introduces a colour not in the token
block above · omits the hero · shows a donut or composite score (this
template never has one) · right-aligns anything except real numeric table
cells (`.tbl .num`, which IS right-aligned per Trent's own real CSS - do not
confuse this with the old `.di-` template's left-alignment rule, which no
longer applies) · fabricates a standout/anomaly finding with no real detector
backing it · loads a font from a CDN or declares `@font-face` itself · skips
the mandatory comparison table/bars when `dimension_rows[block]` has real
rows · references `trent-shared.css` or any external file · or leaves a pane
at "one number and one sentence" when the real data supports more.

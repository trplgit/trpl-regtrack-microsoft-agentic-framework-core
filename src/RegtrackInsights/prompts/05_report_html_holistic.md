# Report Generation (Way 1 — LLM-authored HTML) — HOLISTIC VARIANT, EXPERIMENTAL

**COPY of `05_report_html.md`** (removed 2026-09-01 - "compliance_health"'s dynamic/
no-fixed-tabs render prompt; this file's own Output constraints/Security/Accessibility
sections are already a full standalone copy, not a reference, so nothing here broke)
for visual-style experimentation only (per-tenant
report STRUCTURE stays dynamic, decided by the composition agent — see
`01_composition.md` rules 1/3/4. This file adds LOOK, never fixed layout).
Not wired into the real orchestrator; used only by the render-only lab loop
(`ModelComparisonLabTests.RenderAndReviewAsync` style, reusing a frozen
snapshot) so this can be iterated without re-running gather/compose/narrate.

**Runs:** after the narrative passes reflection.
**Reads:** `prompts/README.md`.

> **MVP only.** Phase 2 replaces this with Way 2 — you emit a typed view-model and
> a trusted Angular renderer produces the markup. Design accordingly: keep
> structure clean and semantic so the migration is mechanical. (Spec §8.5)

---

## Your job

Produce a single self-contained HTML document rendering the approved composition
and prose. The layout should suit *this* report's content — you are composing, not
filling a template. **The set of blocks and their order come from the composition
plan you are given, per-tenant — never force a fixed set of sections.** What
follows is visual vocabulary to reuse WHEN the content calls for it, not a
checklist to fill in regardless of what the data actually contains.

## Output constraints — every one is enforced downstream

A deterministic **Report Emit Normalizer** runs after you and will **reject** the
document if any of these fail. Failure sends the report to the refusal path, so
the user gets nothing.

1. **Exactly one HTML document.** `<!DOCTYPE html>` … `</html>`. Do not append a
   second copy, a design export, or an escaped duplicate.
2. **All CSS inline** in `<style>` blocks. No `<link rel="stylesheet">`, no
   `@import`.
3. **All JS inline** in `<script>` blocks. No `<script src>`.
4. **Zero external references.** No CDN, no Google Fonts, no remote images, no
   `preconnect`/`prefetch`. Every `src`, `href`, and `url()` must be self-origin
   or a `data:` URI.
5. **No network calls at runtime.** No `fetch`, no `XMLHttpRequest`, no
   `WebSocket`, no form posts. CSP sets `connect-src 'none'` — such code cannot
   run and its presence is a rejection.
6. **Charts as inline `<svg>`.** No charting library. Hand-author the SVG.
7. **Font:** `font-family: 'Poppins', sans-serif` — set it on `body` **and**
   explicitly on every SVG `<text>` element's `font-family` attribute (SVG text
   does not inherit page CSS). Do **not** declare `@font-face` yourself — a real,
   self-hosted Poppins face is embedded automatically after you render, by a
   deterministic step (`Insights.Presentation.PoppinsFontInjector`) using real
   vendored font bytes. You cannot produce real font binary data yourself (a
   syntactically-valid `@font-face` you author will always be functionally
   empty), so just write the name and let the injector supply the bytes.

## Security

Your output renders inside a sandboxed iframe with no same-origin access and a
strict CSP. That containment is the boundary — **but write as if it were not
there.**

Tenant data (site names, user names, department names) is **user-entered across
600 tenants** and must be treated as untrusted. Escape every interpolated value:
`&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `"` → `&quot;`, `'` → `&#39;`.

Never place tenant data inside a `<script>` block, an inline event handler
(`onclick=`), a `style` attribute, or an `href`/`src`.

## Design

Clean, dense, professional — a compliance document, not a marketing page.

- Neutral palette; colour only to signal severity (red/amber/green), never decoration
- Generous whitespace, clear hierarchy, readable at a glance
- Tables for detail, SVG for distributions, callouts for the hero finding
- Print-friendly: sensible page breaks, no fixed positioning
- No animation, no hover-dependent content, no interactivity that hides data

[CORRECTED - the first version of this file pointed at tokens.css's --c-green/
--c-amber/--c-red, which is a DIFFERENT, non-approved palette. A live reviewer
run rejected output built on it.] Use these exact three hex families for tone
everywhere colour carries meaning, matching the score-components section below
- reuse the SAME family across the whole document, never a fourth colour and
never a hand-picked hex:
- **ok** (closed/on-time/healthy): `#2e9e5b` for bars/icons/strokes; for a
  filled pill/chip use `background:#e7f5ec; color:#1e8a4a; border:1px solid #a8d3b8`.
- **warn** (due/attention/under-configured): `#e0a106` for bars/icons/strokes;
  pill `background:#fcf0de; color:#b45708; border:1px solid #e8c79c`.
- **bad** (overdue/breach/dark): `#d94a3d` for bars/icons/strokes; pill
  `background:#fceae8; color:#b3261e; border:1px solid #dfa39d`.

A tone never moonlights — green always means the good state for that metric,
never decoration.

## Structure

- Header: report type, tenant, period, **"Generated {timestamp} — reflects live data"**
- Hero block, prominent
- Supporting blocks in composition order
- Data-quality caveats visible where relevant, not footnoted away
- Footer: dictionary version, provenance

### Score hero + weighted components — REUSE VERBATIM when a composite score exists

If (and only if) the narrative contains a `composite_score` block, this is NOT
a section you compose freely - it is a fixed, designer-approved component.
Reproduce this exact markup and these exact classes/colours (source: the real
product's own `detailed-insights.component.html`/`.css` - not a guess, not the
`tokens.css` palette used elsewhere in this file, which does NOT match):

**[BUG FOUND LIVE - do not repeat this] The `{composite score, integer}` and
`{band}` placeholders below MUST be copied verbatim from the `composite_score`
block's own prose (which states them because it cites `A-SCORE-composite` -
see `03_narrative.md`).** A prior run had no such assertion to cite, so a
render step INVENTED a composite number ("24/100") against a real value of
40 - confirmed live, not a hypothetical. If the `composite_score` block's
prose does not contain an actual number and band for the OVERALL score
(only a list of per-component values), that is a data gap upstream - render
the components you do have and OMIT the donut/number/band entirely rather
than invent one. The same rule applies to every other number in this
document (README's D7 contract), stated here again because this is exactly
where it broke.

```html
<section class="di-hero">
  <div class="di-hero__inner">
    <div class="di-scoreblock">
      <div class="di-donut di-donut--{tone}" aria-hidden="true">
        <svg viewBox="0 0 120 120" style="transform:rotate(-90deg)">
          <circle cx="60" cy="60" r="52" fill="none" stroke-width="12" stroke="#e3e9f5"/>
          <circle cx="60" cy="60" r="52" fill="none" stroke-width="12" stroke-linecap="round"
                  stroke="{tone colour, see table below}"
                  stroke-dasharray="{score/100 * 326.7} 326.7"/>
        </svg>
        <div class="di-donut__ctr" style="position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center">
          <div class="di-donut__num tnum">{composite score, integer}</div>
          <div class="di-donut__of">/ 100</div>
        </div>
      </div>
      <div class="di-id">
        <div class="di-eyebrow">{report eyebrow, e.g. tenant/report type}</div>
        <h1 class="di-title">{tenant name}</h1>
        <div class="di-sub">{tenant sub-line, e.g. shape + period}</div>
      </div>
    </div>
    <div class="di-verdict">
      <div class="di-verdict__row">
        <span class="di-band di-band--{tone}"><span class="di-band__dot"></span>{band}</span>
      </div>
      <p class="di-headline">{one-sentence hero takeaway, from the hero block's own prose}</p>
      <p class="di-method">PROVISIONAL - not yet reviewed with the business. {OverallHealth.Method verbatim}</p>
    </div>
  </div>
  <div class="di-components__wrap">
    <div class="di-components__legend" style="display:flex;gap:12px;justify-content:flex-end;font-size:var(--fs-di-comp-meta);color:var(--c-text-2);margin-bottom:6px" aria-hidden="true">
      <span><i style="display:inline-block;width:8px;height:8px;border-radius:50%;background:#2e9e5b;margin-right:4px"></i>&ge; 70</span>
      <span><i style="display:inline-block;width:8px;height:8px;border-radius:50%;background:#e0a106;margin-right:4px"></i>40 &ndash; 69</span>
      <span><i style="display:inline-block;width:8px;height:8px;border-radius:50%;background:#d94a3d;margin-right:4px"></i>&lt; 40</span>
    </div>
    <div class="di-components" aria-label="Composite score components">
      <!-- [FIX - matches the real product's detailed-insights.component.ts SCORE_COMPONENTS,
           which always renders all seven, and reuses the SAME di-component/di-component__name/
           di-component__value/di-meter classes already used elsewhere in this document - do not
           invent a parallel di-comp naming scheme.] ALWAYS seven cards, in this exact order,
           with these exact labels - never fewer, never reordered:
           1. Timeliness            (weight 0.15)
           2. Coverage              (weight 0.15)
           3. Overdue / Backlog     (weight 0.10)
           4. Risk-weighted         (weight 0.20)
           5. Licence               (weight 0.15)
           6. People                (weight 0.15)
           7. Evidence              (weight 0.10)
           A card is REAL only when its matching A-SCORE-{domain_kpi} assertion is present
           (domain_kpi: timeliness/coverage/overdue_backlog/risk_weighted/licence/
           people_continuity/evidence_integrity) - use its real Value. Evidence has NO real
           source and will never have this assertion; Licence commonly won't either on a tenant
           whose Licence dimension failed to compute. For any card with no matching assertion,
           render the SAME di-component shell muted instead of a value - do not omit the card and
           do not invent a number for it: -->
      <article class="di-component">
        <p class="di-component__name">{component label}</p>
        <p class="di-component__value tnum">{value}</p>
        <div class="di-meter"><span class="{ok-fill|warn-fill|bad-fill per the >=70/40-69/<40 tone rule}" style="width:{value}%"></span></div>
      </article>
      <!-- muted variant, for a component with no matching assertion -->
      <article class="di-component di-component--muted" style="opacity:.6">
        <p class="di-component__name">{component label}</p>
        <p class="di-component__value tnum" style="color:var(--c-grey);font-size:var(--fs-di-comp-meta)">Not available yet</p>
        <div class="di-meter"><span style="width:0%"></span></div>
      </article>
    </div>
  </div>
</section>
```

**[BUG FOUND LIVE - do not repeat this]** A real render shipped only 4 of the 7 cards
(Risk-weighted, Coverage, Overdue/Backlog, People) - Timeliness, Licence and Evidence
were silently dropped instead of rendered muted, despite the instruction above already
saying "never fewer, never omit the card." Same failure class `ComputeScoreActivity.cs`
already documents for the composite number itself: an instruction to always include
something is not self-enforcing. **Before you output this section, count your
`<article class="di-component...">` elements. If the count is not exactly 7, you have
this bug right now - go back and add a muted card for every one of the 7 labels above
that is missing, in the fixed order given.** Real vs muted is decided ONLY by whether
`A-SCORE-{domain_kpi}` is present for that label - never by how confident the number
looks, never by leaving a card out because its value is unflattering.

Tone table (per-component AND per composite band - use the RIGHT one for each):
- Component tone (from its own 0-100 value): `>= 70` -> `ok`, `40-69` -> `warn`, `< 40` -> `bad`.
- Composite/band tone (from the band string, not the raw score): `Strong`/`Stable` -> `ok`, `Needs Attention` -> `warn`, `At Risk`/`Critical` -> `bad`.
- Exact colours by tone - use these hex values verbatim, they are already brand-approved (do NOT substitute `tokens.css`'s `--c-green`/`--c-amber`/`--c-red`, which are a DIFFERENT, non-matching palette):
  - `ok`: donut/bar `#2e9e5b`; band pill `background:#e7f5ec; color:#1e8a4a; border:1px solid #a8d3b8`
  - `warn`: donut/bar `#e0a106`; band pill `background:#fcf0de; color:#b45708; border:1px solid #e8c79c`
  - `bad`: donut/bar `#d94a3d`; band pill `background:#fceae8; color:#b3261e; border:1px solid #dfa39d`

The PROVISIONAL caveat MUST be visible in `.di-method`, not buried in a footnote
- it travels with the number, same rule as every other caveat in this report.

### Multi-block navigation — REUSE VERBATIM

When the composition plan produced enough distinct blocks that a reader
benefits from jumping between them (roughly 4+), use this exact tab-nav
markup/classes (source: the same real component file), one tab per actual
block the plan chose - **name each tab from that block's own label, never
from a fixed list**:

```html
<div class="di-stickytabs">
  <nav class="di-tabnav" role="tablist" aria-label="Insight sections">
    <button type="button" class="di-tab di-tab--active" role="tab" aria-selected="true">{first block's own label}</button>
    <button type="button" class="di-tab" role="tab" aria-selected="false">{second block's own label}</button>
    <!-- one per block actually present, in composition order. Add <span class="di-tab__count tnum">{n}</span>
         inside a tab only if that block has a natural count to show (e.g. finding count) - omit otherwise. -->
  </nav>
</div>
```

### KPI tiles / snapshot numbers — REUSE

For a headline metric tile (a big number plus context), reuse this shape:

```html
<div class="di-snapgrid" style="display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px">
  <article class="di-snaptile di-snaptile--{tone}">
    <div class="di-snaptile__label">{what the number measures}</div>
    <div class="di-snaptile__num tnum">{value}</div>
    <div class="di-snaptile__desc">{comparator, e.g. "8.2 points below tenant average" - only if an assertion supports it}</div>
  </article>
</div>
```
`tnum` = `font-variant-numeric: tabular-nums`, already required by the base design
system for every number.

### Store/branch coverage grid — when a coverage block includes per-store detail

One box per store, coloured by its single `primary_status`, resolved by
PRECEDENCE (a store can match more than one condition; the first match in this
order wins — never blend or average a color): `dark` > `unmapped_node` >
`under_configured` > `has_ownerless` > `healthy`. Tone mapping: `dark` and
`unmapped_node` → `bad` (#d94a3d family), `under_configured` and
`has_ownerless` → `warn` (#e0a106 family), `healthy` → `ok` (#2e9e5b family) -
same three hex families as the score components above, for visual consistency
across the whole report. Render only the statuses actually present in the
data — do not fabricate stores or force all five categories to appear.

### Store/branch coverage grid — when a coverage block includes per-store detail

One box per store, coloured by its single `primary_status`, resolved by
PRECEDENCE (a store can match more than one condition; the first match in this
order wins — never blend or average a color): `dark` > `unmapped_node` >
`under_configured` > `has_ownerless` > `healthy`. Tone mapping: `dark` and
`unmapped_node` → red family, `under_configured` and `has_ownerless` → amber
family, `healthy` → green family. Render only the statuses actually present in
the data — do not fabricate stores or force all five categories to appear.

## Accessibility

Semantic HTML (`<table>`, `<th>`, `<caption>`, headings in order). SVG charts need
`<title>` and `role="img"`. Do not encode meaning in colour alone — pair it with a
label.

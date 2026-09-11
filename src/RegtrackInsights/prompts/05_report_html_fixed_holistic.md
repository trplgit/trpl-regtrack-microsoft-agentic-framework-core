# Report Generation — FIXED HOLISTIC TEMPLATE, EXPERIMENTAL

**Reproduces the real, live, designer-approved "Holistic Insights" product page**
(`detailed-insights.component.html`/`.css` — confirmed against the actual deployed
demo, not the illustrative sample data). Unlike `05_report_html_holistic.md`,
this is NOT dynamic-per-tenant — the composition plan for this report type is
built deterministically in C# (`FixedHolisticComposition`), always these exact
six tabs in this exact order. **Your job here is template-fill, not composition
— never omit a tab, never reorder them, never invent a seventh.**

## Output constraints — every one is enforced downstream

**[FIX - inlined 2026-09-01]** This section used to say "read `05_report_html.md`'s
copy of these rules" - that file was the original MVP/dynamic render prompt
(`compliance_health` report type, no fixed tabs, composition-agent-decided
structure). It has been removed: this file is now the ONLY render prompt wired
into production (`PaidReportAgentsRegistration.cs`) - there is no second copy
left to drift out of sync with, so the rules are written out here directly
instead of pointed at a file that no longer exists.

A deterministic **Report Emit Normalizer** runs after you and will **reject**
the document if any of these fail. Failure sends the report to the refusal
path, so the user gets nothing.

1. **Exactly one HTML document.** `<!DOCTYPE html>` … `</html>`. Do not append a
   second copy, a design export, or an escaped duplicate. **The very first element
   inside `<head>` must be `<meta charset="utf-8">`.** [BUG FOUND LIVE, 2026-09-02]
   A real render omitted this entirely - every real UTF-8 character this document
   uses (`·`, `—`, `→`, `&middot;`) mojibaked (`Â·`, `â€"`) the moment it was served
   without an HTTP `charset` header to fall back on. The blob-storage layer may set
   one, may not - this tag is the belt-and-suspenders fix that makes the document
   correct on its own, matching "write as if the sandbox were not there" (Security,
   below) applied to encoding, not just script.
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

## Accessibility

Semantic HTML (`<table>`, `<th>`, `<caption>`, headings in order). SVG charts need
`<title>` and `role="img"`. Do not encode meaning in colour alone — pair it with a
label.

## Binding brand spec — `AI-INSIGHTS-BRAND-HANDOFF.md` (2026-09-10)

Layout structure (which blocks, in what order, at what density) is yours to decide;
visual identity (every hex, gradient, font, radius, shadow, chip shape) is fixed by
that file's §2-§3 tokens, verbatim - never re-derived. Its §4 component vocabulary,
§5 voice rules, §6 settled generation rules, and §7 acceptance check all apply here.
**The reference implementation is now `reference/holistic-insights-tenant1300.html`**
(the tenant-1300 run after the 2026-09-10 designer polish pass - it supersedes every
earlier reference). When a rule and instinct disagree, match THAT file. Three
confirmed exceptions below (checked against this project's own architecture, not
guessed):

0. **NEVER render a "not available", "not scored", muted, ghost, or blocked tile /
   card / section.** [TENANT RULE 2026-09-10, overrides the reference itself.] The
   reference file still shows two muted tiles ("Not available yet" RISK, "Not scored
   this run" Evidence component) - **do not copy that.** When a tile, card, KPI,
   score component, or whole section has no real backing assertion this run, **omit
   it entirely** - no placeholder, no muted class, no "not available" copy, no
   `di-blocked-note`. The user must never see that a piece failed; it simply is not
   there. A deterministic gate (`FixedHolisticStructureGate`) refuses the whole
   document if any `di-snaptile--muted`, `di-comp--muted`, `di-blocked-note`, or the
   literal "Not available yet" / "Not scored this run" text survives.

1. **Font stays self-hosted - do NOT emit the handoff's §6 Google Fonts `<link>`.**
   §1 of the same handoff says "self-hosted, never the Google Fonts CDN link"; §6
   says to emit exactly that link - the handoff contradicts itself. This document
   renders in a sandboxed iframe (`connect-src 'none'`, zero external references
   allowed - this file's own Output constraints rule 4/5, above) - a CDN `<link>` would be refused
   outright by `ReportEmitNormalizer.CheckNoExternalReferences` before it ever
   reached a browser. Keep this project's existing approach: write the CSS name
   "Poppins" only, never declare `@font-face` yourself - the real, self-hosted font
   bytes are embedded automatically after you render, by
   `Insights.Presentation.PoppinsFontInjector`. The handoff's §6 "embed every font
   weight the CSS declares" rule is already satisfied by that injector, not by you.
2. **The reference file's coverage-map `<script>` fabricates numbers - this is why you never
   write that script yourself.** Confirmed by reading it: on tile click it runs
   `hash(branchName)` and derives `mapped`/`overdue`/`peer gap` etc. from that hash -
   fabricated, not real data, the same class of bug this whole prompt exists to
   prevent (CLAUDE.md non-negotiable #2). [CHANGED LIVE, 2026-09-02] The interaction PATTERN
   (tiles as `<button>`, click fills a sticky detail card, chips filter the grid) is real and
   worth keeping - but the tiles themselves, and the script that drives them, are both generated
   deterministically now (CoverageGridInjector / CoverageScriptInjector), reading real `data-*`
   attributes, never a hash. Tab 3 below tells you exactly what that leaves for you to write.

## The one rule that matters most here

**Every number you write MUST come from the `assertions` array you are given —
never from `narrative` prose, never invented.** This report is KPI-card-heavy
(far more discrete numbers per screen than a prose-driven report), which is
exactly why `assertions` is now part of your input alongside `plan`/`narrative`
— use it as the source for every tile/card number; use `narrative`'s prose only
for the "Interpretation"/headline sentences. **If a tile's assertion is not
present in the array, that tile has no real data yet — OMIT THE TILE ENTIRELY
(exception 0 above), never a plausible-looking number and never a "not
available yet" placeholder.** This project's whole architecture exists to
prevent exactly the mistake of a confident-looking number nobody actually
verified — treat that as absolute here.

## Hero + score components

**[REBUILT LIVE, 2026-09-02]** This section used to say "identical to
`05_report_html_holistic.md`'s hero section" - that file's own hero was never
the real one either (an older `di-scorecard`/`di-scoremini` shape, not the
real product's donut gauge + colour-coded component bars). A real render
confirmed the gap: plain white cards, thin uncoloured bars, no ring chart -
compared side by side against the actual live product, "the theme was gone".
Real markup below, copied from `detailed-insights.component.html`/`.css`
(lines 69-133 / 350-657) verbatim, not reinterpreted - same treatment Tab 1-6
already got, just applied to the one section that was still a pointer to a
file that never had the real design.

Add these tokens to your `:root` block, **verbatim** (missing ones only - keep
whatever you already declared for `--c-*`/`--r-*`/`--gap-*`/`--fs-*`, these are
additive). [TOKEN VALUES ARE FIXED, 2026-09-10 - the reference re-drifted here;
these are the §2 values, not a starting point to tune]:
`--r-xl:11px;` `--di-donut:92px;` `--di-hero-pad-y:16px;` `--di-hero-pad-x:16px;`
`--fs-di-score:1.875rem;` `--fs-di-of:.66rem;` `--fs-di-title:.95rem;`
`--fs-di-sub:.88rem;` `--fs-di-method:.72rem;` `--fs-di-comp-name:.66rem;`
`--fs-di-comp-num:.94rem;`
Also confirm the base tokens match §2 exactly: ink `--c-text:#3d3d3d`, borders
`--c-border:#dbdbdb`, `--c-grey:#999999`, radii `--r-sm:3.5px`/`--r-md:5.5px`/
`--r-lg:9px`, page ground `--c-bg:#f9fafb`. Never redefine a §2 token to a
different value.

**Composite score → tone**, same 3-band legend the component grid itself
shows (`≥70` ok / `40-69` warn / `<40` bad) - compute once from
`A-SCORE-composite`'s real `value`, reuse for the donut ring AND the verdict
band pill, never picked independently for each:
`ok` if score ≥ 70, `warn` if 40 ≤ score ≤ 69, `bad` if score < 40.

The document opens with a **topline**, not a masthead - one quiet line above the
hero, `<h1>`/banner/kicker all forbidden (§6, Output constraint 1). Left: brand-blue
600 `Holistic insights · Tenant {N}`. Right: `Generated {YYYY-MM-DD HH:MM} UTC` -
**formatted from `generatedAt`, never the raw ISO `T…Z` stamp**, and nothing else
(no "reflects live data" - these are background-job snapshots).

```html
<div class="topline">
  <span class="topline__t">Holistic insights &middot; Tenant {real tenant id}</span>
  <span class="topline__meta">Generated <span class="tnum">{real generatedAt, formatted "YYYY-MM-DD HH:MM"} UTC</span></span>
</div>
<section class="di-hero">
  <div class="di-hero__inner">
    <div class="di-scoreblock">
      <div class="di-donut di-donut--{tone}" aria-hidden="true">
        <svg viewBox="0 0 120 120">
          <circle class="di-donut__track" cx="60" cy="60" r="52" fill="none" stroke-width="12"></circle>
          <!-- stroke-dasharray = "{real score/100 * 326.7} 326.7" - 326.7 is the real
               circumference of r=52 (2*pi*52), computed once, never a guessed constant -->
          <circle class="di-donut__arc" cx="60" cy="60" r="52" fill="none" stroke-width="12"
                  stroke-linecap="round" style="stroke-dasharray:{real score/100 * 326.7} 326.7"></circle>
        </svg>
        <div class="di-donut__ctr">
          <div class="di-donut__num tnum">{real A-SCORE-composite value, e.g. 52}</div>
          <div class="di-donut__of">/ 100</div>
        </div>
      </div>
      <div class="di-id">
        <div class="di-eyebrow">Composite</div>
        <h1 class="di-title">Compliance-health score</h1>
        <div class="di-sub">Tenant {real tenant id}</div>
        <!-- NO di-meta / "Generated" chip here - the topline above owns the timestamp. -->
      </div>
    </div>
    <div class="di-verdict">
      <div class="di-verdict__row">
        <!-- Band/Trend come from A-SCORE-composite's own caveat text
             ("Band: X. Trend: Y. PROVISIONAL..."), never invented or reworded -->
        <span class="di-band di-band--{tone}"><span class="di-band__dot"></span>{real Band text}</span>
        <!-- Trend glyph + colour follow DIRECTION (from the real Trend text):
             up   -> class di-trend--up,   rising-arrow glyph  <path d="M1 9l4-4 2 2 4-4M11 3v3H8">
             flat -> class di-trend--flat,  steady-arrow glyph <path d="M1 6h10M8 3l3 3-3 3">
             down -> class di-trend--down,  steady-arrow glyph <path d="M1 6h10M8 3l3 3-3 3">
             Never a green arrow on a flat or down trend. -->
        <span class="di-trend di-trend--{up|flat|down}">
          <svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="{glyph per direction, above}"></path></svg>
          Trend: {real Trend text}
        </span>
      </div>
      <!-- HERO VERDICT IS TWO LINES (§6, production run):
           1. di-hero__title  - a 600 headline STATING THE CLAIM in plain words. It must NOT
              repeat the score number or the band word the donut + pill already show.
           2. di-headline     - a 400 sentence whose key FIGURES only are wrapped in
              <em class="tnum"> (the em is 600, band-text colour - warn #b45708 / bad #b3261e).
              The rest of the sentence is NOT coloured. Never paint the whole sentence.
           No third "method" line - Band/Trend are already shown by the donut, pill and trend
           glyph; a caveat about review status does not belong on the customer-facing hero. -->
      <h2 class="di-hero__title">{real 600 claim headline, from narrative prose - e.g. "People continuity and licence lapses are holding the score down"}</h2>
      <p class="di-headline">{one real sentence, from narrative prose only, key figures wrapped <em class="tnum">…</em>, citing only numbers in this run's assertions}</p>
    </div>
  </div>

  <div class="di-components">
    <div class="di-components__head">
      <span class="di-components__label">Score components &middot; weighted</span>
      <span class="di-legend">
        <span><i class="di-legend__sw di-legend__sw--ok"></i>&ge; 70</span>
        <span><i class="di-legend__sw di-legend__sw--warn"></i>40 - 69</span>
        <span><i class="di-legend__sw di-legend__sw--bad"></i>&lt; 40</span>
      </span>
    </div>
    <div class="di-components__grid">
      <!-- ONE di-comp card per component that has a REAL A-SCORE-{domain_kpi}
           assertion this run. Render EVERY such assertion you were given - if the
           run scored 7 components, emit 7 cards; if it scored 6, emit 6. Never
           drop a component that has a real score. Between 1 and 7 cards, never
           more. The 7 possible slots, in this order when present: risk_weighted,
           coverage, overdue_backlog, people_continuity, timeliness, licence,
           evidence.
           Name humanised (risk_weighted -> "Risk-weighted", overdue_backlog ->
           "Overdue", people_continuity -> "People", coverage
           -> "Coverage", licence -> "Licence", timeliness -> "Timeliness",
           evidence -> "Evidence"); score = the assertion's real `value`; weight
           = its real `comparator_value`; tone = the SAME 3-band rule as the
           composite ring above.
           [TENANT RULE 2026-09-10] A component with NO assertion this run is
           OMITTED - no card at all. Never a di-comp--muted "Not scored this run"
           ghost, never a zero-width bar, never a "no data" row. The gate refuses
           the document if a di-comp--muted survives. -->
      <div class="di-comp">
        <div class="di-comp__name">{real component name}<small>Weight {real weight}</small></div>
        <div class="di-comp__bar di-comp__bar--{tone}"><i style="width:{real score}%"></i></div>
        <div class="di-comp__row"><b class="tnum">{real score}</b><span>{real score} &times; {real weight}</span></div>
      </div>
    </div>
  </div>
</section>
```

CSS (declare once, copied verbatim from the real component - only the two
custom-property names differ, matching this document's own `--gap-*`/`--r-*`
convention instead of the source file's inline pixel literals where this
document already has an equivalent token):
```css
.di-hero{position:relative;overflow:hidden;border:1px solid transparent;border-radius:var(--r-xl);color:var(--c-text);background:linear-gradient(115deg,#eef1fe 0%,#f7f0fb 38%,#fdf6fb 62%,#f4f8ff 100%) padding-box,linear-gradient(100deg,#9db4e8 0%,#c3b2ee 30%,#e8b8dd 55%,#a8c8f0 80%,#9db4e8 100%) border-box;background-size:auto,300% 100%;box-shadow:0 2px 10px rgba(18,90,171,.08);animation:di-hero-border-drift 36s linear infinite;--di-fg:var(--c-text);--di-fg-muted:var(--c-text-3);--di-fg-faint:var(--c-grey);--di-line:#e6e6f2;--di-panel:#ffffff;--di-ok:#2e9e5b;--di-warn:#e0a106;--di-bad:#d94a3d}
@keyframes di-hero-border-drift{0%{background-position:0 0,0% 0}50%{background-position:0 0,100% 0}100%{background-position:0 0,0% 0}}
@media (prefers-reduced-motion:reduce){.di-hero{animation:none}}
.topline{display:flex;align-items:baseline;justify-content:space-between;gap:14px;margin-bottom:14px;font-size:var(--fs-di-method)}
.topline__t{font-weight:600;color:var(--c-brand)}
.topline__meta{color:var(--c-text-3)}
.di-hero__inner{position:relative;display:grid;grid-template-columns:fit-content(46%) minmax(0,1fr);gap:2.5rem;align-items:center;padding:var(--di-hero-pad-y) var(--di-hero-pad-x)}
.di-scoreblock{display:grid;grid-template-columns:auto 1fr;gap:var(--gap-lg);align-items:center}
.di-donut{position:relative;width:var(--di-donut);height:var(--di-donut);flex-shrink:0}
.di-donut svg{width:100%;height:100%;transform:rotate(-90deg)}
.di-donut__track{stroke:#e3e9f5}
.di-donut--ok .di-donut__arc{stroke:var(--di-ok)}
.di-donut--warn .di-donut__arc{stroke:var(--di-warn)}
.di-donut--bad .di-donut__arc{stroke:var(--di-bad)}
.di-donut__ctr{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center}
.di-donut__num{font-size:var(--fs-di-score);font-weight:600;letter-spacing:-.03em;line-height:1}
.di-donut__of{font-size:var(--fs-di-of);color:var(--di-fg-muted);margin-top:2px;letter-spacing:.04em}
.di-id{min-width:0}
.di-title{font-size:var(--fs-di-title);font-weight:600;line-height:1.15;margin:6px 0 4px;color:var(--di-fg)}
.di-sub{font-size:var(--fs-di-sub);color:var(--di-fg-muted)}
.di-verdict{min-width:0}
.di-verdict__row{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.di-band{display:inline-flex;align-items:center;gap:8px;padding:4px 12px;border-radius:999px;font-size:var(--fs-di-chip);font-weight:500}
.di-band__dot{width:6px;height:6px;border-radius:999px}
.di-band--warn{background:#fcf0de;color:#b45708;border:1px solid #e8c79c}
.di-band--warn .di-band__dot{background:#b45708}
.di-band--bad{background:#fceae8;color:#b3261e;border:1px solid #dfa39d}
.di-band--bad .di-band__dot{background:#b3261e}
.di-band--ok{background:#e7f5ec;color:#1e8a4a;border:1px solid #a8d3b8}
.di-band--ok .di-band__dot{background:#1e8a4a}
.di-trend{display:inline-flex;align-items:center;gap:6px;color:var(--di-fg-muted);font-size:var(--fs-di-chip)}
.di-trend--up{color:#1e8a4a}
.di-trend--down{color:#b3261e}
.di-trend--flat{color:var(--di-fg-muted)}
.di-trend svg{width:12px;height:12px}
.di-hero__title{margin:10px 0 .35rem;font-size:var(--fs-di-title);font-weight:600;line-height:1.25;letter-spacing:-.02em;color:var(--di-fg)}
.di-headline{font-size:var(--fs-di-headline);line-height:1.45;color:var(--di-fg);margin:0}
.di-headline em{font-style:normal;font-weight:600;color:#b45708}
.di-verdict:has(.di-band--bad) .di-headline em{color:#b3261e}
.di-method{margin-top:12px;font-size:var(--fs-di-method);color:var(--di-fg-muted);line-height:1.55}
.di-components{position:relative;background:rgba(255,255,255,.5);border-top:1px solid var(--di-line);padding:var(--gap-lg) var(--di-hero-pad-x)}
.di-components__head{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:var(--gap-md)}
.di-components__label{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-brand)}
.di-legend{display:flex;gap:14px;font-size:var(--fs-di-chip);color:var(--di-fg-muted)}
.di-legend>span{display:inline-flex;align-items:center;gap:5px}
.di-legend__sw{display:inline-block;width:8px;height:8px;border-radius:2px;flex-shrink:0}
.di-legend__sw--ok{background:var(--di-ok)}
.di-legend__sw--warn{background:var(--di-warn)}
.di-legend__sw--bad{background:var(--di-bad)}
.di-components__grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px}
.di-comp{background:var(--di-panel);border:1px solid var(--di-line);border-radius:var(--r-md);padding:11px 11px 9px;display:flex;flex-direction:column;gap:9px;min-width:0}
.di-comp__name{font-size:var(--fs-di-comp-name);font-weight:500;color:var(--di-fg);letter-spacing:.01em}
.di-comp__name small{display:block;font-weight:400;font-size:var(--fs-di-comp-meta);color:var(--di-fg-faint);margin-top:1px}
.di-comp__bar{position:relative;height:5px;border-radius:999px;background:#eef1f7;overflow:hidden}
.di-comp__bar>i{position:absolute;inset:0 auto 0 0;border-radius:999px;display:block}
.di-comp__bar--ok>i{background:var(--di-ok)}
.di-comp__bar--warn>i{background:var(--di-warn)}
.di-comp__bar--bad>i{background:var(--di-bad)}
.di-comp__row{display:flex;align-items:baseline;justify-content:space-between;font-size:var(--fs-di-comp-meta);color:var(--di-fg-faint)}
.di-comp__row b{color:var(--di-fg);font-weight:600;font-size:var(--fs-di-comp-num)}
.di-components__note{margin:10px 0 0;font-size:var(--fs-di-method);color:var(--di-fg-muted);line-height:1.5}
```
The `--di-ok`/`--di-warn`/`--di-bad`/`--di-fg`/`--di-fg-muted`/`--di-fg-faint`/`--di-line`/`--di-panel`
custom properties are declared LOCALLY on `.di-hero` itself (see the CSS
above) - do not also declare them in `:root`, and do not rename them; every
rule inside this section reads them through that local scope.

If `A-SCORE-composite` is absent (every score component degraded this run),
omit the whole hero section - never render an empty donut or a fabricated
score. This should not happen in practice (ComputeScoreActivity runs before
composition unconditionally) but if it does, treat it the same as any other
missing-assertion case elsewhere in this document.

## Tab navigation — FIXED, not derived from the plan's block names, and REAL CLICK-TO-SWITCH

**[CORRECTED - the reasoning here used to cite the wrong rule]** Inline `<script>`
IS allowed in this document (rule 3: "no `<script src>`", not "no script") - what's
actually barred is an EXTERNAL script and any runtime network call (rule 5: fetch/
XHR/WebSocket/form-posts). Tab-switching below stays CSS-only anyway, not because
JS is forbidden, but because it already works, is already proven (Playwright QA),
and needs no data to drive it - Tab 3's coverage map below DOES use real inline
`<script>`, because per-branch interactivity needs to read real data, which CSS
selectors cannot do. Use the standard CSS-only radio/label technique for the tabs:
a hidden radio input per tab, a `<label>` styled as the tab button, and a `~`
sibling selector to show the matching pane. Six panes, six radios, one `name`
group so only one can be checked. **The radios and panes must be direct siblings
of each other** (general-sibling `~` only matches at the same DOM level) — do not
nest the panes inside `.di-stickytabs` or anywhere the radios cannot reach them.
This already matches the brand handoff's "sticky section tabs are TWO elements"
rule: `.di-stickytabs` is the full-width sticky wrapper, `.di-tabnav` is the
fit-content pill strip it contains - keep that split, do not merge them into one
element or the strip itself becomes sticky and content slides past beside it.

```html
<div class="di-tabsroot">
  <input type="radio" name="di-tab" id="di-tab-1" class="di-tab-input" checked>
  <input type="radio" name="di-tab" id="di-tab-2" class="di-tab-input">
  <input type="radio" name="di-tab" id="di-tab-3" class="di-tab-input">
  <input type="radio" name="di-tab" id="di-tab-4" class="di-tab-input">
  <input type="radio" name="di-tab" id="di-tab-5" class="di-tab-input">
  <input type="radio" name="di-tab" id="di-tab-6" class="di-tab-input">

  <div class="di-stickytabs">
    <nav class="di-tabnav" role="tablist" aria-label="Insight sections">
      <label for="di-tab-1" class="di-tab" role="tab">Snapshot</label>
      <label for="di-tab-2" class="di-tab" role="tab">Risk &amp; licences<span class="di-tab__count tnum">{real card count for pane 2}</span></label>
      <label for="di-tab-3" class="di-tab" role="tab">Coverage<span class="di-tab__count tnum">1</span></label>
      <label for="di-tab-4" class="di-tab" role="tab">Operations<span class="di-tab__count tnum">{real card count for pane 4}</span></label>
      <label for="di-tab-5" class="di-tab" role="tab">Forward look<span class="di-tab__count tnum">{1 if the forward card renders this run, else 0}</span></label>
      <label for="di-tab-6" class="di-tab" role="tab">Actions<span class="di-tab__count tnum">{real number of ranked action cards you rendered}</span></label>
    </nav>
  </div>

  <!-- the six <section class="di-pane" id="di-pane-N"> panes go here, as direct
       children of .di-tabsroot, siblings of the six radios above -->
</div>
```
**[TENANT RULE 2026-09-10] Every badge on tabs 2-6 is the REAL count of cards actually rendered
in that pane this run** - never a fixed slot count, never a fabricated number. Because a card
with no real assertion is OMITTED (not muted), that count varies: Risk & licences 0-3,
Coverage always 1 (the injected coverage pane always has real Location data), Operations 0-3,
Forward look 0 or 1, Actions 0-6. Snapshot (tab 1) has no badge. No pane carries
`data-blocked="true"` any more; `FixedHolisticStructureGate` no longer enforces any fixed badge.
CLAUDE.md non-negotiable #2 is why the count must be real: a live render once shipped "Actions"
badged **6** over an empty pane - a fabricated number one line from its own admission nothing
backed it.

Required CSS (add to the document's `<style>` block, verbatim, alongside the
rest of the brand styling — do not omit any rule or this degrades back to
scroll-only):

```css
.di-tabsroot { margin-top: 22px; }
.di-tab-input { position: absolute; opacity: 0; pointer-events: none; }
.di-tabpane, .di-pane { display: none; }
.di-pane { padding: 18px 0; }
#di-tab-1:checked ~ #di-pane-1,
#di-tab-2:checked ~ #di-pane-2,
#di-tab-3:checked ~ #di-pane-3,
#di-tab-4:checked ~ #di-pane-4,
#di-tab-5:checked ~ #di-pane-5,
#di-tab-6:checked ~ #di-pane-6 { display: block; }
```
**[FIX 2026-09-09] `.di-tab`/`.di-tabnav` were never actually declared anywhere in this
file** — the text used to say "reuses the exact same visual styling `button.di-tab` used
before", a dangling reference to styling that no longer existed in this prompt. Confirmed
live: with no real rule to follow, a render guessed a full 999px pill - the real reference
file's own `.di-tab` is only **7px** radius, barely rounded, not a pill at all. Declare these
verbatim (copied directly from `reference/production-holistic-refined.html`):
```css
.di-tabnav{display:inline-flex;align-items:center;gap:4px;padding:4px;border-radius:var(--r-lg);background:#f3f5f7;border:1px solid var(--c-border);max-width:100%}
.di-tab{cursor:pointer;user-select:none;display:inline-flex;align-items:center;gap:8px;padding:7px 12px;border-radius:7px;font-size:var(--fs-di-chip);color:var(--c-text-3);font-weight:500}
.di-tab:hover{color:var(--c-brand)}
.di-tab__count{font-size:10px;font-weight:600;padding:1px 6px;border-radius:999px;background:var(--c-bg);border:1px solid var(--c-border);color:var(--c-text-3)}
```
The labels sit inside `.di-stickytabs`, not as direct siblings of the radios, so a plain
`~`/`+` sibling selector cannot reach them — use `:has()` instead (this renders in headless
Chromium, which has supported `:has()` since 2022, so it is safe here). Active state is
**background + colour only, same 7px radius, no bigger, no pill** - never let the active
state grow rounder than the resting one:
```css
.di-tabsroot:has(#di-tab-1:checked) label[for="di-tab-1"],
.di-tabsroot:has(#di-tab-2:checked) label[for="di-tab-2"],
.di-tabsroot:has(#di-tab-3:checked) label[for="di-tab-3"],
.di-tabsroot:has(#di-tab-4:checked) label[for="di-tab-4"],
.di-tabsroot:has(#di-tab-5:checked) label[for="di-tab-5"],
.di-tabsroot:has(#di-tab-6:checked) label[for="di-tab-6"] {
  background:#125aab; color:#fff; font-weight:600;
}
.di-tabsroot:has(#di-tab-1:checked) label[for="di-tab-1"] .di-tab__count,
.di-tabsroot:has(#di-tab-2:checked) label[for="di-tab-2"] .di-tab__count,
.di-tabsroot:has(#di-tab-3:checked) label[for="di-tab-3"] .di-tab__count,
.di-tabsroot:has(#di-tab-4:checked) label[for="di-tab-4"] .di-tab__count,
.di-tabsroot:has(#di-tab-5:checked) label[for="di-tab-5"] .di-tab__count,
.di-tabsroot:has(#di-tab-6:checked) label[for="di-tab-6"] .di-tab__count{
  background:rgba(255,255,255,.16); border-color:rgba(255,255,255,.45); color:#fff;
}
```
Every one of the six `<section class="di-pane" ...>` markup blocks below now
needs a matching `id="di-pane-N"` added (N = 1..6, in tab order) — add it to
the `aria-label` element shown in each tab's section below; it is not written
inline in every snippet to avoid repeating the whole block six times.

## Missing data — OMIT, never a placeholder (TENANT RULE 2026-09-10)

**There is no "not available yet" state any more.** When a specific piece has NO
real backing assertion this run:

- **Omit it entirely.** No card, no tile, no muted class, no "Not available yet"
  / "Not scored this run" copy, no `di-blocked-note`, no zero-width bar, no empty
  circle. The grid simply has one fewer child.
- Do **not** add `di-snaptile--muted`, `di-comp--muted`, or `di-blocked-note` -
  the deterministic gate (`FixedHolisticStructureGate`) refuses the whole
  document if any of them, or the literal "Not available yet" / "Not scored this
  run" text, survives to the shipped HTML.

**But "omit" is NOT a judgement call — it is triggered ONLY by an assertion being
genuinely ABSENT from the `assertions` array you were given.** It is not for "this
card looks thin", "I'm not certain the data is meaningful", or "I'd rather show
less". If the assertion IS in the array, you MUST render its tile/card. **When you
are unsure whether a piece has data: it has data — render it.** The concrete
allowed omissions this run are a short, closed list:
- Snapshot tile 1 (Risk · imprisonment overdue) — always omitted, no assertion exists.
- Any other Snapshot tile whose named assertion is absent.
- A score component with no `A-SCORE-{domain_kpi}` assertion (a healthy tenant has all 7).
- Tab 4 Card 3 (Evidence) when `A-REVIEWTRAIL` is absent.
- Tab 4 Card 1 (Timeliness) when `A-CURRENT` is absent.
- An individual `di-kpi__pair`, gauge, or "upgrade" block whose field is absent.
- A whole KPI card ONLY when its entire backing dimension failed/degraded this run
  (it is not in `DimensionResults` at all) — never merely because its numbers seem small.

Everything else renders. A real **zero** is data — render `0` normally. If a whole
tab genuinely has zero cards, render just its `di-pane__head` and set the badge to
`0`, no explanatory sentence. The tab-nav badge for tabs 2-6 is the real count of
cards actually rendered.

## Colour = the state of the number (TENANT RULE 2026-09-10)

Red / amber / green carry **meaning**, never decoration — and the meaning must match
the real value:

- **A tile, KPI tag, verdict pill, or snapshot number in a bad state is RED**
  (`--bad` / `#b3261e`). Amber (`--warn`) for a middling state. Green (`--ok`) ONLY
  when the number is genuinely good. Pick the tone from the assertion's own
  `direction` / `vs_*` / threshold fields — never "green by default".
- **A bar / meter / segment FILL follows the value the same way.** A bar showing a
  number that got WORSE fills **red or amber**, not green. Examples: the Timeliness
  current-period `di-fybar` (green only if on-time % improved); a `di-atable__meter`
  on a high overdue rate (`--bad`); a `di-comp__bar` (`<40` red, `40-69` amber,
  `≥70` green). The only always-green fill is a segment that literally represents
  "the good outcome" (e.g. the Evidence stackbar's "with review trail" segment).
- If a tab, card, or tile represents a **critical** finding, its tag/eyebrow tone is
  `--bad` (red) and stays red — do not soften it to amber or grey.

## Jargon gets an info icon (2026-09-11)

A business reader does not know what "pp" (percentage points), "SPOF", "YoY", or
similar compliance/metrics shorthand means. The FIRST time such a term appears in a
card (headline, narrative sentence, KPI label, or metric chip), follow it
immediately with `<i class="di-info" title="{one short plain-language sentence
explaining the term}">?</i>` — e.g. `9.7pp<i class="di-info" title="Percentage
points - the size of the gap between two percentages, not a percent change.">?</i>`.
One icon per distinct term per card is enough; do not repeat it on every later
occurrence in the same card. Never wrap a plain number or a real business word
(risk, licence, backlog) — only genuine shorthand a non-specialist would have to
look up.

`.di-info{display:inline-flex;align-items:center;justify-content:center;width:13px;height:13px;margin-left:3px;border-radius:50%;background:var(--c-content-mist);border:1px solid var(--c-border);color:var(--c-text-3);font-size:9px;font-weight:700;font-style:normal;line-height:1;cursor:help;vertical-align:middle}`

## Tab 1 — Snapshot (`di-snapgrid`/`di-snaptile`, up to 7 tiles)

```html
<section class="di-pane" id="di-pane-1" aria-label="Snapshot">
  <div class="di-snaphero">
    <p class="di-snaphero__headline">{one-sentence overview, from the narrative's snapshot-equivalent prose if present}</p>
  </div>
  <div class="di-snapgrid">
    <!-- one di-snaptile per tile in the list below that HAS a real assertion this
         run. A tile with no assertion is OMITTED - no muted tile, no "not available"
         (tenant rule). The grid auto-reflows to however many real tiles there are. -->
    <article class="di-snaptile di-snaptile--{tone}">
      <div class="di-snaptile__label">{label}</div>
      <div class="di-snaptile__num tnum">{real value}</div>
      <div class="di-snaptile__desc">{one-line context, from the matching assertion's comparator fields only}</div>
      <!-- [FIX - found live, matches the real product's di-snaptile__jump exactly] Every tile,
           real OR muted, ends with a jump link to the tab that expands on it - the real product's
           own JUMP_LABELS ("See risks & licences →" etc). A real Angular app does this with a
           click handler; this document has no JS for it (nor needs one) - it is the SAME CSS-only
           radio mechanism the tab nav itself already uses. Wrap the jump line in a <label> for the
           target tab's radio input, not a <button> - clicking a <label for="di-tab-N"> checks that
           radio exactly like clicking the real tab does, with zero script. -->
      <label for="di-tab-{N}" class="di-snaptile__jump">
        <span class="di-snaptile__dot"></span>{real JUMP_LABELS text for that tab, e.g. "See risks &amp; licences &rarr;"}
      </label>
    </article>
  </div>
</section>
```
**Jump targets, exact tab numbers** (must match the tab-nav radios above verbatim): Licence·lapses / Risk-weighted content -> `di-tab-2`; Backlog·overdue / Coverage·locations -> `di-tab-3`; People·SPOF / Timeliness / Evidence·review-trail -> `di-tab-4`; Forward·next-90-days -> `di-tab-5`. No tile jumps to `di-tab-6` (Actions) or back to `di-tab-1`. If a jump's target tab renders no cards this run, still link it - the tab still exists.

Add to your `<style>` block (copied from the real product's own `.di-snaptile__jump`/`.di-snaptile__dot`, using this document's token names): `.di-snaptile__jump{display:inline-flex;align-items:center;gap:6px;margin-top:8px;font-size:var(--fs-di-chip);color:var(--c-text-3);cursor:pointer;text-decoration:none}` `.di-snaptile__jump:hover{color:var(--c-brand)}` `.di-snaptile__dot{width:6px;height:6px;border-radius:999px;background:var(--c-grey);display:inline-block}` `.di-snaptile--bad .di-snaptile__dot{background:#b3261e}` `.di-snaptile--warn .di-snaptile__dot{background:#b45708}` `.di-snaptile--ok .di-snaptile__dot{background:#1e8a4a}`.

**[FIX - dangling reference, found live, 2026-09-09]** The above only ever covered the jump link
and its dot - `.di-snaphero`, `.di-snapgrid`, and `.di-snaptile` themselves (the hero band and the
tile shell: background/border/radius/shadow/padding, the top status stripe, the label/number/desc
typography) had no CSS declared anywhere in this file. Declare once (copied verbatim from the real
product's `detailed-insights.component.css`, using this document's token names):
`.di-snaphero{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:var(--gap-lg) calc(var(--gap-lg) + 4px);box-shadow:0 1px 3px rgba(20,28,48,.05)}` `.di-snaphero__headline{font-size:calc(var(--fs-di-snaphead) - 1.5px);line-height:1.5;letter-spacing:-.01em;color:var(--c-text);margin:0}` `.di-snapgrid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:var(--gap-md);margin-top:var(--gap-lg)}` `.di-snaptile{position:relative;overflow:hidden;background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:calc(var(--gap-md) + 3px) var(--gap-md) var(--gap-md);display:flex;flex-direction:column;gap:6px;min-width:0;box-shadow:0 1px 3px rgba(20,28,48,.05)}` `.di-snaptile::before{content:"";position:absolute;left:0;right:0;top:0;height:3px}` `.di-snaptile--bad::before{background:#b3261e}` `.di-snaptile--warn::before{background:#b45708}` `.di-snaptile--ok::before{background:#1e8a4a}` `.di-snaptile__label{font-size:var(--fs-di-snap-label);text-transform:uppercase;letter-spacing:.08em;font-weight:600;color:var(--c-grey)}` `.di-snaptile__num{font-size:var(--fs-di-snap-num);font-weight:600;letter-spacing:-.025em;line-height:1;margin-top:2px;color:var(--c-text)}` `.di-snaptile--bad .di-snaptile__num{color:#b3261e}` `.di-snaptile--warn .di-snaptile__num{color:#b45708}` `.di-snaptile--ok .di-snaptile__num{color:#1e8a4a}` `.di-snaptile__desc{font-size:var(--fs-di-snap-desc);color:var(--c-text-3);line-height:1.45}`
`@media(max-width:900px){.di-snapgrid{grid-template-columns:repeat(2,minmax(0,1fr))}}`

The tile order below; render a tile ONLY when its real assertion is present this run, otherwise
OMIT it (no muted tile, no placeholder). Aim is up to 7 real tiles; a clean run may show fewer.
1. **Risk · imprisonment overdue** — ALWAYS OMITTED. No such assertion exists, by design, not by
   omission: `sql/08_dimension_risk.sql` computes `ImprisonmentOverdue` but deliberately never
   promotes it to a standalone assertion, because imprisonment-bearing instances are ~95-99.6% the
   same population as the Critical risk tier (see that file's own header trap and its narrative
   guard on `A-CRIT`: "Do not also raise imprisonment exposure as a separate finding"). The only
   real imprisonment assertion is `A-IMP-OVERLAP` (imprisonment-on-critical overlap pct), already
   used correctly in Tab 2 Card 1 below - do not reuse it here as if it meant "overdue". Never
   render this tile in any form.
2. **Licence · confirmed lapses** — REAL. Source: `LicenceControlTotals`/Licence dimension row data (lapsed licences).
3. **Backlog · overdue** — REAL. Source: Location dimension's tenant overdue count/pct.
4. **Coverage · locations mapped** — REAL. Source: Location dimension's `BranchesReported` / ghost-entity data.
5. **People · SPOF** — REAL. Source: Users dimension (top-3/reviewer concentration, same data the composite score's `people_continuity` component uses).
6. **Timeliness · on-time closure** — REAL. **[FIX - found live, 2026-09-09]** This used to point
   at `A-TIMELINESS`/`TenantOnTimePct` from the Location dimension - that assertion **has never
   existed** (verified directly against `sql/05_dimension_location.sql`: it never emits an
   assertion by that id, only a bare `TenantOnTimePct` field in control_totals with nothing
   backing it). Because non-negotiable #5 forbids citing an unasserted number, this tile has been
   rendering "not available" on every real run, always - not a data gap, a wrong pointer. Real
   source: the **TimelinessFY** dimension (`sql/23`), `A-CURRENT` assertion (`ontime_pct` metric,
   `value` = real current-FY pct). Same source Tab 4 Card 1's fybars already correctly use - see
   there. If `A-CURRENT` is absent this run (current FY has zero completed events with a known
   Timeliness classification), OMIT this tile.
7. **Evidence · review trail** — REAL, 2026-09-02. Source: EvidenceIntegrity dimension (sql/25),
   `A-REVIEWTRAIL` assertion (`review_trail_pct` metric, tenant scope) — the real pct of closed
   schedules with more than one recorded `ComplianceTransaction` row. This is a workflow-trail
   PROXY, never "evidence was attached" — its `caveat` field says so explicitly and MUST travel
   with the number wherever cited (README.md's own rule). If `A-REVIEWTRAIL` is absent (tenant has
   zero completed/resolved schedules in scope), OMIT this tile.
8. **Forward · next 90 days** — REAL, 2026-09-02. Source: ForwardPipeline dimension (sql/24),
   `DueNext90d` in control_totals — the real count of schedules due in the next 90 days, scoped.
   Always present (the proc always returns a control_totals row, even when the real count is 0 —
   a real zero is not "not available", render it as a real 0, never muted).

## Tab 2 — Risk & licences (`di-kpigrid`/`di-kpi`, 3 cards)

**Render all 3 cards (Risk-weighted, Overdue / Backlog, Licence).** For tenant 1300
- and any tenant whose Risk, BacklogAging and Licence dimensions did not fail - all
three have real data; render all three. Omit a card ONLY if its entire backing
dimension is absent from your inputs (Risk / BacklogAging / Licence not in
`DimensionResults`) - never because the numbers look small or you are unsure. If you
do drop one, re-pick the remaining cards' `di-kpi--span{n}` to fill the 12-column
row (two cards -> span6 + span6, one -> span12). The tab badge = cards rendered.

```html
<section class="di-pane" id="di-pane-2" aria-label="Risk and licences">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">02</span>
    <h2 class="di-pane__title">Key indicators</h2>
  </div>
  <div class="di-kpigrid">
    <!-- di-kpi--span{4|6|8|12} per card - pick a span that sums sensibly across the row -->
    <article class="di-kpi di-kpi--span{n}">
      <div class="di-kpi__head">
        <div class="di-kpi__headtext">
          <div class="di-kpi__eyebrow">{eyebrow}</div>
          <h3 class="di-kpi__title">{title}</h3>
        </div>
        <span class="di-kpi__tag di-kpi__tag--{tone}"><span class="di-kpi__dot"></span>{tag label, e.g. "Action required"}</span>
      </div>
      <!-- ONE of these three body shapes per card, pick whichever fits its real data: -->
      <!-- (a) bigNumber - one dominant figure -->
      <div class="di-kpi__big">
        <div class="di-kpi__num tnum di-kpi__num--{tone}">{value}</div>
        <div class="di-kpi__unit">{unit text}</div>
      </div>
      <!-- (b) pairs - 2-4 side-by-side stats -->
      <div class="di-kpi__pairs">
        <div class="di-kpi__pair di-kpi__pair--{tone}">
          <div class="di-kpi__pair-lbl">{label}</div>
          <div class="di-kpi__pair-val tnum">{value}</div>
          <div class="di-kpi__pair-sub">{sub-text, only from a real comparator field}</div>
        </div>
      </div>
      <!-- (c) ageBar - [CHANGED LIVE, 2026-09-09] you do not author ANY of this bar's markup any
           more, same reasoning as Tab 3's di-pane-3-body below: a segmented bar with real
           proportional widths is exactly the kind of large, mechanical, exact-shape task that is
           not something to gamble on an LLM getting right every time. Confirmed live, twice: this
           whole segment was silently skipped in favour of the bigNumber-only fallback on two
           consecutive real tenant-29 runs, even though real BacklogAging bucket data existed both
           times. Write ONLY the placeholder div - InjectBacklogAgeBarActivity replaces it
           deterministically, post-render, from the real BacklogAgingRow/BacklogAgingControlTotals
           data this orchestrator already has. Total count goes ABOVE the placeholder (in
           di-kpi__big, same slot the bigNumber shape uses), not inside it - you still author that
           part, it is real and simple. -->
      <div class="di-kpi__big">
        <div class="di-kpi__num tnum di-kpi__num--{tone}">{real total overdue count}</div>
        <div class="di-kpi__unit">overdue obligations</div>
      </div>
      <div id="di-agebar-root"></div>
      <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, still citing only real numbers}</p>
    </article>
  </div>
</section>
```
**The `<div id="di-agebar-root"></div>` placeholder is ALWAYS emitted in this card - it is a
mandatory handoff point, not "omit if no data".** `InjectBacklogAgeBarActivity` either fills it
with the real segmented bar + legend, or (if `BacklogAgingControlTotals.SumOfRows` is 0 or
BacklogAging degraded) clears it to nothing. Either way you emit the empty div. Never skip Card 2
and never skip its placeholder. Do not write `di-agebar`, `di-agebar__scale`, `di-agebar__seg`,
`di-stacklegend`, or `di-agebar__axis` markup of your own anywhere in this card - it will be
discarded (`InjectBacklogAgeBarActivity` replaces the ENTIRE contents of `#di-agebar-root`).

Card 1 — **Risk-weighted** (pairs shape): critical-overdue count, critical-share-of-overdue-pct,
imprisonment-critical-overlap-pct — all from the Risk dimension's critical-tier row/assertions.
Card 2 — **Overdue / Backlog**: total count (`di-kpi__big`) is the real `SumOfRows` from
BacklogAgingControlTotals - the same population this card's bigNumber has always shown. If the
`older_bucket_dominates` detector fired aggregate (an `A-OLDER-AGG` assertion is present), cite
its real headline in this card's narrative line - real evidence the backlog is structurally old,
not just behind this year. Overdue is a flow metric (the underlying data carries this caveat) -
never compare the bar's shape (which you do not author any more, see above) against a different
run. If `BacklogAgingControlTotals.SumOfRows` is 0 (the `zero_overdue`
data_quality note is present), the narrative should still state a real 0 - the placeholder
itself is handled entirely by `InjectBacklogAgeBarActivity` in either case.
Card 3 — **Licence** (pairs shape): corroborated lapses, locations affected, avg days overdue,
expiring-in-90d — from `LicenceControlTotals` and Licence dimension rows. If `expiring_90d` has no
real assertion, omit that one pair rather than invent it - 3 real pairs beats 4 with one fake.

**[FIX - dangling reference, found live, 2026-09-09]** This document has used `.di-kpi`/
`.di-kpigrid`/`.di-kpi--span{n}` markup since Tab 2 above, and reuses it again in Tab 4 and Tab 5
below - but the CARD SHELL itself (background/border/padding/shadow, the 12-col span grid, the
eyebrow/title pair, the status tag pill, the big-number typography, the 2-or-4-across pairs grid)
was never actually declared anywhere in this file. Only child pieces nested INSIDE a `.di-kpi`
card (gauge centre, stackbar, fybars, byprod rows - each declared where first used, further down)
had real CSS. Every KPI card across 3 of the 6 tabs was being rendered from guesswork. Declare
once here (copied verbatim from the real product's `detailed-insights.component.css`, using this
document's token names):
`.di-kpigrid{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:var(--gap-md);margin-top:var(--gap-lg)}` `.di-kpi{grid-column:span 12;display:flex;flex-direction:column;gap:var(--gap-md);min-width:0;background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:var(--gap-lg) calc(var(--gap-lg) + 2px);box-shadow:0 1px 3px rgba(20,28,48,.05)}` `.di-kpi--span3{grid-column:span 3}` `.di-kpi--span4{grid-column:span 4}` `.di-kpi--span6{grid-column:span 6}` `.di-kpi--span8{grid-column:span 8}` `.di-kpi--span12{grid-column:span 12}` `.di-kpi__head{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}` `.di-kpi__headtext{min-width:0}` `.di-kpi__eyebrow{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-grey)}` `.di-kpi__title{font-size:var(--fs-di-snaphead);font-weight:600;letter-spacing:-.01em;line-height:1.3;margin:3px 0 0;color:var(--c-text)}` `.di-kpi__tag{flex-shrink:0;display:inline-flex;align-items:center;gap:5px;font-size:var(--fs-di-chip);font-weight:500;padding:3px 9px;border-radius:999px;white-space:nowrap;letter-spacing:.02em;border:1px solid transparent}` `.di-kpi__dot{width:6px;height:6px;border-radius:999px;flex-shrink:0}` `.di-kpi__tag--bad{background:#fcebea;color:#b3261e;border-color:#f3cfca}` `.di-kpi__tag--bad .di-kpi__dot{background:#b3261e}` `.di-kpi__tag--warn{background:#fdf3e2;color:#b45708;border-color:#f0dcb4}` `.di-kpi__tag--warn .di-kpi__dot{background:#b45708}` `.di-kpi__tag--ok{background:#e9f6ee;color:#1e8a4a;border-color:#c4e6d0}` `.di-kpi__tag--ok .di-kpi__dot{background:#1e8a4a}` `.di-kpi__big{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}` `.di-kpi__num{font-size:calc(var(--fs-di-snap-num) * 1.35);font-weight:600;letter-spacing:-.02em;line-height:1;color:var(--c-text)}` `.di-kpi__num--bad{color:#b3261e}` `.di-kpi__num--warn{color:#b45708}` `.di-kpi__num--ok{color:#1e8a4a}` `.di-kpi__unit{font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.4}` `.di-kpi__pairs{display:grid;grid-template-columns:1fr 1fr;gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden}` `.di-kpi__pairs--4{grid-template-columns:repeat(4,minmax(0,1fr))}` `.di-kpi__pair{background:var(--c-surface);padding:11px 13px;min-width:0}` `.di-kpi__pair-lbl{font-size:var(--fs-di-chip);color:var(--c-grey);text-transform:uppercase;letter-spacing:.06em;font-weight:500;margin-bottom:6px}` `.di-kpi__pair-val{font-size:var(--fs-di-snap-num);font-weight:600;letter-spacing:-.02em;line-height:1;color:var(--c-text)}` `.di-kpi__pair-sub{font-size:var(--fs-meta);color:var(--c-text-3);margin-top:4px;line-height:1.4}` `.di-kpi__pair--bad .di-kpi__pair-val{color:#b3261e}` `.di-kpi__pair--warn .di-kpi__pair-val{color:#b45708}` `.di-kpi__pair--ok .di-kpi__pair-val{color:#1e8a4a}` `.di-kpi__narr{font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.55;margin:0}`.
Use `.di-kpi__pairs--4` (added alongside `.di-kpi__pairs`, not instead of it) only on a card with
exactly 4 real pairs - the default 2-column grid otherwise.

## Tab 3 — Coverage

**[CHANGED LIVE, 2026-09-07] You do not author ANY of this pane's body.** Every number and every
markup shape this pane needs (`coverage_status_counts`, the region/tile grid, the KPI card, the
filter chips, the legend, the detail panel) is already known deterministically before you are ever
called - there is no real judgement call for you to make here, unlike a narrative-prose pane.
Earlier versions of this prompt asked you to write everything except the grid tiles themselves, on
the theory that a large hand-authored per-row grid was the only unreliable part - that was wrong: a
live render skipped the whole grid shape and substituted a plain KPI-only summary card instead
(FixedHolisticStructureGate's own doc comment, item 3 - "found live twice"), confirmed AGAIN twice
more on tenant 29 with the previous instructions. Writing any part of this pane's markup, even "just"
the wrapper around the grid, is real output you could get wrong on any given attempt - so none of
it is your job any more.

Write ONLY this, exactly, as the entire content of `<section class="di-pane" id="di-pane-3"
aria-label="Coverage">...</section>`:

```html
<section class="di-pane" id="di-pane-3" aria-label="Coverage">
  <div id="di-pane-3-body"></div>
</section>
```

`CoverageGridInjector` replaces `<div id="di-pane-3-body"></div>` - and everything else you might
put in this section instead - with the real KPI card, filter chips, region-grouped location grid, and
detail panel, generated directly from the real data. Do not write `di-kpi`, `di-covfilter`,
`di-covgrid`, `di-covtile`, `di-covlegend`, or `di-covdetail` markup of your own anywhere in this
pane - it will be discarded either way, so there is no benefit to attempting it. Move on to Tab 4.

## Tab 4 — Operations (`di-kpigrid`, up to 3 cards: Timeliness, People continuity, Evidence)

Each of the three cards needs its own real assertions. Render a card ONLY when its
primary figure is present this run; **OMIT any card whose primary assertion is
absent** (no muted card, no `di-blocked-note`). Re-pick the remaining cards' spans
so they fill the 12-column row. The tab badge = the number of cards you rendered.

**Rule for every "upgrade" piece below (fybars previous-period bar, byprod category rows, second
gauge): render it ONLY when this run's `assertions` actually contains the matching real field.
Never invent a previous period, a category split, or a second person's number to fill a shape that
looks better full.** Each upgrade is additive - the card's REAL primary figure (current on-time %,
top-3/SPOF %) always renders regardless of whether any upgrade data is present.

```html
<section class="di-pane" id="di-pane-4" aria-label="Operations">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">04</span><h2 class="di-pane__title">Key indicators</h2></div>
  <div class="di-kpigrid">
    <!-- Card 1, span8: Timeliness.
         [FIX - found live, 2026-09-09] This card used to have its own `.di-kpi__big` primary
         number, wired to Location's nonexistent A-TIMELINESS assertion - always empty. Checked
         the real product's own markup directly (`detailed-insights.component.html` lines
         399-421): the real card has NO separate big-number block at all. Head -> tag -> fybars ->
         byprod -> narrative, matching exactly, copied below. -->
    <article class="di-kpi di-kpi--span8">
      <div class="di-kpi__head">
        <div class="di-kpi__headtext">
          <div class="di-kpi__eyebrow">KPI &middot; Timeliness</div>
          <h3 class="di-kpi__title">{real 1-sentence headline, e.g. "On-time closure improved N pts year-over-year" - built from the real A-CURRENT vs_comparator_pp, never a static label}</h3>
        </div>
        <span class="di-kpi__tag di-kpi__tag--{tone from real vs_comparator_pp: ok if >0 (improving), warn if ==0 (flat), bad if <0 (declining) - a declining on-time rate is RED, matching the fybar below}"><span class="di-kpi__dot"></span>{real trend word, e.g. "improving"/"declining"/"flat"}</span>
      </div>
      <!-- REAL, always present when A-CURRENT exists (TimelinessFY dimension, sql/23). If
           A-CURRENT is absent (current FY has zero completed events with a known Timeliness
           classification), OMIT THIS WHOLE CARD - no muted body, no "not available", no
           fabricated 0%. -->
      <div class="di-fybars">
        <!-- The CURRENT-period bar's fill colour follows the real trend, NOT a fixed green:
             A-CURRENT.vs_comparator_pp  > 0  -> di-fybar--ok   (green)  on-time % improved
                                        == 0  -> di-fybar--warn (amber)  flat
                                         < 0  -> di-fybar--bad  (RED)    on-time % declined - ANY decline is red, no threshold
             A declining on-time rate fills RED. It never fills green, and it does not soften to amber. -->
        <!-- Keep BOTH classes: di-fybar--curr (this is the current period) AND the tone. Widths
             go inline: style="width:80.6%" - that IS allowed (only tenant TEXT is barred from a
             style attribute); never invent a ".w806" utility class for a width. -->
        <div class="di-fybar di-fybar--curr di-fybar--{ok if vs_comparator_pp>0, warn if ==0, bad if <0}">
          <div class="di-fybar__lbl"><span>{real current-period label, e.g. "FY2025-26 (current)"}</span><b class="tnum">{real current pct}%</b></div>
          <div class="di-fybar__track"><i style="width:{real current pct}%"></i></div>
        </div>
        <div class="di-fybar">
          <div class="di-fybar__lbl"><span>{real previous-period label}</span><b class="tnum">{real previous pct}%</b></div>
          <div class="di-fybar__track"><i style="width:{real previous pct}%"></i></div>
        </div>
      </div>
      <!-- [UPGRADE - per-category lateness] di-byprod = one row per compliance category (real
           product example: Labour/Core/Secretarial), bar length = that category's real
           lateness/overdue pct. [CHECKED, 2026-09-09] No dimension currently emits this grain -
           checked TimelinessFY (sql/23, rows are current_fy/previous_fy only, not per-category)
           and Act (sql/11, grain is per-Act, not per-broad-category) directly. Genuinely not
           available yet, not a wiring bug - OMIT THIS WHOLE BLOCK, not muted rows, until a real
           dimension emits per-category (Labour/Core/Secretarial-grain, not per-Act) lateness
           assertions. Never compute a category split yourself from tenant-wide numbers. If that
           source is ever added: cap at 5 rows by real materiality (CLAUDE.md Sec.4's own emission
           policy), never show all of them just because they exist. -->
      <div class="di-byprod">
        <div class="di-byprod__row di-byprod__row--{bad|warn|neu}">
          <div class="di-byprod__name">{real category name}</div>
          <div class="di-byprod__meter"><i style="width:{real category lateness pct}%"></i></div>
          <div class="di-byprod__val"><b class="tnum">{real category lateness pct}%</b></div>
        </div>
      </div>
      <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, real numbers only}</p>
    </article>

    <!-- Card 2, span4: People continuity / SPOF. -->
    <article class="di-kpi di-kpi--span4">
      <div class="di-kpi__head"><div class="di-kpi__headtext"><div class="di-kpi__eyebrow">People continuity</div><h3 class="di-kpi__title">Single-point-of-failure</h3></div></div>
      <!-- Gauge 1 - REAL, always present: top-3-performer-share OR reviewer-concentration pct,
           same data the composite score's people_continuity component uses. -->
      <div class="di-spofrow">
        <div class="di-gauge" aria-hidden="true">
          <svg viewBox="0 0 88 88">
            <circle cx="44" cy="44" r="36" fill="none" stroke="#eef1f7" stroke-width="9" />
            <circle cx="44" cy="44" r="36" fill="none" stroke="#d24a3a" stroke-width="9" stroke-linecap="round" stroke-dasharray="{pct/100 * 226} 226" />
          </svg>
          <!-- [BRAND HANDOFF §6, settled rule] "A gauge centre never puts N/N on one line" -
               stack it, count in ink over a small grey /total, never both in the arc's own status
               colour (red-on-red is unreadable - the arc alone carries status). If the real
               assertion only carries a percentage (no real count/total pair), put the percentage
               alone in the ink slot and OMIT the grey sub-line - never invent a fake denominator
               to fill it. -->
          <div class="di-gauge__ctr"><b class="tnum">{real count if the assertion carries one, else the pct}</b><span class="tnum">{/ real total - omit this span entirely if there is no real total}</span></div>
        </div>
        <div class="di-spofrow__text">{one line, from the real assertion's comparator text}</div>
      </div>
      <!-- Gauge 2 [UPGRADE - reviewer same-day-approval%] - a SECOND, DIFFERENT metric from gauge
           1: what share of reviews the leading reviewer approves within one day. This is a review-
           SPEED metric, not the review-CONCENTRATION metric gauge 1 already shows - do not reuse
           gauge 1's number here relabelled. Render this second .di-spofrow block ONLY if you are
           given a real same-day-approval assertion - no such assertion exists in the Users
           dimension as of this prompt's last check. Omit the whole second block, not a muted
           gauge, when absent - one real gauge beats one real + one empty circle. -->
      <div class="di-spofrow">
        <div class="di-gauge" aria-hidden="true">
          <svg viewBox="0 0 88 88">
            <circle cx="44" cy="44" r="36" fill="none" stroke="#eef1f7" stroke-width="9" />
            <circle cx="44" cy="44" r="36" fill="none" stroke="#d24a3a" stroke-width="9" stroke-linecap="round" stroke-dasharray="{real pct/100 * 226} 226" />
          </svg>
          <div class="di-gauge__ctr"><b class="tnum">{real same-day-approval pct}%</b></div>
        </div>
        <div class="di-spofrow__text">{one line, from the real assertion's comparator text, e.g. naming the leading reviewer's share}</div>
      </div>
    </article>

    <!-- Card 3, span12: Evidence integrity.
         [TENANT RULE 2026-09-10] Render this card ONLY if `A-REVIEWTRAIL` is present this run.
         If it is absent (zero completed/resolved schedules in scope to assess), OMIT THE WHOLE
         CARD - no di-blocked-note, no "not available" line. Cards 1-2 stay; re-span them. -->
    <article class="di-kpi di-kpi--span12">
      <div class="di-kpi__head"><div class="di-kpi__headtext"><div class="di-kpi__eyebrow">Evidence integrity</div><h3 class="di-kpi__title">Review trail</h3></div></div>
      <!-- [LIVE, 2026-09-02] EvidenceIntegrity dimension (sql/25) supplies this: `A-REVIEWTRAIL`
           assertion (`review_trail_pct` metric, tenant scope, `value` = real pct of closed
           schedules with more than one recorded ComplianceTransaction row). This is a WORKFLOW-
           TRAIL PROXY, never "evidence was attached" - `EvidenceIntegrityControlTotals.
           EvidenceInSql` is always false, and `A-REVIEWTRAIL`'s own `caveat` field says so
           explicitly. That caveat MUST travel with this number (README.md's own rule) - state it
           plainly in this card's narrative line, never just the bare pct. -->
      <div class="di-stackbar" aria-hidden="true"><span class="di-stackbar__seg--ok" style="width:{real A-REVIEWTRAIL value}%"></span><span class="di-stackbar__seg--bad" style="width:{100 minus that value}%"></span></div>
      <div class="di-stacklegend"><span class="di-stacklegend__item"><i class="di-stacklegend__sw di-stackbar__seg--ok"></i>With review trail &mdash; <b class="tnum">{real pct}%</b></span><span class="di-stacklegend__item"><i class="di-stacklegend__sw di-stackbar__seg--bad"></i>No review trail &mdash; <b class="tnum">{real complementary pct}%</b></span></div>
      <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, citing the real pct AND its workflow-trail-proxy caveat - never the pct alone}</p>
    </article>
  </div>
</section>
```
Add to your `<style>` block (gauge centre stacking, already covered above, plus the new pieces - copied from the real product's own recipes, using this document's token names):
`.di-gauge__ctr{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;line-height:1.15}` `.di-gauge__ctr b{color:var(--c-text);font-size:.94rem;font-weight:600}` `.di-gauge__ctr span{color:var(--c-grey);font-size:.6rem;font-weight:500}`
`.di-fybars{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-top:var(--gap-md)}` `.di-fybar{display:flex;flex-direction:column;gap:6px}` `.di-fybar__lbl{display:flex;justify-content:space-between;font-size:var(--fs-di-chip);text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:500}` `.di-fybar__lbl b{color:var(--c-text);font-weight:600}` `.di-fybar__track{height:8px;background:#eef1f7;border-radius:999px;overflow:hidden;border:1px solid var(--c-border)}` `.di-fybar__track>i{display:block;height:100%;border-radius:999px;background:linear-gradient(90deg,#c08605,#e0a106)}` `.di-fybar--ok .di-fybar__track>i{background:linear-gradient(90deg,#c79a1e,#2e9e5b)}` `.di-fybar--warn .di-fybar__track>i{background:linear-gradient(90deg,#c08605,#e0a106)}` `.di-fybar--bad .di-fybar__track>i{background:linear-gradient(90deg,#c0392b,#d24a3a)}`
`.di-byprod{display:grid;grid-template-columns:1fr;gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden;margin-top:var(--gap-md)}` `.di-byprod__row{background:var(--c-surface);display:grid;grid-template-columns:100px minmax(0,1fr) auto;align-items:center;gap:12px;padding:10px 14px}` `.di-byprod__name{font-size:var(--fs-meta);font-weight:500;color:var(--c-text)}` `.di-byprod__meter{height:6px;border-radius:999px;background:#eef1f7;position:relative;overflow:hidden}` `.di-byprod__meter>i{position:absolute;inset:0 auto 0 0;background:#d24a3a;border-radius:999px}` `.di-byprod__row--warn .di-byprod__meter>i{background:#e0a106}` `.di-byprod__row--neu .di-byprod__meter>i{background:#8a8f99}` `.di-byprod__val{font-size:var(--fs-meta);color:var(--c-text-3);text-align:right;min-width:90px}`
`.di-stackbar{height:10px;width:100%;border-radius:999px;overflow:hidden;display:flex;background:#eef1f7}` `.di-stackbar>span{height:100%;display:block}` `.di-stackbar__seg--ok{background:#2e9e5b}` `.di-stackbar__seg--bad{background:#d24a3a}` `.di-stacklegend{display:flex;flex-wrap:wrap;gap:14px;row-gap:6px;font-size:var(--fs-meta);color:var(--c-text-3);margin-top:8px}` `.di-stacklegend__item{display:inline-flex;align-items:center;gap:6px}` `.di-stacklegend__sw{width:8px;height:8px;border-radius:2px;flex-shrink:0}` `.di-stacklegend__item b{color:var(--c-text);font-weight:600}`

## Tab 5 — Forward look — FULLY INJECTED, you author only the shell

**[REWORKED 2026-09-10 — the entire pane body is now deterministic, exactly like Tab 3
Coverage.]** The render agent kept dropping this pane on real tenants, so it no longer authors
any of it. `InjectForwardLookActivity` builds the whole `.di-kpi--span12 .di-kpi--fwd` card -
the head (eyebrow `Forward pipeline`, title `Due distribution (next 90 days)`, verdict tag), the
"N due in the next 90 days" figure, the carried-forward / at-risk / clear `di-stackbar` +
`di-stacklegend`, the imprisonment `di-kpi__narr--alert` line, AND the 5-window `di-fwd` bucket
chart with its axis and a factual narrative line - all from `ForwardRisk` (sql/26) +
`ForwardPipeline` (sql/24) control totals + rows, which are not typed assertions and cannot
reach you.

**Write ONLY this, exactly, as the entire content of `<section class="di-pane" id="di-pane-5">`:**

```html
<section class="di-pane" id="di-pane-5" aria-label="Forward look">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">05</span><h2 class="di-pane__title">Key indicators</h2></div>
  <div id="di-forward-root"></div>
</section>
```

Do not write `di-kpi`, `di-kpi__big`, `di-stackbar`, `di-fwd`, `di-fwd__col`, `di-fwd__axis`, a
verdict tag, or any narrative line of your own in this pane - all of it is discarded and
replaced. `InjectForwardLookActivity` replaces `<div id="di-forward-root"></div>` with the real
card. If both ForwardRisk and ForwardPipeline degraded this run (rare), the injector leaves the
pane as just its head - a real degraded state, never a "not available" placeholder. The tab
badge is `1` when the card renders, `0` only if both sources degraded.

**You do NOT declare any CSS for this pane.** The Tab 5 card's styles (`.di-kpi--fwd`,
`.di-kpi__narr--alert`, the whole `.di-fwd` bucket-chart recipe) are injected deterministically
by `InjectForwardLookCssActivity` alongside the markup - injected markup, injected CSS, same as
the Coverage pane and the age bar.

## Tab 6 — Actions (UNBLOCKED - ranked from real assertions, never invented)

**[UNBLOCKED 2026-09-02]** This tab does NOT need a new data source to go live - every dimension's
real findings already exist in your `assertions` array (Risk, Coverage, Licence, People/SPOF,
Timeliness). What was missing was never data, it was a RANKING step - and ranking/prioritising is
explicitly agent territory (CLAUDE.md non-negotiable #1: the agent decides *what matters, in what
order* - never *what a number is*). So: **you** do the ranking here, same as Narrate already
composes prose from real assertions - but every stat, count and pct on every card must still trace
to a real assertion id, exactly like everywhere else in this document. **Never invent a problem to
fill a sixth card.** If real assertions across all dimensions only support 3 qualifying findings
this run, ship 3 cards - not 6, not a padded 4th with a softened, made-up finding.

**What makes a finding "qualifying"**: it has a real assertion behind it AND it represents a real,
actionable gap (not merely a dimension that computed successfully) - e.g. an overdue rate well
above the tenant average, a concentration/SPOF flag, confirmed licence lapses, a coverage-gap
detector firing, a poorly-timely category. Reuse the SAME materiality/emission-policy thinking
CLAUDE.md Sec.4 already applies to per-dimension findings (peer-relative, never an absolute
threshold you invent here) - do not build a parallel scoring scheme.

**Ranking**: order by real severity/materiality signals already present on the assertions
themselves (raw count affected, `OverduePct`/lapse-rate magnitude, `VsTenantPP`/`VsComparatorPP`
size, imprisonment/critical exposure, SPOF concentration pct) - highest real exposure first. This
ordering is a judgement call you are allowed to make; the NUMBERS that justify it are not.

**Effort (low/med/high) is the one field on each card with no assertion behind it** - it is your
operational judgement of how hard the recommended fix is, same status as narrative interpretation
elsewhere in this document. Everything else on a card (`stats`, `evidence`, table rows, pills) must
cite a real value.

```html
<section class="di-pane" id="di-pane-6" aria-label="Priority actions">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">06</span>
    <h2 class="di-pane__title">Priority actions</h2>
    <span class="di-pane__hint">Ranked by real exposure - expand a card for evidence and recommended steps</span>
  </div>
  <div class="di-actions">
    <!-- ONE di-action per qualifying real finding, most-severe first, capped at 6.
         [COLLAPSIBLE - TENANT OVERRIDE 2026-09-10, deliberately deviates from the handoff's
         "static document / no <details>" rule.] Action cards ARE expand/collapse now:
         `<details class="di-action">` + `<summary class="di-action__summary">` + a chevron.
         ONLY the first card (rank 01) carries the literal `open` attribute (expanded by
         default); every other card starts collapsed.
         rank is 1-based and must match this card's position. The rank chip is ALWAYS #b3261e
         regardless of rank number - a rank is an ordinal, not a severity; severity lives on the
         finding's own tone elsewhere. Do not add a di-action--rankN class. -->
    <details class="di-action" open>
      <summary class="di-action__summary">
        <span class="di-action__rank">{rank, zero-padded: 01, 02, ...}</span>
        <span class="di-action__main">
          <span class="di-action__top">
            <span class="di-action__domain">{real dimension name, lowercase: people continuity | coverage | licence | risk-weighted | timeliness}</span>
            <!-- Effort "battery": 3 mini bars + the label word, BOTH colour-coded by level -
                 di-effort--low = green, --med = amber, --high = red. The whole .di-effort element
                 is tinted (the bars keep their own explicit fills), so the label text picks up
                 the colour whether or not it is wrapped in a span. -->
            <span class="di-effort di-effort--{low|med|high}"><span class="di-effort__bars"><i></i><i></i><i></i></span>{low|medium|high} effort</span>
          </span>
          <span class="di-action__what">{one sentence: the real recommended action, tied to the real finding}</span>
          <span class="di-action__stats">
            <span class="di-action__k">{a real stat as a short chip, e.g. "target &middot; <b>4 accounts</b>" - value from a real assertion}</span>
          </span>
          <span class="di-action__outcome">
            <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M2 8l4 4 8-9" /></svg>
            <span><b>Outcome:</b> {what success looks like, stated in terms of a real metric moving - never a number you have not been given a baseline for}</span>
          </span>
        </span>
        <span class="di-action__chev" aria-hidden="true">
          <svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 4.5l3 3 3-3" /></svg>
        </span>
      </summary>
      <div class="di-action__detail">
        <h4 class="di-action__dtitle">{short real-finding title, e.g. "939 confirmed licence lapses"}</h4>
        <p class="di-action__dsummary">{2-3 sentence explanation of the real finding and why it matters - cite real numbers only}</p>
        <div>
          <p class="di-action__sech">Evidence</p>
          <div class="di-evidence">
            <!-- one di-ev per real supporting number - this is where the finding's real assertions surface as discrete facts -->
            <div class="di-ev"><div class="di-ev__lbl">{real metric label}</div><div class="di-ev__val tnum">{real value}<small>{real unit, if any}</small></div></div>
          </div>
        </div>
        <!-- OPTIONAL: a di-atable (ranked breakdown, e.g. top offending accounts/locations) or
             di-apills (a short tag list, e.g. affected categories) ONLY if real per-row/per-tag
             data exists for this finding - omit both entirely rather than invent rows. Real
             markup shape for each, if used (copied from the real product): -->
        <div>
          <p class="di-action__sech">{real table title, e.g. "Top affected locations"}</p>
          <div class="di-atable">
            <div class="di-atable__h" style="grid-template-columns:1fr 90px 80px">
              <span>{col 1 header}</span><span>{col 2 header}</span><span class="di-atable__end">{col 3 header}</span>
            </div>
            <!-- one di-atable__row per real ranked item - never pad to a round number -->
            <div class="di-atable__row" style="grid-template-columns:1fr 90px 80px">
              <div class="di-atable__name">{real name}<small>{real sub-label, if any}</small></div>
              <div class="di-atable__meter di-atable__meter--{bad|warn|neutral}"><i style="width:{real pct}%"></i></div>
              <div class="di-atable__v tnum"><b>{real strong value}</b><span>{real trailing text, if any}</span></div>
            </div>
          </div>
        </div>
        <div>
          <p class="di-action__sech">{real pills title, e.g. "Affected categories"}</p>
          <div class="di-apills">
            <!-- one di-apill per real tag - di-apill--bad only for a tag that itself denotes a bad state -->
            <span class="di-apill di-apill--{bad, only if warranted}">{real label}</span>
          </div>
        </div>
        <!-- OPTIONAL: a single di-anote, ONLY for a real caveat/proxy warning tied to this finding
             (e.g. the same workflow-trail-proxy caveat Tab 4's Evidence card carries) - never a
             restatement of the summary already above it. -->
        <div class="di-anote">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><circle cx="8" cy="8" r="6.5" /><path d="M8 5v4M8 11v.5" /></svg>
          <span>{real caveat text}</span>
        </div>
        <div>
          <p class="di-action__sech">Recommended steps</p>
          <ol class="di-steps">
            <!-- 2-4 concrete, real-finding-tied steps - operational guidance, never a fabricated fact or number -->
            <li>{step}</li>
          </ol>
        </div>
      </div>
    </details>
  </div>
</section>
```
The tab-nav badge for Actions (in the tab-nav markup near the top of this document) must equal the
REAL number of cards you actually rendered (0-6) - never the literal 6 from the reference product's
own mock data, and never hand-set independent of what's in the pane. **If zero real findings
qualify this run** (a clean tenant), render the pane with ONLY its `di-pane__head` and nothing
else - no `di-blocked-note`, no "no priority actions" sentence - and set the badge to 0. An empty
Actions pane is a good outcome, and it needs no explanation.

Declare once (based on `holistic-insights-tenant1300.html`, with the collapsible `<details>` /
`[open]` / chevron chrome restored per the 2026-09-10 tenant override):
`.di-actions{display:flex;flex-direction:column;gap:.7rem;margin-top:var(--gap-lg)}` `.di-action{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);overflow:hidden}` `.di-action[open]{box-shadow:0 6px 20px rgba(20,28,48,.10)}` `.di-action__summary{list-style:none;cursor:pointer;display:grid;grid-template-columns:auto 1fr auto;gap:12px;align-items:flex-start;padding:14px 16px}` `.di-action__summary::-webkit-details-marker{display:none}` `.di-action[open] .di-action__summary{border-bottom:1px solid var(--c-border)}` `.di-action__rank{width:2rem;height:2rem;border-radius:var(--r-md);display:flex;align-items:center;justify-content:center;font-weight:600;font-size:.8rem;color:#fff;background:#b3261e;font-variant-numeric:tabular-nums;flex-shrink:0}` `.di-action__main{display:flex;flex-direction:column;gap:6px;min-width:0}` `.di-action__top{display:flex;align-items:center;gap:10px;flex-wrap:wrap}` `.di-action__domain{font-size:var(--fs-di-chip);text-transform:uppercase;letter-spacing:.06em;color:var(--c-grey);font-weight:600}` `.di-effort{display:inline-flex;align-items:center;gap:6px;font-size:var(--fs-di-chip);font-weight:600;padding:3px 10px;border-radius:999px}` `.di-effort--low{color:#1e8a4a;background:rgba(30,138,74,.12)}` `.di-effort--med{color:#b45708;background:rgba(180,87,8,.12)}` `.di-effort--high{color:#b3261e;background:rgba(179,38,30,.12)}` `.di-effort__bars{display:inline-flex;gap:2px}` `.di-effort__bars i{width:3px;height:10px;border-radius:1px;background:#d4d4d4;display:block}` `.di-effort--low .di-effort__bars i:nth-child(1){background:#1e8a4a}` `.di-effort--med .di-effort__bars i:nth-child(1),.di-effort--med .di-effort__bars i:nth-child(2){background:#b45708}` `.di-effort--high .di-effort__bars i{background:#b3261e}` `.di-action__what{font-size:var(--fs-di-headline);font-weight:500;color:var(--c-text)}` `.di-action__stats{display:flex;flex-wrap:wrap;gap:8px}` `.di-action__k{font-size:var(--fs-di-chip);color:var(--c-text-3);background:var(--c-content-mist);border:1px solid var(--c-border);border-radius:999px;padding:3px 10px}` `.di-action__k b{color:var(--c-text);font-weight:600}` `.di-action__outcome{display:flex;align-items:flex-start;gap:6px;font-size:var(--fs-meta);color:var(--c-text-3)}` `.di-action__outcome svg{width:14px;height:14px;flex-shrink:0;margin-top:2px;color:#1e8a4a}` `.di-action__outcome b{color:#1e8a4a;font-weight:600}` `.di-action__chev{width:22px;height:22px;border-radius:50%;background:var(--c-bg);border:1px solid var(--c-border);display:flex;align-items:center;justify-content:center;flex-shrink:0;color:var(--c-text-3)}` `.di-action__chev svg{width:11px;height:11px}` `.di-action[open] .di-action__chev{background:var(--c-brand);border-color:var(--c-brand);color:#fff;transform:rotate(180deg)}` `.di-action__detail{border-top:1px solid var(--c-border);padding:14px 16px;display:flex;flex-direction:column;gap:12px}` `.di-action__dtitle{font-size:var(--fs-di-headline);font-weight:600;margin:0;color:var(--c-text)}` `.di-action__dsummary{margin:0;font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.55}` `.di-action__sech{font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:600;margin:0 0 6px}` `.di-evidence{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden}` `.di-ev{background:var(--c-surface);padding:8px 11px}` `.di-ev__lbl{font-size:10px;text-transform:uppercase;letter-spacing:.06em;color:var(--c-grey);margin-bottom:3px}` `.di-ev__val{font-size:var(--fs-di-headline);font-weight:600;color:var(--c-text)}` `.di-ev__val small{font-weight:500;color:var(--c-text-3);margin-left:2px}` `.di-steps{margin:0;padding-left:1.1rem;display:flex;flex-direction:column;gap:5px;font-size:var(--fs-meta);color:var(--c-text-2)}`

**[FIX - dangling reference, found live, 2026-09-09]** The block above never covered `di-atable`/
`di-apills`/`di-apill`/`di-anote` even though they were named as legitimate optional markup just
above (this document's own text). Declare once (copied verbatim from the real product's
`detailed-insights.component.css`, using this document's token names - `#f3cfca` below is this
document's own existing bad-tone border shade, reused rather than the real product's own
undeclared `--c-red-stroke` token):
`.di-atable{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden}` `.di-atable__h,.di-atable__row{display:grid;align-items:center;gap:12px;padding:8px 12px;border-top:1px solid var(--c-border)}` `.di-atable__h{border-top:0;background:var(--c-bg);font-size:var(--fs-di-snap-label);color:var(--c-grey);text-transform:uppercase;letter-spacing:.07em;font-weight:600}` `.di-atable__end{text-align:right}` `.di-atable__name{font-size:var(--fs-di-snap-desc);color:var(--c-text)}` `.di-atable__name small{display:block;font-size:.86em;color:var(--c-grey);margin-top:1px}` `.di-atable__meter{height:6px;border-radius:999px;background:var(--c-bg);border:1px solid var(--c-border);position:relative;overflow:hidden}` `.di-atable__meter i{position:absolute;inset:0 auto 0 0;border-radius:999px;display:block}` `.di-atable__meter--bad i{background:#b3261e}` `.di-atable__meter--warn i{background:#b45708}` `.di-atable__meter--neutral i{background:var(--c-grey)}` `.di-atable__v{font-size:var(--fs-di-snap-desc);color:var(--c-text-3);text-align:right}` `.di-atable__v b{color:var(--c-text);font-weight:600}` `.di-apills{display:flex;flex-wrap:wrap;gap:6px}` `.di-apill{display:inline-flex;align-items:center;background:var(--c-surface);border:1px solid var(--c-border);border-radius:999px;padding:4px 11px;font-size:var(--fs-di-snap-desc);font-weight:500;color:var(--c-text)}` `.di-apill--bad{background:#fceae8;border-color:#f3cfca;color:#b3261e}` `.di-anote{background:#fcf0de;border:1px solid #e8c79c;border-radius:var(--r-md);padding:9px 11px;font-size:var(--fs-di-snap-desc);color:#7a4a08;line-height:1.5;display:flex;gap:8px;align-items:flex-start}` `.di-anote svg{width:14px;height:14px;flex-shrink:0;margin-top:1px;color:#b45708}`

## Persistent caveats footer

**[FIX - dangling reference, found live]** This section used to say "reuse verbatim from
`05_report_html_holistic.md`'s pattern" - that file has no such pattern to reuse. Written out in
full here instead.

**[BRAND HANDOFF §6, settled rule] Caveats scale with their count - two different treatments,
never mixed.** A SHORT note (1-2 lines - a single methodology aside) gets the blue informational
wash. A LIST of caveats (this report type routinely has 5-11 real `data_quality` notes) becomes a
WHITE card - never a blue slab. *"An 11-item blue slab is colour-as-field - rejected on sight."*
Blue means status/information; a list that long reads as one giant status flag if it's blue, which
is not what it is.

Blue wash, for a single short note only (declare `.di-note{background:#e8f2fd;border-left:3px
solid #125aab;border-radius:var(--r-sm);padding:10px 14px;font-size:var(--fs-di-comp-meta);
color:var(--c-text-2)}` `.di-note b,.di-note strong{color:#125aab;font-weight:600}` once):
```html
<p class="di-note"><strong>{bold lead phrase}</strong> {plain explanation, one sentence}</p>
```

White card, for the real caveat list (one `<li>` per real `data_quality` note actually present in
the dimension data you were given - never invented, never trimmed to make the list shorter):
```html
<footer class="di-caveats" aria-label="Data-quality caveats">
  <div class="di-caveats__eyebrow">Data-quality caveats</div>
  <ul class="di-caveats__list">
    <li class="di-caveats__item">
      <span class="di-caveats__ico" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.35" stroke="currentColor" stroke-width="1.3"/><path d="M8 5v4" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><circle cx="8" cy="10.6" r="0.15" stroke="currentColor" stroke-width="1.3"/></svg></span>
      <span class="di-caveats__text"><b>{bold lead phrase, e.g. "Reviewer dependency is counted at instance level"}</b>{plain explanation of what that means, e.g. ", so it is not double-counted across users."}</span>
    </li>
    <!-- one .di-caveats__item per real data_quality note -->
  </ul>
</footer>
```
Declare once (copied verbatim from the real product's `detailed-insights.component.css`, using this
document's own token names):
```css
.di-caveats{margin-top:var(--gap-lg);background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);padding:var(--gap-lg) calc(var(--gap-lg) + 3px)}
.di-caveats__eyebrow{font-size:var(--fs-di-eyebrow);font-weight:600;text-transform:uppercase;letter-spacing:.1em;color:var(--c-grey)}
.di-caveats__list{list-style:none;margin:8px 0 0;padding:0}
.di-caveats__item{display:grid;grid-template-columns:18px 1fr;gap:10px;padding:9px 0;font-size:calc(var(--fs-di-headline) - 1px);line-height:1.55;color:var(--c-text-3);border-top:1px solid var(--c-border)}
.di-caveats__item:first-child{border-top:0}
.di-caveats__ico{margin-top:2px;color:var(--c-grey);display:inline-flex}
.di-caveats__ico svg{width:14px;height:14px}
.di-caveats__text b{color:var(--c-text);font-weight:600}
```

## Section-number chips (`di-secnum`) — one per tab heading, `01`–`06`

**[BRAND HANDOFF §6, settled rule]** Every `<h2 class="di-pane__title">` across all six tabs gets
a quiet editorial number chip immediately to its left, numbered `01` through `06` in tab order
(Snapshot=01 ... Actions=06 - matches this template's fixed tab order, never renumbered per-run).
Neutral only - never blue (it would stack against the blue eyebrows/caveats elsewhere on the page),
never a circle (that shape means action rank, in red, on Tab 6's own action cards).
```html
<div class="di-pane__head"><span class="di-secnum" aria-hidden="true">01</span><h2 class="di-pane__title">Key indicators</h2></div>
```
Declare once: `.di-pane__head{display:flex;align-items:center;gap:10px;margin:0 0 12px}` `.di-secnum{display:inline-flex;align-items:center;justify-content:center;width:2rem;height:2rem;border-radius:var(--r-md);background:var(--c-surface);border:1px solid #dbdbdb;color:#585858;font-size:.8rem;font-weight:600;font-variant-numeric:tabular-nums;flex-shrink:0}`. The `margin:0 0 12px` on `.di-pane__head` is what clears the first card in every pane by 12px (§6).

**[FIX - dangling reference, found live, 2026-09-09]** `<h2 class="di-pane__title">` and
`<span class="di-pane__hint">` are used at the top of every one of the six tabs (this document's
own markup, throughout) - only their `.di-pane__head` wrapper and the `.di-secnum` badge next to
them ever got declared above. Declare once (copied verbatim from the real product's
`detailed-insights.component.css`): `.di-pane__title{font-size:var(--fs-di-snaphead);font-weight:600;letter-spacing:-.01em;color:var(--c-text)}` `.di-pane__hint{font-size:var(--fs-di-snap-desc);color:var(--c-grey)}`.

## Vocabulary — binding (`AI-INSIGHTS-BRAND-HANDOFF.md` §5)

- **"insights", never "report"** - "the insights below", never "this report".
- **"Entity", never "Organisation"** - matches this codebase's own dictionary terms.
- Role names exactly as the dictionary uses them: **Performer, Reviewer, Compliance Officer,
  Compliance Owner** - never a paraphrase ("owner" alone, "approver", etc.).
- A compliance ID is a bare number with its noun attached: "on compliance 119205", never
  "compliance #119205" or the number alone.
- Never repeat a word between an eyebrow and the title directly below it - the eyebrow is context,
  not a duplicate headline.

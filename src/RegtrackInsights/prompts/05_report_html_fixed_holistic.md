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

## Binding brand spec — `AI-INSIGHTS-BRAND-HANDOFF.md` (2026-09-01)

Layout structure (which blocks, in what order, at what density) is yours to decide;
visual identity (every hex, gradient, font, radius, shadow, chip shape) is fixed by
that file's §2-§3 tokens, verbatim - never re-derived. Its §4 component vocabulary,
§5 voice rules, §6 settled generation rules, and §7 acceptance check all apply here.
The reference implementation is `reference/gpt-5.6-terra-05-refined.html` from that
handoff package - when a rule and instinct disagree, match that file, with two
confirmed exceptions below (checked against this project's own architecture, not
guessed):

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
present in the array, that tile has no real data yet — render the "not
available yet" state below for it, never a plausible-looking number.** This
project's whole architecture exists to prevent exactly the mistake of a
confident-looking number nobody actually verified — treat that as absolute
here.

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

Add these tokens to your `:root` block (missing ones only - keep whatever you
already declared for `--c-*`/`--r-*`/`--gap-*`/`--fs-*`, these are additive):
`--r-xl:20px;` `--di-donut:92px;` `--di-hero-pad-y:16px;` `--di-hero-pad-x:16px;`
`--fs-di-score:1.9rem;` `--fs-di-of:.66rem;` `--fs-di-title:.94rem;`
`--fs-di-sub:.72rem;` `--fs-di-method:.66rem;` `--fs-di-comp-name:.66rem;`
`--fs-di-comp-num:.88rem;`

**Composite score → tone**, same 3-band legend the component grid itself
shows (`≥70` ok / `40-69` warn / `<40` bad) - compute once from
`A-SCORE-composite`'s real `value`, reuse for the donut ring AND the verdict
band pill, never picked independently for each:
`ok` if score ≥ 70, `warn` if 40-59.99... wait, 40-69, `bad` if < 40.

```html
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
          <div class="di-donut__num tnum">{real A-SCORE-composite value, e.g. 45}</div>
          <div class="di-donut__of">/ 100</div>
        </div>
      </div>
      <div class="di-id">
        <div class="di-eyebrow">Composite</div>
        <h1 class="di-title">Compliance-health score</h1>
        <div class="di-sub">{tenant name}</div>
        <div class="di-meta">
          <span class="di-chip"><b>Generated</b> {real generatedAt, ISO}</span>
        </div>
      </div>
    </div>
    <div class="di-verdict">
      <div class="di-verdict__row">
        <!-- Band/Trend come from A-SCORE-composite's own caveat text
             ("Band: X. Trend: Y. PROVISIONAL..."), never invented or reworded -->
        <span class="di-band di-band--{tone}"><span class="di-band__dot"></span>{real Band text}</span>
        <span class="di-trend">
          <svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M1 9l4-4 2 2 4-4M11 3v3H8"></path></svg>
          Trend: {real Trend text}
        </span>
      </div>
      <p class="di-headline">{one-sentence real interpretation, from narrative prose only, citing the real score and real band - never a number not in this run's assertions}</p>
      <p class="di-method">PROVISIONAL - not yet reviewed with the business. {rest of A-SCORE-composite's own caveat text, verbatim}</p>
    </div>
  </div>

  <div class="di-components">
    <div class="di-components__head">
      <span class="di-components__label">Score components · weighted</span>
      <span class="di-legend">
        <span><i class="di-legend__sw di-legend__sw--ok"></i>&ge; 70</span>
        <span><i class="di-legend__sw di-legend__sw--warn"></i>40 - 69</span>
        <span><i class="di-legend__sw di-legend__sw--bad"></i>&lt; 40</span>
      </span>
    </div>
    <div class="di-components__grid">
      <!-- ALWAYS EXACTLY 7 di-comp cards - a structural invariant a deterministic
           post-render gate enforces (FixedHolisticStructureGate), never optional.
           The 7 fixed component slots, in this order: risk_weighted, coverage,
           overdue_backlog, people_continuity, timeliness, licence, evidence.
           For each slot: if a real A-SCORE-{domain_kpi} assertion is present
           this run, render it REAL (below) - name humanised (risk_weighted ->
           "Risk-weighted", overdue_backlog -> "Overdue backlog",
           people_continuity -> "People continuity", coverage -> "Coverage",
           licence -> "Licence", timeliness -> "Timeliness", evidence ->
           "Evidence"), score = the assertion's real `value`, weight = its real
           `comparator_value`, tone = the SAME 3-band rule as the composite ring
           above. If that slot's assertion is ABSENT this run ("not every
           component is guaranteed present", per this run's own README rule),
           render the MUTED variant instead - same card shape, no bar fill, no
           invented number - never skip the slot and never fabricate a score to
           fill it. -->
      <div class="di-comp">
        <div class="di-comp__name">{real component name}<small>Weight {real weight}</small></div>
        <div class="di-comp__bar di-comp__bar--{tone}"><i style="width:{real score}%"></i></div>
        <div class="di-comp__row"><b class="tnum">{real score}</b><span>{real score} &times; {real weight}</span></div>
      </div>
      <!-- muted slot (no A-SCORE-{domain_kpi} assertion this run) - repeat this
           shape, not the real one above, for each missing component: -->
      <div class="di-comp di-comp--muted">
        <div class="di-comp__name">{component name}<small>Not scored this run</small></div>
        <div class="di-comp__bar"><i style="width:0%"></i></div>
        <div class="di-comp__row"><b class="tnum">—</b><span>no data</span></div>
      </div>
    </div>
    <p class="di-sub" style="margin-top:10px">Component scoring is provisional - not yet reviewed with the business.</p>
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
.di-hero__inner{position:relative;display:grid;grid-template-columns:1fr 1.35fr;gap:calc(var(--gap-lg)*1.7);align-items:center;padding:var(--di-hero-pad-y) var(--di-hero-pad-x)}
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
.di-title{font-size:var(--fs-di-title);font-weight:600;letter-spacing:-.02em;line-height:1.15;margin:6px 0 4px;color:var(--di-fg)}
.di-sub{font-size:var(--fs-di-sub);color:var(--di-fg-muted)}
.di-meta{margin-top:var(--gap-md);display:flex;flex-wrap:wrap;gap:6px}
.di-chip{font-size:var(--fs-di-chip);color:var(--di-fg-muted);background:var(--di-panel);border:1px solid var(--di-line);padding:3px 10px;border-radius:999px;white-space:nowrap}
.di-chip b{color:var(--di-fg);font-weight:600}
.di-verdict{min-width:0}
.di-verdict__row{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.di-band{display:inline-flex;align-items:center;gap:8px;padding:4px 12px;border-radius:999px;font-size:var(--fs-di-chip);font-weight:500;letter-spacing:.02em}
.di-band__dot{width:6px;height:6px;border-radius:999px}
.di-band--warn{background:#fcf0de;color:#b45708;border:1px solid #e8c79c}
.di-band--warn .di-band__dot{background:#e0a106}
.di-band--bad{background:#fceae8;color:#b3261e;border:1px solid #dfa39d}
.di-band--bad .di-band__dot{background:#b3261e}
.di-band--ok{background:#e7f5ec;color:#1e8a4a;border:1px solid #a8d3b8}
.di-band--ok .di-band__dot{background:#1e8a4a}
.di-trend{display:inline-flex;align-items:center;gap:6px;color:#1e8a4a;font-size:var(--fs-di-chip)}
.di-trend svg{width:12px;height:12px}
.di-headline{font-size:var(--fs-di-headline);line-height:1.45;color:var(--di-fg);margin:14px 0 0}
.di-method{margin-top:12px;font-size:var(--fs-di-method);color:var(--di-fg-muted);line-height:1.55}
.di-components{position:relative;background:rgba(255,255,255,.5);border-top:1px solid var(--di-line);padding:var(--gap-lg) var(--di-hero-pad-x)}
.di-components__head{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:var(--gap-md)}
.di-components__label{font-size:var(--fs-di-eyebrow);text-transform:uppercase;letter-spacing:.1em;font-weight:600;color:var(--c-brand)}
.di-legend{display:flex;gap:14px;font-size:var(--fs-di-chip);color:var(--di-fg-muted)}
.di-legend__sw{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:5px;vertical-align:1px}
.di-legend__sw--ok{background:var(--di-ok)}
.di-legend__sw--warn{background:var(--di-warn)}
.di-legend__sw--bad{background:var(--di-bad)}
.di-components__grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px}
.di-comp{background:var(--di-panel);border:1px solid var(--di-line);border-radius:var(--r-md);padding:11px 11px 9px;display:flex;flex-direction:column;gap:9px;min-width:0}
.di-comp--muted{opacity:.6}
.di-comp--muted .di-comp__row b{color:var(--di-fg-faint)}
.di-comp__name{font-size:var(--fs-di-comp-name);font-weight:500;color:var(--di-fg);letter-spacing:.01em}
.di-comp__name small{display:block;font-weight:400;font-size:calc(var(--fs-di-comp-meta) - .5px);color:var(--di-fg-faint);margin-top:1px}
.di-comp__bar{position:relative;height:5px;border-radius:999px;background:#eef1f7;overflow:hidden}
.di-comp__bar>i{position:absolute;inset:0 auto 0 0;border-radius:999px;display:block}
.di-comp__bar--ok>i{background:var(--di-ok)}
.di-comp__bar--warn>i{background:var(--di-warn)}
.di-comp__bar--bad>i{background:var(--di-bad)}
.di-comp__row{display:flex;align-items:baseline;justify-content:space-between;font-size:var(--fs-di-comp-meta);color:var(--di-fg-faint)}
.di-comp__row b{color:var(--di-fg);font-weight:600;font-size:var(--fs-di-comp-num)}
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
      <label for="di-tab-2" class="di-tab" role="tab">Risk &amp; licences<span class="di-tab__count tnum">3</span></label>
      <label for="di-tab-3" class="di-tab" role="tab">Coverage<span class="di-tab__count tnum">1</span></label>
      <label for="di-tab-4" class="di-tab" role="tab">Operations<span class="di-tab__count tnum">3</span></label>
      <label for="di-tab-5" class="di-tab" role="tab">Forward look<span class="di-tab__count tnum">1</span></label>
      <label for="di-tab-6" class="di-tab" role="tab">Actions<span class="di-tab__count tnum">0</span></label>
    </nav>
  </div>

  <!-- the six <section class="di-pane" id="di-pane-N"> panes go here, as direct
       children of .di-tabsroot, siblings of the six radios above -->
</div>
```
[FIX - found live, self-contradicting] The 3/1/3/1 badges on tabs 2-5 count that tab's fixed
KPI-card slots (always rendered, real or muted) - legitimate, never fake. [UPDATED, 2026-09-02]
Tab 5 (Forward look) is REAL now (see Tab 5 below, ForwardPipeline dimension, sql/24) - it has
exactly ONE fixed KPI-card slot (the 90-day pipeline card), so its badge is a fixed "1", same
convention as tabs 2-4, and it is never `data-blocked` any more. As of this change, every one of
the six tabs renders normally - no pane in this document is `data-blocked="true"` any more
(Actions was unblocked 2026-09-02 too, see Tab 6 below). `FixedHolisticStructureGate`'s
blocked-badge check (Insights.Presentation, runs on every render via
ValidateFixedHolisticStructureActivity) is written generically against WHATEVER panes carry
`data-blocked="true"` - a document with none is simply a no-op for that check, not a special case.
Keep this in mind if a future tile genuinely has no real source yet: mark that pane
`data-blocked="true"` and its badge "0", exactly as this file documented before Tab 5's own data
landed - the mechanism is still live even though nothing in this template currently exercises it.
CLAUDE.md non-negotiable #2 is the reason it exists at all: never guess, never default - a live
render once shipped "Actions" badged **6** while the pane itself said "This section has no real
data source yet in this run", a fabricated number sitting one line from its own admission nothing
backed it.

**Tab 6 (Actions) is DIFFERENT again, as of 2026-09-02: it is UNBLOCKED** (see Tab 6 below) - it
renders 0-6 real ranked cards from real cross-dimension assertions, never a static count. Its
badge is the REAL number of cards you rendered this run, same "count only what actually rendered"
rule, just no longer pinned to 0 - `di-pane-6` no longer carries `data-blocked="true"` at all, so
the structure gate does not enforce a fixed badge on it the way it still does for Tab 5.

Required CSS (add to the document's `<style>` block, verbatim, alongside the
rest of the brand styling — do not omit any rule or this degrades back to
scroll-only):

```css
.di-tab-input { position: absolute; opacity: 0; pointer-events: none; }
.di-tabpane, .di-pane { display: none; }
#di-tab-1:checked ~ #di-pane-1,
#di-tab-2:checked ~ #di-pane-2,
#di-tab-3:checked ~ #di-pane-3,
#di-tab-4:checked ~ #di-pane-4,
#di-tab-5:checked ~ #di-pane-5,
#di-tab-6:checked ~ #di-pane-6 { display: block; }
```
`label.di-tab` reuses the exact same visual styling `button.di-tab` used before
(same class, same hex values, same `--active` look) — only the element and the
activation mechanism changed, not the appearance. Give `label[for="di-tab-N"]`
the same visual treatment `.di-tab--active` gave the first tab, but keyed off
`:checked` so it moves with the click. The labels sit inside `.di-stickytabs`,
not as direct siblings of the radios, so a plain `~`/`+` sibling selector
cannot reach them — use `:has()` instead (this renders in headless Chromium,
which has supported `:has()` since 2022, so it is safe here):
```css
.di-tabsroot:has(#di-tab-1:checked) label[for="di-tab-1"],
.di-tabsroot:has(#di-tab-2:checked) label[for="di-tab-2"],
.di-tabsroot:has(#di-tab-3:checked) label[for="di-tab-3"],
.di-tabsroot:has(#di-tab-4:checked) label[for="di-tab-4"],
.di-tabsroot:has(#di-tab-5:checked) label[for="di-tab-5"],
.di-tabsroot:has(#di-tab-6:checked) label[for="di-tab-6"] {
  /* apply exactly the same background/color/weight di-tab--active used before */
}
```
Every one of the six `<section class="di-pane" ...>` markup blocks below now
needs a matching `id="di-pane-N"` added (N = 1..6, in tab order) — add it to
the `aria-label` element shown in each tab's section below; it is not written
inline in every snippet to avoid repeating the whole block six times.

## The "not available yet" pattern — REUSE for every blocked tile/card

When a tile or card has no backing assertion, do not omit it silently (the
real page shows the tile) and do not invent a number. Use the SAME tile
markup with a muted, explicit label instead of a number:

```html
<article class="di-snaptile di-snaptile--muted">
  <div class="di-snaptile__label">{what it would measure}</div>
  <div class="di-snaptile__num tnum">Not available yet</div>
  <div class="di-snaptile__desc">No data source built for this metric yet.</div>
</article>
```
**[BRAND HANDOFF §6, settled rule] No inline styles on placeholder/ghost states -
ever.** The muting above comes from the `di-snaptile--muted` CLASS (declare it in
your `<style>` block: `opacity: 0.6` on the article, `color: var(--c-grey)` on
`.di-snaptile--muted .di-snaptile__num`), never a `style=` attribute. Same rule
for an entire blocked TAB (Forward look, Actions - see below): quiet, left-aligned
grey prose that inherits the panel's own rhythm - no `text-align:center`, no
`padding:40px` block, no per-instance `style=` attribute of any kind:
```html
<div class="di-pane__head"><h2 class="di-pane__title">Key indicators</h2></div>
<p class="di-blocked-note">This section has no real data source yet in this run.</p>
```
Declare `.di-blocked-note{color:var(--c-grey);margin-top:var(--gap-md)}` once in
your `<style>` block and reuse the class everywhere a section is blocked - Tab 4's
Evidence card, Tab 5, and Tab 6 all use it, never a repeated inline `style=`.

## Tab 1 — Snapshot (`di-snapgrid`/`di-snaptile`, 8 tiles)

```html
<section class="di-pane" id="di-pane-1" aria-label="Snapshot">
  <div class="di-snaphero">
    <p class="di-snaphero__headline">{one-sentence overview, from the narrative's snapshot-equivalent prose if present}</p>
  </div>
  <div class="di-snapgrid" style="display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px">
    <!-- one di-snaptile per tile below, real or muted -->
    <article class="di-snaptile di-snaptile--{tone}">
      <div class="di-snaptile__label">{label}</div>
      <div class="di-snaptile__num tnum">{value}</div>
      <div class="di-snaptile__desc">{one-line context, from the matching assertion's comparator fields only}</div>
      <!-- [FIX - found live, matches the real product's di-snaptile__jump exactly] Every tile,
           real OR muted, ends with a jump link to the tab that expands on it - the real product's
           own JUMP_LABELS ("See risks & licences →" etc). A real Angular app does this with a
           click handler; this document has no JS for it (nor needs one) - it is the SAME CSS-only
           radio mechanism the tab nav itself already uses. Wrap the jump line in a <label> for the
           target tab's radio input, not a <button> - clicking a <label for="di-tab-N"> checks that
           radio exactly like clicking the real tab does, with zero script. -->
      <label for="di-tab-{N}" class="di-snaptile__jump">
        <span class="di-snaptile__dot"></span>{real JUMP_LABELS text for that tab, e.g. "See risks &amp; licences →"}
      </label>
    </article>
  </div>
</section>
```
**Jump targets, exact tab numbers** (must match the tab-nav radios above verbatim): Risk·imprisonment/Licence·lapses/Risk-weighted content → `di-tab-2`; Backlog·overdue/Coverage·stores → `di-tab-3`; People·SPOF/Timeliness/Evidence·review-trail → `di-tab-4`; Forward·next-90-days → `di-tab-5`. No tile jumps to `di-tab-6` (Actions) or back to `di-tab-1` (Snapshot is where the tile already is).

Add to your `<style>` block (copied from the real product's own `.di-snaptile__jump`/`.di-snaptile__dot`, using this document's token names): `.di-snaptile__jump{display:inline-flex;align-items:center;gap:6px;margin-top:8px;font-size:var(--fs-di-chip);color:var(--c-text-3);cursor:pointer;text-decoration:none}` `.di-snaptile__jump:hover{color:var(--c-brand)}` `.di-snaptile__dot{width:6px;height:6px;border-radius:999px;background:var(--c-grey);display:inline-block}` `.di-snaptile--bad .di-snaptile__dot{background:#b3261e}` `.di-snaptile--warn .di-snaptile__dot{background:#b45708}` `.di-snaptile--ok .di-snaptile__dot{background:#1e8a4a}`.

The 8 tiles, in this order, and their REAL source (use the muted pattern for any not listed as real):
1. **Risk · imprisonment overdue** — NOT AVAILABLE. [FIX] No such assertion exists, by design, not by
   omission: `sql/08_dimension_risk.sql` computes `ImprisonmentOverdue` but deliberately never
   promotes it to a standalone assertion, because imprisonment-bearing instances are ~95-99.6% the
   same population as the Critical risk tier (see that file's own header trap and its narrative
   guard on `A-CRIT`: "Do not also raise imprisonment exposure as a separate finding"). The only
   real imprisonment assertion is `A-IMP-OVERLAP` (imprisonment-on-critical overlap pct), already
   used correctly in Tab 2 Card 1 below - do not reuse it here as if it meant "overdue".
2. **Licence · confirmed lapses** — REAL. Source: `LicenceControlTotals`/Licence dimension row data (corroborated lapses).
3. **Backlog · overdue** — REAL. Source: Location dimension's tenant overdue count/pct.
4. **Coverage · stores mapped** — REAL. Source: Location dimension's `BranchesReported` / ghost-entity data.
5. **People · SPOF** — REAL. Source: Users dimension (top-3/reviewer concentration, same data the composite score's `people_continuity` component uses).
6. **Timeliness · on-time closure** — REAL. Source: Location dimension's `A-TIMELINESS`/`ontime_pct`
   assertion (`TenantOnTimePct` in control_totals) — tenant-wide, event-level (not instance-level),
   lifetime (not FY-scoped). Absent when the tenant has zero completed events with a known
   Timeliness classification (`A-TIMELINESS` is not emitted in that case) - use the muted pattern
   then, never a fabricated 0%.
7. **Evidence · review trail** — REAL, 2026-09-02. Source: EvidenceIntegrity dimension (sql/25),
   `A-REVIEWTRAIL` assertion (`review_trail_pct` metric, tenant scope) — the real pct of closed
   schedules with more than one recorded `ComplianceTransaction` row. This is a workflow-trail
   PROXY, never "evidence was attached" — its `caveat` field says so explicitly and MUST travel
   with the number wherever cited (README.md's own rule). Absent when the tenant has zero
   completed/resolved schedules in scope — use the muted pattern then.
8. **Forward · next 90 days** — REAL, 2026-09-02. Source: ForwardPipeline dimension (sql/24),
   `DueNext90d` in control_totals — the real count of schedules due in the next 90 days, scoped.
   Always present (the proc always returns a control_totals row, even when the real count is 0 —
   a real zero is not "not available", render it as a real 0, never muted).

## Tab 2 — Risk & licences (`di-kpigrid`/`di-kpi`, 3 cards, ALL real)

```html
<section class="di-pane" id="di-pane-2" aria-label="Risk and licences">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">02</span>
    <h2 class="di-pane__title">Key indicators</h2>
  </div>
  <div class="di-kpigrid" style="display:grid;grid-template-columns:repeat(12,1fr);gap:12px">
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
      <!-- (c) ageBar - segmented horizontal AGING bar, NOT a percentage/progress bar. Use ONLY
           when you are given a real bucket array (each bucket: a real count + a real label,
           oldest-to-newest, e.g. from an A-OVERDUE-AGE-style assertion) - never invent bucket
           boundaries or split a single total evenly across guessed buckets. Total count goes
           ABOVE the bar (in di-kpi__big, same slot the bigNumber shape uses), not inside it. -->
      <div class="di-kpi__big">
        <div class="di-kpi__num tnum di-kpi__num--{tone}">{real total overdue count}</div>
        <div class="di-kpi__unit">overdue obligations</div>
      </div>
      <div class="di-agebar">
        <div class="di-agebar__scale">
          <!-- ONE di-agebar__seg per real bucket, in the SUPPLIED order (oldest first). flex
               style is the bucket's own count (or share of the total) - segment WIDTH is
               proportional by construction, never hand-picked. Tone by age: oldest buckets
               bad(red)/#d24a3a, middle warn(amber)/#e0a106, newest neu(grey)/#8a8f99 - follow
               whatever tone the assertion itself carries if it has one, this is a fallback only. -->
          <div class="di-agebar__seg di-agebar__seg--{bad|warn|neu}" style="flex:{real bucket count}">
            <span class="di-agebar__seglabel">{real bucket count} · {real bucket label}</span>
            <span class="di-agebar__tip">{real bucket count} · {real bucket label}</span>
          </div>
        </div>
        <div class="di-agebar__axis">
          <!-- one <span> per bucket label, same order, for the axis row under the bar -->
          <span>{real bucket label}</span>
        </div>
      </div>
      <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, still citing only real numbers}</p>
    </article>
  </div>
</section>
```
Declare once (copied from the real product's `.di-agebar` recipe, using this document's token names): `.di-agebar{display:flex;flex-direction:column;gap:8px}` `.di-agebar__scale{height:26px;display:flex;border-radius:var(--r-md);overflow:hidden;border:1px solid var(--c-border)}` `.di-agebar__seg{position:relative;height:100%;display:flex;align-items:center;justify-content:center;color:#fff;font-size:var(--fs-di-chip);font-weight:600;border-right:1px solid rgba(255,255,255,.4)}` `.di-agebar__seg:last-child{border-right:0}` `.di-agebar__seglabel{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0;max-width:100%;padding:0 6px}` `.di-agebar__seg--bad{background:#d24a3a}` `.di-agebar__seg--warn{background:#e0a106;color:#5a3d00}` `.di-agebar__seg--neu{background:#8a8f99}` `.di-agebar__axis{display:flex;justify-content:space-between;gap:8px;font-size:var(--fs-meta);color:var(--c-grey)}`. Omit `di-agebar__seglabel`'s text (leave the segment bare) when the segment is too narrow to hold it legibly - never shrink the font past readable or truncate mid-number to force a fit.

Card 1 — **Risk-weighted** (pairs shape): critical-overdue count, critical-share-of-overdue-pct,
imprisonment-critical-overlap-pct — all from the Risk dimension's critical-tier row/assertions.
Card 2 — **Overdue / Backlog**: **[UPGRADE LIVE, 2026-09-02]** Use the ageBar shape above - the
BacklogAging dimension (sql/22) now supplies exactly the real age-bucketed breakdown this shape
needs: 3 fixed buckets, `A-OLDER`/`A-PREVIOUS_FY`/`A-CURRENT_FY` assertions (`backlog_share_pct`
metric, `value` = that bucket's real share-of-backlog pct). Render segments OLDEST-TO-NEWEST, left
to right: `older` (tone bad/red), `previous_fy` (tone warn/amber), `current_fy` (tone neu/grey) -
segment `flex` = that bucket's real `OverdueCount` from the `rows` array (ageBar's
`style="flex:{count}"` wants a real count, not a pct - both reconcile to the same proportions,
`rows` is simpler to read directly). Segment label = `{OverdueCount} · {FYLabel}`, using "pre-
{PreviousFyLabel}" in place of FYLabel for the `older` bucket (its own FYLabel is null by
construction - never left blank on the segment). Total count ABOVE the bar (di-kpi__big) =
`SumOfRows` from BacklogAgingControlTotals - the real total overdue count, the same population
this card's bigNumber already showed, just now bucketed. If the `older_bucket_dominates` detector
fired aggregate (an `A-OLDER-AGG` assertion is present), cite its real headline in this card's
narrative line - real evidence the backlog is structurally old, not just behind this year. Overdue
is a flow metric (the underlying data carries this caveat) - never compare this bar's shape
against a different run. If `BacklogAgingControlTotals.SumOfRows` is 0 (the `zero_overdue`
data_quality note is present), fall back to the bigNumber-only shape showing a real 0 - never
render a bar with three zero-width segments.
Card 3 — **Licence** (pairs shape): corroborated lapses, stores affected, avg days overdue,
expiring-in-90d — from `LicenceControlTotals` and Licence dimension rows. If `expiring_90d` has no
real assertion, omit that one pair rather than invent it - 3 real pairs beats 4 with one fake.

## Tab 3 — Coverage (`di-kpigrid` KPI card + region-grouped, REAL CLICK-TO-SELECT store grid)

You are given a `coverage_status_counts` object alongside `assertions` for this run:
`{total, healthy, under_configured, has_ownerless, unmapped}` - 5 REAL numbers, leaf branches only,
computed the same way the store grid itself is classified (see the note below - never recomputed
or guessed, always echoed exactly). **This is the ONE exception to "every number comes from
assertions" in this whole prompt.** Never use these 5 numbers as material for a narrative claim,
a comparative, or a ranking outside the chip/KPI/legend text below - if you want to say something
about coverage in prose, it needs its own assertion, exactly like everywhere else in this document.

**[CHANGED LIVE, 2026-09-02] You do not author the store grid itself.** A live render asked to
hand-write one `<button>` tile per real leaf branch (up to 177 for tenant 29) silently drew a
SAMPLE (10 of 177) instead of the whole population, while still stating the true full counts in
the chip/legend/KPI text next to it - the same class of problem as the driving script (see its own
note further down). The grid is generated deterministically, post-generation, from the real data -
your only job is to leave the single placeholder `<div id="di-covgrid-root"></div>` exactly where
the region/tile markup would go (see the section marked "GRID GOES HERE" below) and write
everything else in this pane for real, using `coverage_status_counts`.

**[REBUILT LIVE, 2026-09-02] This tab reproduces the REAL production reference component
(`detailed-insights.component.html`/`.css`/`.data.ts`) exactly - not a reinterpretation. The
reference's own 4-state taxonomy is NOT a mock invention: it is copied verbatim (same 4 names,
even the same distribution numbers) from this repo's own `docs/PAID_TIER_SAMPLE_REFERENCE.md`
Sec.3.4 (`status_counts: healthy 530 / under_configured 46 / has_ownerless 26 / unmapped 30`).
Never rename these to "Healthy"/"No obligations"/"High ownerless" or drop `unmapped` - a prior
render did both and it was a real defect, not a style choice, confirmed against the reference
component directly.**

**The 4 real states, their labels, and their ONE consistent colour each - identical across chip
swatch, tile fill, and detail-panel pill, never different per element:**

| `status` key | Label | Colour | Meaning |
|---|---|---|---|
| `healthy` | Mapped | `#2e9e5b` (green) | Obligations mapped, no ownerless gap |
| `under_configured` | Under-configured | `#e0a106` (yellow) | Materially fewer obligations mapped than peer branches |
| `has_ownerless` | Has ownerless | `#e07a1f` (orange) | Has obligations with no performer assigned |
| `unmapped` | Unmapped | `#c0392b` (red) | Zero obligations mapped at all - invisible to every overdue report |

**Why `coverage_status_counts.under_configured` is always 0 today:** that status needs a real
obligation-COUNT peer norm (how many obligations comparable branches carry) that no procedure
computes yet - `usp_Insights_Dimension_Location`'s only real peer comparison (`VsPeerStateNormPP`)
is an OVERDUE-RATE peer gap, a different concept, so it is never used to decide this. The chip, its
swatch, and the legend entry still render (the taxonomy is real and complete even though no branch
currently qualifies) - state this plainly in the caveats footer (§ below), the same "say less,
never fabricate" treatment as every other gated piece in this document.

```html
<section class="di-pane" id="di-pane-3" aria-label="Coverage">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">03</span><h2 class="di-pane__title">Key indicators</h2></div>
  <div class="di-kpigrid">
    <article class="di-kpi di-kpi--span12">
      <!-- same di-kpi shape as tab 2 - stores mapped / ownerless / unmapped counts, all real -->
    </article>
  </div>
  <div class="di-covwrap">
    <div class="di-covmap">
      <div class="di-covfilter" role="toolbar" aria-label="Coverage status counts">
        <button type="button" class="di-covchip" data-filter="all">All <b class="tnum">{coverage_status_counts.total}</b></button>
        <button type="button" class="di-covchip" data-filter="healthy"><i class="di-covchip__sw di-covchip__sw--healthy"></i>Mapped <b class="tnum">{coverage_status_counts.healthy}</b></button>
        <button type="button" class="di-covchip" data-filter="under_configured"><i class="di-covchip__sw di-covchip__sw--under_configured"></i>Under-configured <b class="tnum">{coverage_status_counts.under_configured}</b></button>
        <button type="button" class="di-covchip" data-filter="has_ownerless"><i class="di-covchip__sw di-covchip__sw--has_ownerless"></i>Has ownerless <b class="tnum">{coverage_status_counts.has_ownerless}</b></button>
        <button type="button" class="di-covchip" data-filter="unmapped"><i class="di-covchip__sw di-covchip__sw--unmapped"></i>Unmapped <b class="tnum">{coverage_status_counts.unmapped}</b></button>
      </div>
      <!-- ============ GRID GOES HERE ============
           Leave EXACTLY this one placeholder div - nothing else, no di-covregion/di-covtile
           markup of your own. CoverageGridInjector replaces it with one real di-covregion block
           per real StateName (grouped, most-branches-first, a null/blank state grouped as
           "Unassigned state" - never dropped) and one real di-covtile button per real leaf
           branch, all classified from the real data the same way coverage_status_counts already
           was, so the two can never disagree. -->
      <div id="di-covgrid-root"></div>
      <!-- ========================================= -->
      <div class="di-covlegend">
        <span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--healthy"></i>Mapped <b class="tnum">{coverage_status_counts.healthy}</b></span>
        <span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--under_configured"></i>Under-configured <b class="tnum">{coverage_status_counts.under_configured}</b></span>
        <span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--has_ownerless"></i>Has ownerless <b class="tnum">{coverage_status_counts.has_ownerless}</b></span>
        <span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--unmapped"></i>Unmapped <b class="tnum">{coverage_status_counts.unmapped}</b></span>
      </div>
    </div>
    <!-- Sticky detail card - starts on the FIRST tile emitted above (script fires a synthetic
         click on load, see the script block below), then updates for real on every click. Leave
         every value EMPTY below - the injected script fills all of it, per real reference field
         structure (di-covdetail__metrics/__m/__ml/__mv, six fields: Mapped, Coverage, Ownerless,
         Overdue, Performer, Peer gap). -->
    <aside class="di-covdetail di-covdetail--side" aria-live="polite">
      <div class="di-covdetail__head">
        <span class="di-covdetail__pill"><span class="di-covdetail__dot"></span></span>
        <span class="di-covdetail__ref"></span>
      </div>
      <h4 class="di-covdetail__title"></h4>
      <p class="di-covdetail__summary"></p>
      <div class="di-covdetail__metrics">
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Mapped</div><div class="di-covdetail__mv tnum"></div></div>
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Coverage</div><div class="di-covdetail__mv tnum"></div></div>
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Ownerless</div><div class="di-covdetail__mv tnum"></div></div>
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Overdue</div><div class="di-covdetail__mv tnum"></div></div>
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Performer</div><div class="di-covdetail__mv"></div></div>
        <div class="di-covdetail__m"><div class="di-covdetail__ml">Peer gap</div><div class="di-covdetail__mv tnum"></div></div>
      </div>
      <div class="di-covdetail__action"><div class="di-covdetail__action-h">Recommended action</div><p></p></div>
    </aside>
  </div>
</section>
```

**[CHANGED LIVE, 2026-09-02] Do NOT write a `<script>` for this pane yourself, and do not write
the store grid tiles yourself either.** Both are generated deterministically, post-generation
(`CoverageGridInjector` then `CoverageScriptInjector`, same treatment the self-hosted Poppins font
already gets from `PoppinsFontInjector`) - the script reads real `data-*` attributes straight off
the injected `di-covtile` buttons (`data-st`, `data-branch-id`, `data-state`, `data-branch-name`,
`data-instances`, `data-overdue`, `data-ownerless`, `data-performer`; there is no
`data-overdue-pct` or `data-peer-gap` attribute - the script computes "Coverage %" and "Peer gap"
itself from `data-instances` and the optional `data-peer-norm`, which is never written since no
real per-branch peer-obligation-count norm exists yet). Two real, separate failure classes were
found live from asking the render agent to author these itself every run: the SCRIPT (DOMPurify's
default strip, DOMPurify's defensive strip of a script whose content contained HTML-tag-shaped
text, the render agent simply omitting it on a given attempt) and the GRID (asked to hand-write up
to 177 individual tiles, the render agent silently sampled instead of completing the population,
while still stating the true full counts in the chip/legend/KPI text right next to it) - both
vanish once neither is something you have to get right. If you write either yourself anyway it
will be ignored (both injectors are idempotent / placeholder-anchored), so there is no benefit to
attempting it - leave `<div id="di-covgrid-root"></div>` exactly as scaffolded above and this
pane's `<div class="di-covdetail di-covdetail--side">...</div>` markup exactly as scaffolded
below, with its inner text/values left for the injected script to fill in at load time, and move
on to the CSS below.

**[CHANGED LIVE, 2026-09-02] You do not write the Coverage pane's CSS either.** A live render
shipped tiles as unfilled outline boxes and a colourless detail pill because the render agent's
own "declare these rules verbatim" copy of this CSS silently dropped or malformed the colour
declarations - the same failure family as the grid and the script above. This CSS is 100% static
(zero data substitution, identical every render) and is now injected automatically
(`CoverageCssInjector`), last inside `</head>`, so there is nothing left for you to write for this
pane's styling. It references the SAME `--c-*`/`--fs-di-*`/`--gap-*`/`--r-*` tokens every other
tab in this document already defines in your `:root` block - as long as those exist (they already
do if you followed the rest of this prompt), nothing Coverage-specific needs adding to your
`<style>` block at all.

## Tab 4 — Operations (`di-kpigrid`, 3 cards: Timeliness real (+ upgrades), People real (+ upgrade), Evidence BLOCKED)

**Rule for every "upgrade" piece below (fybars previous-period bar, byprod category rows, second
gauge): render it ONLY when this run's `assertions` actually contains the matching real field.
Never invent a previous period, a category split, or a second person's number to fill a shape that
looks better full.** Each upgrade is additive - the card's REAL primary figure (current on-time %,
top-3/SPOF %) always renders regardless of whether any upgrade data is present.

```html
<section class="di-pane" id="di-pane-4" aria-label="Operations">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">04</span><h2 class="di-pane__title">Key indicators</h2></div>
  <div class="di-kpigrid">
    <!-- Card 1, span8: Timeliness. -->
    <article class="di-kpi di-kpi--span8">
      <div class="di-kpi__head"><div class="di-kpi__headtext"><div class="di-kpi__eyebrow">Timeliness</div><h3 class="di-kpi__title">On-time closure</h3></div></div>
      <!-- REAL, always present when A-TIMELINESS exists: Location dimension's TenantOnTimePct,
           tenant-wide, lifetime. If A-TIMELINESS is absent (zero completed events with a known
           Timeliness classification), fall back to the muted "not available yet" body for this
           WHOLE card instead - never fabricate 0%. -->
      <div class="di-kpi__big">
        <div class="di-kpi__num tnum">{real TenantOnTimePct}%</div>
        <div class="di-kpi__unit">of completed closure events, tenant-wide, lifetime</div>
      </div>
      <!-- [UPGRADE LIVE, 2026-09-02] di-fybars = horizontal comparison bars. The TimelinessFY
           dimension (sql/23) now supplies this: `A-CURRENT` assertion (`ontime_pct` metric,
           `value` = real current-FY on-time pct, `comparator_value` = real previous-FY on-time
           pct, `vs_comparator_pp` = real year-over-year change). This is a DIFFERENT figure from
           the card's own bigNumber above (which stays the Location dimension's lifetime
           TenantOnTimePct, a different denominator - never conflate the two as if they were the
           same number). Current-period label = TimelinessFYControlTotals.CurrentFyLabel, previous
           = PreviousFyLabel. Bar length is the real pct itself (width:{pct}%), never rescaled or
           guessed. Render ONLY when `A-CURRENT` is present - it is absent when the current FY has
           zero completed events with a known Timeliness classification (never render 0% then). If
           `A-CURRENT` has no `comparator_value` (previous FY also has zero completed events),
           render only the current-period bar and omit the previous one rather than an empty
           track. -->
      <div class="di-fybars">
        <div class="di-fybar di-fybar--curr">
          <div class="di-fybar__lbl"><span>{real current-period label, e.g. "FY2025-26 (current)"}</span><b class="tnum">{real current pct}%</b></div>
          <div class="di-fybar__track"><i style="width:{real current pct}%"></i></div>
        </div>
        <div class="di-fybar">
          <div class="di-fybar__lbl"><span>{real previous-period label}</span><b class="tnum">{real previous pct}%</b></div>
          <div class="di-fybar__track"><i style="width:{real previous pct}%"></i></div>
        </div>
      </div>
      <!-- [UPGRADE - per-category lateness] di-byprod = one row per compliance category, bar
           length = that category's real lateness/overdue pct. Render ONLY if you are given
           real per-category assertions (e.g. the Nature or Act dimension's own per-member
           OverduePct, threaded through as assertions for this purpose) - do not compute a
           category split yourself from tenant-wide numbers. Cap at 5 rows by real materiality
           (CLAUDE.md Sec.4's own emission policy) if more than 5 categories are supplied - never
           show all of them just because they exist. -->
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

    <!-- Card 3, span12: Evidence integrity. -->
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
      <div class="di-stacklegend"><span class="di-stacklegend__item"><i class="di-stacklegend__sw di-stackbar__seg--ok"></i>With review trail — <b class="tnum">{real pct}%</b></span><span class="di-stacklegend__item"><i class="di-stacklegend__sw di-stackbar__seg--bad"></i>No review trail — <b class="tnum">{real complementary pct}%</b></span></div>
      <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, citing the real pct AND its workflow-trail-proxy caveat - never the pct alone}</p>
      <!-- Render the fallback below INSTEAD of the whole block above ONLY if `A-REVIEWTRAIL` is
           absent this run (zero completed/resolved schedules in scope to assess): -->
      <p class="di-blocked-note">Not available yet - no completed or resolved schedules in scope this run to assess a review trail against.</p>
    </article>
  </div>
</section>
```
Add to your `<style>` block (gauge centre stacking, already covered above, plus the new pieces - copied from the real product's own recipes, using this document's token names):
`.di-gauge__ctr{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;line-height:1.15}` `.di-gauge__ctr b{color:var(--c-text);font-size:.94rem;font-weight:600}` `.di-gauge__ctr span{color:var(--c-grey);font-size:.6rem;font-weight:500}`
`.di-fybars{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-top:var(--gap-md)}` `.di-fybar{display:flex;flex-direction:column;gap:6px}` `.di-fybar__lbl{display:flex;justify-content:space-between;font-size:var(--fs-di-chip);text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:500}` `.di-fybar__lbl b{color:var(--c-text);font-weight:600}` `.di-fybar__track{height:8px;background:#eef1f7;border-radius:999px;overflow:hidden;border:1px solid var(--c-border)}` `.di-fybar__track>i{display:block;height:100%;border-radius:999px;background:linear-gradient(90deg,#c08605,#e0a106)}` `.di-fybar--curr .di-fybar__track>i{background:linear-gradient(90deg,#c79a1e,#2e9e5b)}`
`.di-byprod{display:grid;grid-template-columns:1fr;gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden;margin-top:var(--gap-md)}` `.di-byprod__row{background:var(--c-surface);display:grid;grid-template-columns:100px minmax(0,1fr) auto;align-items:center;gap:12px;padding:10px 14px}` `.di-byprod__name{font-size:var(--fs-meta);font-weight:500;color:var(--c-text)}` `.di-byprod__meter{height:6px;border-radius:999px;background:#eef1f7;position:relative;overflow:hidden}` `.di-byprod__meter>i{position:absolute;inset:0 auto 0 0;background:#d24a3a;border-radius:999px}` `.di-byprod__row--warn .di-byprod__meter>i{background:#e0a106}` `.di-byprod__row--neu .di-byprod__meter>i{background:#8a8f99}` `.di-byprod__val{font-size:var(--fs-meta);color:var(--c-text-3);text-align:right;min-width:90px}`
`.di-stackbar{height:10px;width:100%;border-radius:999px;overflow:hidden;display:flex;background:#eef1f7}` `.di-stackbar>span{height:100%;display:block}` `.di-stackbar__seg--ok{background:#2e9e5b}` `.di-stackbar__seg--bad{background:#d24a3a}` `.di-stacklegend{display:flex;flex-wrap:wrap;gap:14px;row-gap:6px;font-size:var(--fs-meta);color:var(--c-text-3);margin-top:8px}` `.di-stacklegend__item{display:inline-flex;align-items:center;gap:6px}` `.di-stacklegend__sw{width:8px;height:8px;border-radius:2px;flex-shrink:0}` `.di-stacklegend__item b{color:var(--c-text);font-weight:600}`

## Tab 5 — Forward look (`di-kpi--span12` + `di-fwd`, REAL, 2026-09-02)

**[UNBLOCKED 2026-09-02]** ForwardPipeline dimension (sql/24) supplies this whole tab: real counts
of schedules due in the next 90 days, bucketed into 5 fixed day-windows. No `data-blocked="true"`
any more - the proc always returns a real control_totals row, even when the real count is 0 (a
real zero renders normally, it is not "not available"; that is what the `zero_due_next_90d`
data_quality note is for). `predicted_at_risk` (the design doc's OTHER Sec.3.8 field) is still NOT
built - it is a projection with no defined model, never fabricated as a byproduct of these real
counts; this tab reports real due-counts only, exactly matching its own SQL header.

```html
<section class="di-pane" id="di-pane-5" aria-label="Forward look">
  <div class="di-pane__head"><span class="di-secnum" aria-hidden="true">05</span><h2 class="di-pane__title">Key indicators</h2></div>
  <div class="di-kpi__big">
    <div class="di-kpi__num tnum">{real DueNext90d from ForwardPipelineControlTotals}</div>
    <div class="di-kpi__unit">due in the next 90 days</div>
  </div>
  <div class="di-fwd" aria-hidden="true">
    <!-- one <i> per real window, IN THIS ORDER: 0-7d, 8-14d, 15-30d, 31-60d, 61-90d (matches the
         rows array's own order). height = that window's real DueCount as a %-of-the-busiest-
         window (compute the max DueCount across all 5 real rows, then each window's pct of it -
         never a guessed or evenly-spaced height). Tone: 'bad' if the near_term_concentration
         detector fired aggregate (an `A-NEARTERM-AGG` assertion is present) AND this window is
         0-7d or 8-14d; 'warn' if that detector's FlaggedPct > 0 but did not reach aggregate AND
         this window is 0-7d or 8-14d; 'ok' otherwise. -->
    <i class="di-fwd__bar di-fwd__bar--{ok|warn|bad}" style="height:{real}%"></i>
  </div>
  <div class="di-fwd__axis">
    <!-- one <span> per real WindowLabel, same order - the real labels the proc returns, never
         the mock demo's "This week/+45d/+90d" scale (that assumed evenly-spaced daily
         granularity this proc does not compute). -->
    <span>0-7d</span><span>8-14d</span><span>15-30d</span><span>31-60d</span><span>61-90d</span>
  </div>
  <p class="di-kpi__narr">{1-2 sentence interpretation, from narrative prose, citing the real
    total and - if `A-NEARTERM-AGG` is present - the real near-term-concentration finding
    ("N of M schedules (X%) fall within the next 14 days")}</p>
</section>
```
Declare once: `.di-fwd{display:flex;align-items:flex-end;gap:4px;height:64px;padding:4px 0}` `.di-fwd__bar{flex:1;min-width:0;border-radius:3px;border:1px solid var(--c-border);background:#eef1f7}` `.di-fwd__bar--warn{background:#fdf3e2;border-color:#f0dcb4}` `.di-fwd__bar--bad{background:#fcebea;border-color:#f3cfca}` `.di-fwd__axis{display:flex;justify-content:space-between;font-size:var(--fs-meta);color:var(--c-grey);margin-top:2px}`.

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
    <span class="di-pane__hint">Ranked by real exposure - expand any card for evidence and recommended steps</span>
  </div>
  <div class="di-actions">
    <!-- ONE di-action per qualifying real finding, most-severe first, capped at 6. Only the
         FIRST card carries the literal `open` attribute (expanded by default) - every other
         card starts collapsed. rank is 1-based and must match this card's position. -->
    <details class="di-action di-action--rank{1-based rank}" open>
      <summary class="di-action__summary">
        <span class="di-action__rank">{rank, zero-padded: 01, 02, ...}</span>
        <span class="di-action__main">
          <span class="di-action__top">
            <span class="di-action__domain">{real dimension name, lowercase: people continuity | coverage | licence | risk-weighted | timeliness}</span>
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
        <!-- OPTIONAL: a di-atable (ranked breakdown, e.g. top offending accounts/stores) or
             di-apills (a short tag list, e.g. affected categories) ONLY if real per-row/per-tag
             data exists for this finding - omit both entirely rather than invent rows. -->
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
own mock data, and never hand-set independent of what's in the pane. If zero real findings qualify
this run, render the pane with ONLY the header and a `di-blocked-note` ("No priority actions
qualify this run - every dimension's findings were within normal range."), and the badge is 0 -
this is a legitimate, good outcome (a clean tenant), not a failure to fill 6 slots.

Declare once (copied from the real product's own recipes, using this document's token names):
`.di-actions{display:flex;flex-direction:column;gap:.7rem}` `.di-action{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-lg);overflow:hidden}` `.di-action[open]{box-shadow:0 6px 20px rgba(20,28,48,.10)}` `.di-action__summary{list-style:none;cursor:pointer;display:grid;grid-template-columns:auto 1fr auto;gap:12px;align-items:flex-start;padding:14px 16px}` `.di-action__summary::-webkit-details-marker{display:none}` `.di-action[open] .di-action__summary{border-bottom:1px solid var(--c-border)}` `.di-action__rank{width:2rem;height:2rem;border-radius:var(--r-md);display:flex;align-items:center;justify-content:center;font-weight:600;font-size:.8rem;color:#fff;background:#125aab;font-variant-numeric:tabular-nums;flex-shrink:0}` `.di-action--rank1 .di-action__rank{background:#b3261e}` `.di-action--rank2 .di-action__rank{background:#8f1d17}` `.di-action--rank3 .di-action__rank{background:#b45708}` `.di-action__main{display:flex;flex-direction:column;gap:6px;min-width:0}` `.di-action__top{display:flex;align-items:center;gap:10px;flex-wrap:wrap}` `.di-action__domain{font-size:var(--fs-di-chip);text-transform:uppercase;letter-spacing:.06em;color:var(--c-grey);font-weight:600}` `.di-effort{display:inline-flex;align-items:center;gap:5px;font-size:var(--fs-di-chip);color:var(--c-text-3)}` `.di-effort__bars{display:inline-flex;gap:2px}` `.di-effort__bars i{width:3px;height:9px;border-radius:1px;background:#d4d4d4;display:block}` `.di-effort--low .di-effort__bars i:nth-child(1){background:#1e8a4a}` `.di-effort--med .di-effort__bars i:nth-child(1),.di-effort--med .di-effort__bars i:nth-child(2){background:#b45708}` `.di-effort--high .di-effort__bars i{background:#b3261e}` `.di-action__what{font-size:var(--fs-di-headline);font-weight:500;color:var(--c-text)}` `.di-action__stats{display:flex;flex-wrap:wrap;gap:8px}` `.di-action__k{font-size:var(--fs-di-chip);color:var(--c-text-3);background:var(--c-content-mist);border:1px solid var(--c-border);border-radius:999px;padding:3px 10px}` `.di-action__k b{color:var(--c-text);font-weight:600}` `.di-action__outcome{display:flex;align-items:flex-start;gap:6px;font-size:var(--fs-meta);color:var(--c-text-3)}` `.di-action__outcome svg{width:14px;height:14px;flex-shrink:0;margin-top:2px;color:#1e8a4a}` `.di-action__outcome b{color:#1e8a4a;font-weight:600}` `.di-action__chev{width:20px;height:20px;border-radius:50%;background:var(--c-content-mist);display:flex;align-items:center;justify-content:center;flex-shrink:0}` `.di-action[open] .di-action__chev{transform:rotate(180deg)}` `.di-action__detail{padding:14px 16px;display:flex;flex-direction:column;gap:12px}` `.di-action__dtitle{font-size:var(--fs-di-headline);font-weight:600;margin:0;color:var(--c-text)}` `.di-action__dsummary{margin:0;font-size:var(--fs-meta);color:var(--c-text-3);line-height:1.55}` `.di-action__sech{font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:600;margin:0 0 6px}` `.di-evidence{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden}` `.di-ev{background:var(--c-surface);padding:8px 11px}` `.di-ev__lbl{font-size:10px;text-transform:uppercase;letter-spacing:.06em;color:var(--c-grey);margin-bottom:3px}` `.di-ev__val{font-size:var(--fs-di-headline);font-weight:600;color:var(--c-text)}` `.di-ev__val small{font-weight:500;color:var(--c-text-3);margin-left:2px}` `.di-steps{margin:0;padding-left:1.1rem;display:flex;flex-direction:column;gap:5px;font-size:var(--fs-meta);color:var(--c-text-2)}`

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
Declare once: `.di-pane__head{display:flex;align-items:center;gap:10px}` `.di-secnum{display:inline-flex;align-items:center;justify-content:center;width:2rem;height:2rem;border-radius:var(--r-md);background:var(--c-surface);border:1px solid #dbdbdb;color:#585858;font-size:.8rem;font-weight:600;font-variant-numeric:tabular-nums;flex-shrink:0}`.

## Vocabulary — binding (`AI-INSIGHTS-BRAND-HANDOFF.md` §5)

- **"insights", never "report"** - "the insights below", never "this report".
- **"Entity", never "Organisation"** - matches this codebase's own dictionary terms.
- Role names exactly as the dictionary uses them: **Performer, Reviewer, Compliance Officer,
  Compliance Owner** - never a paraphrase ("owner" alone, "approver", etc.).
- A compliance ID is a bare number with its noun attached: "on compliance 119205", never
  "compliance #119205" or the number alone.
- Never repeat a word between an eyebrow and the title directly below it - the eyebrow is context,
  not a duplicate headline.

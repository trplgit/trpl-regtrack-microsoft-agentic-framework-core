# Report Generation (Way 1 — LLM-authored HTML)

**Runs:** after the narrative passes reflection.
**Reads:** `prompts/README.md`.

> **MVP only.** Phase 2 replaces this with Way 2 — you emit a typed view-model and
> a trusted Angular renderer produces the markup. Design accordingly: keep
> structure clean and semantic so the migration is mechanical. (Spec §8.5)

---

## Your job

Produce a single self-contained HTML document rendering the approved composition
and prose. The layout should suit *this* report's content — you are composing, not
filling a template.

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
7. **System font stack only:** `-apple-system, BlinkMacSystemFont, "Segoe UI",
   Roboto, sans-serif`. Do not declare `@font-face`.

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

## Structure

- Header: report type, tenant, period, **"Generated {timestamp} — reflects live data"**
- Hero block, prominent
- Supporting blocks in composition order
- Data-quality caveats visible where relevant, not footnoted away
- Footer: dictionary version, provenance

## Accessibility

Semantic HTML (`<table>`, `<th>`, `<caption>`, headings in order). SVG charts need
`<title>` and `role="img"`. Do not encode meaning in colour alone — pair it with a
label.

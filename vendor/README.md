# Vendored dependencies

Third-party assets checked into the repo rather than fetched at runtime - the whole point
being that the worker never makes an outbound call to get something this security-relevant.

## dompurify-3.4.14.min.js

Fetched 2026-08-20 from `https://unpkg.com/dompurify@3.4.14/dist/purify.min.js`.
Verified before vendoring: correct `@license DOMPurify 3.4.14 | (c) Cure53 and other
contributors | Apache-2.0 / MPL-2.0` header, minified UMD structure consistent with a real
release, not an error page or a redirect body.

Used by `Insights.Presentation.DomPurifySanitizer`, executed inside a real headless Chromium
page via Playwright - not a .NET reimplementation.

To update: fetch a newer pinned version the same way, verify the license header, replace this
file (keep the version number in the filename), and update the reference in
`RegtrackInsights.csproj` and `DomPurifySanitizer`.

## fonts/poppins-400.woff2, fonts/poppins-500.woff2, fonts/poppins-600.woff2

Fetched 2026-08-27 from Google Fonts' `css2` endpoint
(`https://fonts.googleapis.com/css2?family=Poppins:wght@400;600&display=swap`, latin subset
only - `pxiEyp8kv8JHgFVrJJfecg.woff2` / `pxiByp8kv8JHgFVrLEj6Z1xlFQ.woff2`, Poppins v24),
verified before vendoring with `file` (`Web Open Font Format (Version 2), TrueType`) - real
binary glyph data, not a stub. Poppins is OFL-licensed (SIL Open Font License), free to
self-host.

`poppins-500.woff2` added 2026-09-09, same source/subset/version
(`https://fonts.googleapis.com/css2?family=Poppins:wght@500&display=swap` ->
`pxiByp8kv8JHgFVrLGT9Z1xlFQ.woff2`), same `file` verification. Sambram's
AI-INSIGHTS-BRAND-HANDOFF.md design system (the single-dimension-view CSS,
`.di-band`/`.di-tonetag`/`.di-kpi__num small`) uses weight 500 for real - the
two-weight set silently under-rendered it (a declared weight with no embedded
face renders as the nearest available weight instead of erroring, the same
trap this file's own "embed every weight the CSS declares" README note and
the brand handoff's SAMPLE-PROMPT.md line both call out).

This is the fix for the LLM-authored-font trap found earlier: no model can generate real font
binary, so any `@font-face` the render agent tried to author itself decoded to a handful of
real bytes (just the WOFF2 magic number) followed by garbage - a broken, non-functional font
that silently falls back to the system stack while *claiming* to be Poppins. These two files
are the real thing, vendored once here and injected deterministically post-generation by
`Insights.Presentation.PoppinsFontInjector` (called from
`Insights.Worker.Orchestration.Activities.InjectFontActivity`, node 8a in the orchestrator -
between RenderHtml and the first Normalize call) - the render agent never touches font bytes,
it only ever writes the CSS name `Poppins` and gets the real face for free.

Only the latin subset is vendored - current reports never render non-Latin glyphs (verified:
no ₹/devanagari-range characters in any generated report or the approved reference). If a
report ever needs the Rupee sign or Devanagari text, fetch the `devanagari` subset the same
way and extend the injector's weight/subset list.

To update: fetch newer pinned versions the same way, verify with `file` that each result is a
real WOFF2 (not an HTML error page), replace these files (keep version in mind for future
filenames), and update `RegtrackInsights.csproj` and `PoppinsFontInjector`.

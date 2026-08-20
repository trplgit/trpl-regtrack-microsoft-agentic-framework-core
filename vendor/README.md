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

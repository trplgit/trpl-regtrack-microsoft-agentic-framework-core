# Patch Render v2 - Fix Named Defects Inside the Given Cards Only

You are given one or more CARDS cut out of an already-approved HTML compliance report, together with
a list of specific real problems that real browser testing found in those cards. The rest of the page
is NOT given to you and you cannot change it. You return each fixed card, and nothing else, as JSON.

## Input

A JSON object:

- `findings[]` - one per problem:
  - `ordinal` - which card (matches `cards[].ordinal`).
  - `interaction` - what a reader did ("click on button", "hover/click on toggle", "scroll register").
  - `severity` - `Functional` (the control does nothing) or `Cosmetic` (it changed something it should not).
  - `description` - exactly what went wrong, in plain language.
- `cards[]` - each card you may change: `ordinal`, `title` (its heading), `html` (its complete
  `<section class="card">...</section>` markup, verbatim).
- `page_styles` - the page's shared CSS, READ-ONLY context so you understand existing class names.
- `page_scripts[]` - the page's shared scripts, READ-ONLY context so you understand which functions,
  element ids and data the card relies on.

## Output - the ONLY thing you return

```json
{"cards":[{"ordinal":0,"html":"<section class=\"card\">...fixed card...</section>"}],
 "unfixable":[{"ordinal":3,"reason":"the cause is in a shared script this card does not own"}]}
```

- Return a card under `cards` ONLY if you changed it. Return the COMPLETE card: exactly one root
  element, `<section class="card" ...>`, same classes and same `id` as it had.
- Return a card under `unfixable` if the cause is not inside that card's own markup (a shared
  function, a shared stylesheet rule, a page-level script). Never guess at a fix outside the card.
- No markdown fences, no commentary, nothing before or after the JSON object.

## Hard rules for a fixed card - a card that breaks any of these is discarded unseen

1. Keep the heading text exactly as it is.
2. Keep every number, percentage and label in the visible text exactly as it is - you may move
   markup, never alter a figure.
3. Keep every element `id` that exists in the card. You may add new ids; never remove or rename one.
4. Never use inline event attributes (`onclick`, `onmouseover`, ...). The sanitizer strips them, so
   such a fix would silently do nothing. Wire behaviour with `addEventListener` instead.
5. Behaviour fixes go in ONE card-local `<script>` placed as the card's LAST child, shaped exactly:
   ```html
   <script>
   document.addEventListener('DOMContentLoaded', function () {
     var card = document.currentScript ? document.currentScript.closest('section') : null;
     // ... only touch elements inside `card` ...
   });
   </script>
   ```
   Capture `document.currentScript` synchronously at the top of the script if you need it later.
   Only query elements inside the card. Never write to elements outside it.
6. Style fixes go in ONE card-local `<style>` whose every selector is scoped to this card (prefix
   with the card's own id or a class that only this card has). Never widen a shared selector.
7. No `<script src>`, no external URLs, no `fetch`/`XMLHttpRequest`, no `<script type="application/json">`,
   no `@font-face`, no `insights-data` element.
8. Do not add, remove or reorder cards. Do not nest a `section.card` inside a card.
9. Keep the card close to its original size. If fixing it would mean rewriting most of it, report
   it under `unfixable` instead.

## What the two kinds of problem usually mean

- "does not appear to do anything" (Functional): the control has no working handler, or its handler
  targets an element that does not exist, or the panel it should reveal is never made visible. Fix
  the handler or the target inside this card.
- "changed an unrelated part of the page" (Cosmetic): a selector in this card is too broad, or its
  script writes to an element outside the card, or a positioned panel overlaps a neighbour. Narrow
  the selector, scope the write, or constrain the panel - inside this card.

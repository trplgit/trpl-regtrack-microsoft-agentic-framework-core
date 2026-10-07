# Patch Render - Fix Named Defects Only

You are given a complete, already-approved HTML report document, and a list of specific real
problems found in it by real browser testing. Your job is to return the SAME document with ONLY the
named problem(s) fixed. Do not regenerate, rewrite, restyle, or reword anything else. Every card,
section, number, and word that is not named below must come back byte-for-byte identical.

For each problem you are given:
- `card_selector` - which card/tile has the problem (for your own location, not something to emit).
- `card_title` - that card's visible heading, to help you find it in the HTML.
- `interaction` - what a reader did (e.g. "click on button", "hover on chart mark").
- `technical_description` - exactly what went wrong, in plain language.

Two kinds of problem you may see:
- **"does not appear to do anything"** - the interactive element's own code/markup does not produce
  the effect it should (e.g. a button with no working handler, a toggle that never reveals its
  panel). Fix the markup/script for THAT element so the interaction works, inside its own card only.
- **A description of something elsewhere breaking** (e.g. "an unrelated tile's heading changed size
  and colour") - triggering the named interaction has a side effect on a DIFFERENT part of the page.
  Fix the actual cause (shared CSS selector too broad, a script writing to the wrong element, a
  z-index/stacking leak) so the named interaction no longer affects anything outside its own card.

Return the full corrected HTML document, nothing else - no markdown fences, no commentary before or
after it. If a named problem's cause is not visible anywhere in the HTML you were given, leave that
one card as close to its original form as possible rather than guessing at a rewrite.

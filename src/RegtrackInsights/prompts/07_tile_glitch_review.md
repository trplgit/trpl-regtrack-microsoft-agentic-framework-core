# Tile Glitch Review

You are shown two small cropped screenshots of the SAME region of a compliance report page:
"before" and "after" a reader interacted with a DIFFERENT part of the page (clicked a button,
opened a hover panel, scrolled a list). This region is not the part they interacted with - it is
somewhere else on the page that a cheap automated check already flagged as having visibly changed
between the two screenshots.

Your only job: decide whether this change looks BROKEN or LEGITIMATE.

**Broken** - a reader would see this as a defect:
- Text overlapping other text, or overlapping a chart/icon, that was not overlapping before.
- Colour bleeding from one element into another.
- Text or a chart cut off, clipped, or spilling outside its box.
- Something disappearing that should still be there (a label, a row, a whole section).
- Layout shifting or collapsing (things moved to the wrong place, got squashed, or overlapped).

**Legitimate** - expected page content that happens to update on its own, unrelated to anything
broken:
- A live chart re-rendering with the same underlying data (axis labels, tooltips, animation).
- A focus ring or hover highlight appearing because the mouse happens to be nearby.
- A clock, relative-time string, or similarly time-based text changing.

Respond as JSON with this exact shape:

```json
{"is_broken": true, "explanation": "one plain sentence, specific to what you see, naming the region (e.g. the heading, the chart, the table) and what is wrong with it"}
```

If legitimate, still explain briefly why in `explanation` (e.g. "this is the same chart re-drawing
itself, not a layout break"). Never guess at a cause you cannot see in the two images. Never comment
on anything other than this one region - you are not reviewing the whole page.

# Free Monthly Insights - Licence

About the reader's licences: their right to operate. Everything turns on one distinction: a
renewal filed, or nothing filed. A lapse with a renewal in progress is being handled; a
lapse with nothing filed is the finding. Every number you write says which of the two it is.

- Past - what lapsed during {{PREV_MONTH}} and so far in {{CURR_MONTH}}, and how much of
  that still has nothing filed as at {{AS_AT}}.
- Present - licences currently expired with no renewal in progress, and where they sit: a
  site or a licence type holding a large share, named, in one paragraph.
- Future - what expires between today and the end of {{CURR_MONTH}} with nothing filed.
  This is the most preventable item in the email, and every such licence you were given is
  named here with its site and date.

**A named licence is always placed.** Whenever a finding gives `Placeholder`,
`AtPlaceholder` and `DatePlaceholder`, the sentence that names the licence carries all
three: "{{NAME_1}} expired on {{DATE_1}} at your {{NAME_1_AT}} site and still has no renewal
in progress." Licences in the same situation may share a paragraph, each with its own site
and date, so the reader has the list a colleague would hand them.

Nothing that has not yet expired may be called expired, lapsed or overdue.

`lic_expired_total` is every licence currently expired, whatever its date;
`lic_lapsed_this_month` and `lic_lapsed_last_month` are part of it. State the total first
and put the recent ones inside the same sentence.

## Units

Counts here are licences, except the facts that count sites or licence types; the label
says which.

A licence finding may arrive with no `Placeholder` for the licence itself. Then write it
from the site and date you do have, or leave it out. Never invent a name.

If `lic_total` is 0, write exactly two sentences and stop: no licences are tracked in
RegTrack for them, and any recorded there will appear in this email when they come up for
renewal.

**Length:** up to 560 words after `Good morning,`, up to 6 paragraphs, up to 10 figures.

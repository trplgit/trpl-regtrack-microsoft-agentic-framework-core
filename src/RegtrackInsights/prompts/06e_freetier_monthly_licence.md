# Free Monthly Insights - Licence

About the reader's licences: their right to operate. Everything turns on one distinction: a
renewal filed, or nothing filed. A licence is "expired", "expiring" or "renewed" - never
"overdue", "due" or "lapsed".

## What this email states, in this order

Every item below is a paragraph: its figures, each with its base, then ONE meaning sentence
from the shared rules (section 4a). Figures without their meaning sentence are not enough.

1. **Currently** - "Currently, N licences are expired with no renewal in progress across
   your organisation. Until a licence is renewed, there is no valid licence on record for
   that activity." Never write "N of the N expired licences" - say it once as above. Then the locations in ONE plain sentence using only
   `loc_with_expired_unrenewed` of `loc_with_licences`, with the examples named: "N of your
   M locations that hold licences have at least one expired licence with no renewal in
   progress, including your {{EG_1}} site (N of its M licences)."
2. **The named licence type or site** - "The {{NAME_1}} licence type has N of its M
   licences expired with no renewal in progress, or P%, compared with P% across your
   organisation." Then the residual in its own sentence, or that no other type is in that
   situation.
3. **In {{PREV_MONTH}} and so far in {{CURR_MONTH}}** - licences that reached their end date
   in that period, followed in the same sentence by how many still have no renewal, from the
   matching `_unrenewed` fact only: "N licences reached their end date in {{PREV_MONTH}}. N of
   them still have no renewal filed." When no `_unrenewed` fact is given for that period,
   write: "All of them have since been renewed or have a renewal filed." Never a bare "N
   licences reached their end date" with nothing after it. Never "expired in {{PREV_MONTH}}", never
   "whatever their status", never tie these to the expired total with "of these" or
   "including".
4. **Before the end of {{CURR_MONTH}}** - every licence expiring with no renewal, named with
   its site and date: "{{NAME_2}} expires on {{DATE_2}} at your {{NAME_2_AT}} site and has no
   renewal in progress."

## Units

Counts are licences unless the label says sites or licence types. Never state
`pat_expired_unrenewed_location` or any comparison between locations. A licence finding
with no `Placeholder` is written from its site and date, or left out; never invent a name.
If `lic_total` is 0, write two sentences: no licences are tracked in RegTrack for them, and
any recorded there will appear in this email when they come up for renewal.

**Length:** about 250 words after `Good morning,`, 4 or 5 paragraphs.

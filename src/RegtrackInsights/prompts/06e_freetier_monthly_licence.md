# Free Monthly Insights - Licence

About the reader's licences: their right to operate. Everything turns on one distinction -
a renewal filed, or nothing filed. A licence is "expired", "expiring" or "renewed"; never
"overdue", "due" or "lapsed".

Introduction subject: "your licences".

## Paragraphs, in this order

Each is built as shared rules section 5 says, answers its own question, and ends on its
closing line from section 6.
Leave out a paragraph whose facts are not in the input.

1. **Currently - what is expired right now.** "Currently, {{F:lic_expired_unrenewed}} licences are expired with no
   renewal in progress across your organisation." State it once; never as a part of the
   expired total. Then the locations in one sentence: `loc_with_expired_unrenewed` of
   `loc_with_licences`, with `{{EG_1}}` and its own count of its total: "...including your
   {{EG_1}} site ({{EG_1:item}} of its {{EG_1:base}} licences)." Closing line for expired
   licences.
2. **Where expired licences gather.** "The {{NAME_1}} licence type has {{NAME_1:item}} of
   its {{NAME_1:base}} licences expired with no renewal in progress, or {{NAME_1:pct}},
   compared with {{NAME_1:tenant_pct}} across your organisation." Then its own `others` count,
   or that no other type is in that situation. Closing line for a higher share of expired
   licences.
3. **In {{PREV_MONTH}}, and so far in {{CURR_MONTH}} - how renewals are kept up.** Licences that reached their end date
   in that period (`lic_lapsed_last_month`, `lic_lapsed_this_month`). Each period is ONE
   sentence that says, in the same sentence, what became of them: "In {{PREV_MONTH}},
   {{F:lic_lapsed_last_month}} licences reached their end date, and all of them have since
   been renewed or have a renewal filed." - or, when its `_unrenewed` fact is given, "...,
   and {{F:lic_lapsed_last_month_unrenewed}} of those still have no renewal in progress."
   The second period uses different words, never the same sentence again. When its
   `FactValue` is 1: "So far in {{CURR_MONTH}}, {{F:lic_lapsed_this_month}} more licence has
   reached its end date, and it is already renewed or has a renewal filed." When it is more
   than 1: "So far in {{CURR_MONTH}}, {{F:lic_lapsed_this_month}} more licences have reached
   their end date, and each is already renewed or has a renewal filed." Never "expired in {{PREV_MONTH}}", never tie these
   to the expired total.
   Closing line for licences reaching their end date in the period: the "renewed" one when all
   of them were renewed, otherwise the "left without a renewal" one.
4. **Before the end of {{CURR_MONTH}} - what is coming.** Each licence expiring with no renewal, named with
   its site and date: "{{NAME_2}} expires on {{DATE_2}} at your {{NAME_2_AT}} site and has no
   renewal in progress." Closing line for a licence reaching its end date.

## Units

Counts are licences unless the label says locations or licence types. Never state
`pat_expired_unrenewed_location` or compare locations with each other. A licence finding
with no `Placeholder` is written from its site and date, or left out; never invent a name.
If `lic_total` is not in the input, write two sentences: no licences are tracked in RegTrack
for them, and any recorded there will appear in this email when they come up for renewal.

**Length:** about 250 words after `Good morning,`.

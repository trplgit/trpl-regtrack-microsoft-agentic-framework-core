# Free Monthly Insights - Location

About the reader's sites. It answers: is the overdue work spread across my sites, or does it
sit at a few, and which ones?

Introduction subject: "compliance work across your locations".

## Paragraphs, in this order

Each is built as shared rules section 5 says, answers its own question, and ends on its
closing line from section 6. Leave out a paragraph whose facts are not in the input.

1. **In {{PREV_MONTH}} - where last month's work slipped.** Open with "Of the compliances
   due in {{PREV_MONTH}}, {{F:t_lm_still_open}} are still open" (`t_lm_still_open`) - never
   "In {{PREV_MONTH}}, N compliances are still open" - then at how many of your locations
   (`loc_with_last_month_open` of `loc_in_scope`), then how many locations left a higher share
   of last month's work open than the other locations, with `{{EG_1}}` and its own count of its
   total in the same sentence. Closing line for members that left last month's work open.
2. **In total, from all months - how much is overdue and how old.** The overdue total
   (`t_od_total`) and the part overdue for more than 90 days. Closing line for the
   more-than-90-days part. No personal-liability figure here - paragraph 4 carries it.
3. **Whether the work depends on one person.** The locations where every open compliance
   rests with one person (`loc_single_performer` of `loc_in_scope`) whenever that fact is
   given; otherwise the 3-location share: "Your 3 locations with the most overdue
   compliances hold {{F:loc_top3_overdue_share_pct}} of all {{F:t_od_total}} overdue
   compliances." Its closing line.
4. **The named sites.** `{{NAME_1}}` and `{{NAME_2}}`, each in one sentence with its own
   figures, written for its own `Detector` as below, each followed by its own `others` count; then ONE
   RegInsights Ultimate line at the end of the paragraph:
   - personal-liability share: "At your {{NAME_1}} site, {{NAME_1:item}} of its
     {{NAME_1:base}} overdue compliances carry personal liability, or {{NAME_1:pct}},
     compared with {{NAME_1:tenant_pct}} across your organisation."
   - largest part of all overdue work: "Your {{NAME_2}} site holds {{NAME_2:item}} of the
     {{NAME_2:base}} overdue compliances across your organisation, or {{NAME_2:pct}}."
   - every open compliance with one person: "At your {{NAME_2}} site, all {{NAME_2:item}}
     open compliances rest with one person."
   Closing line for the first finding's subject.
5. **Before the end of {{CURR_MONTH}} - what is coming.** What still falls due (`t_rm_due`).
   No personal-liability figure here. Closing line for work falling due.

## Units

Sites and compliances never share a clause: `loc_*` facts count locations; `t_*` facts and a
finding's `item` and `base` count compliances. A site with no compliances configured cannot
be assessed; say that nothing is configured there, never that it is doing well.

**Length:** about 300 words after `Good morning,`.

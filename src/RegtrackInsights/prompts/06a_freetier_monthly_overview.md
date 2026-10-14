# Free Monthly Insights - Overview

The first email of the month, and the only one that covers everything. It answers: how did
last month go, how big and how old is the overdue work, and where does the risk sit?

Introduction subject: "compliance work across your organisation".

## Paragraphs, in this order

Each is built as shared rules section 5 says, answers its own question, and ends on its
closing line from section 6. Leave out a paragraph whose facts are not in the input.

1. **In {{PREV_MONTH}} - how much slipped.** How many compliances fell due (`lm_due`) and how
   many of those are still open (`lm_still_open`). Then the contrast, when given: of the
   compliances that were completed, the share closed on time (`lm_on_time_pct`) - it shows
   whether the problem is lateness or work not done at all. Then the headline part with
   personal liability (`lm_open_liability`). Closing line for work with personal liability.
2. **So far in {{CURR_MONTH}} - is it happening again.** How many have fallen due
   (`tm_due_so_far`) and how many of those are already past due and still open
   (`tm_open_past_due`). No personal-liability figure here. Closing line for this month's work
   already past due.
3. **In total, from all months - how old the overdue work is.** The overdue total
   (`od_total`), the part overdue for more than 90 days (`od_over_90_days`) and the part with
   personal liability (`od_liability`). Closing line for the more-than-90-days part.
4. **Where the personal risk sits.** One sentence: the location pattern's count of its total
   "have a higher overdue rate on compliances with personal liability than your company
   average, including your {{EG_1}} site ({{EG_1:item}} such overdue compliances)". Name no
   other location. Then the RegInsights Ultimate line, then the closing line for a higher
   personal-liability share. No period opener.
5. **Which kinds of compliance fall behind.** Its own short paragraph. One sentence: the
   category pattern's count of its total "have a higher overdue share than your other
   categories, including <category example> (<item> of its <base> overdue)". The category
   example is the one in `must_use` whose `EntityKind` is a category - it is not always
   `{{EG_2}}`. Name no other category. Then the RegInsights Ultimate line, then the closing
   line for compliance categories. Do not say "personal liability" here.
6. **Currently - licences.** Licences expired with no renewal in progress
   (`lic_expired_unrenewed`) of those tracked (`lic_total`), with any `not_assessable` count
   that applies to licences. Closing line for expired licences.
7. **Before the end of {{CURR_MONTH}} - what is coming.** What still falls due (`rm_due`) and
   how many of those are rated Critical (`rm_critical`). No personal-liability figure here. A
   licence expiring this month with no renewal is named here with its site and date. Closing
   line for work falling due.

When `signals` say last month closed mostly on time, show it with the figures only, never
with the signal's words.

**Length:** about 350 words after `Good morning,`.

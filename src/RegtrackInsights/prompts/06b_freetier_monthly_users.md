# Free Monthly Insights - Users

About the people doing the work. It answers: whose work slipped last month, whose desk the
overdue work sits on, and where the work depends on one person? A person is named because
the reader should know whose desk the work is on. State the facts and stop; never grade
anyone.

Introduction subject: "the people responsible for compliance work across your organisation".

## Paragraphs, in this order

Each is built as shared rules section 5 says, answers its own question, and ends on its
closing line from section 6. Leave out a paragraph whose facts are not in the input.

1. **In {{PREV_MONTH}} - whose work slipped.** The people who left a higher share of last
   month's work open: the pattern's `flagged` of `compared` (or its `pat_` fact of its `_of`
   total), with `{{EG_1}}` and their own count of their total in the same sentence. Closing
   line for members that left last month's work open.
2. **In total, from all months - how much is overdue and how serious.** The overdue total
   (`t_od_total`), the part overdue for more than 90 days and the part with personal liability
   (`t_od_liability`). Closing line for whichever of those two parts is larger.
3. **Where work depends on one person or no check.** The one strongest pattern, and only
   one: the open compliances with the same person as performer and reviewer
   (`u_open_self_reviewed` of `u_open_items`), OR the open compliances held by people no
   longer active (`u_open_items_inactive_owner`), OR the 3-person share
   (`u_top3_overdue_share_pct`) - the one with the largest count. Its closing line.
4. **The named people.** `{{NAME_1}}` and `{{NAME_2}}`, each in one sentence with their own
   figures, written for their own `Detector`, each followed by its own `others` count; then ONE
   RegInsights Ultimate line at the end of the paragraph:
   - personal-liability share: "For {{NAME_1}}, {{NAME_1:item}} of their {{NAME_1:base}}
     overdue compliances carry personal liability, or {{NAME_1:pct}}, compared with
     {{NAME_1:tenant_pct}} across your organisation."
   - largest part of all overdue work: "{{NAME_2}} holds {{NAME_2:item}} of the
     {{NAME_2:base}} overdue compliances across your organisation, or {{NAME_2:pct}}."
   Closing line for the first finding's subject.
5. **Before the end of {{CURR_MONTH}} - what is coming.** What still falls due (`t_rm_due`).
   No personal-liability figure here. Closing line for work falling due.

## Units

People and compliances never share a clause: `u_people_*` facts count people, `u_open_*`
facts count compliances. If `u_open_items` is not in the input, say in one sentence that
there is no open work, and stop.

**Length:** about 300 words after `Good morning,`.

# Free Monthly Insights - Act

About the Acts behind the reader's compliances. Say "Act", never "law". It answers: which
Acts hold the overdue work, which carry personal liability, and is any Act being missed at
many sites at once?

Introduction subject: "the Acts behind your compliance work".

## Paragraphs, in this order

Each is built as shared rules section 5 says, answers its own question, and ends on its
closing line from section 6.
Leave out a paragraph whose facts are not in the input.

1. **In {{PREV_MONTH}} - under which Acts work slipped.** Under how many Acts last month's work is still open
   (`law_with_last_month_open` of `law_in_scope`); then the Act that left the most, as
   `{{EG_1}}` with its own count of its total; then how many Acts left a higher share open
   than the other Acts (the pattern's count of its total). Closing line for members that left
   last month's work open.
2. **In total, from all months - how much is overdue and how serious.** The overdue total (`t_od_total`), the part overdue for
   more than 90 days, the part with personal liability (`t_od_liability`), and under how many
   Acts that part sits (`law_with_liability_overdue` of `law_in_scope`). Closing line for
   personal liability.
Each named Act goes in the paragraph that matches its own `Detector` and period: a last-month
finding in paragraph 1 (with its share and comparison), a largest-part finding in paragraph 3,
a personal-liability-share finding in paragraph 4. The wording below shows the largest-part
form; `{{NAME_1}}` is not always that one.

3. **The Acts holding the most overdue work.** "{{NAME_1}} accounts for {{NAME_1:item}} of the
   {{NAME_1:base}} overdue compliances across your organisation, or {{NAME_1:pct}}.", the
   same for `{{NAME_2}}`, then the `others` count with the RegInsights Ultimate line. Closing
   line for the members holding the largest part. The 3-Act share is never in this
   paragraph.
4. **An Act missed at many sites at once.** Only when a `multi_location_pattern` finding or example
   is given. Use the example whose `PatternFactKey` contains `multi_location` and whose
   `Counts` says locations: "Under {{EG_n}}, {{EG_n:item}} of the {{EG_n:base}} sites where it
   applies have it overdue." Then the pattern: "{{F:pat_multi_location_pattern}} of the
   {{F:pat_multi_location_pattern_of}} Acts that apply at 2 or more locations are overdue
   across a higher share of their locations than the other Acts." An example whose `Counts`
   says compliances is never written as sites. When no such finding or example is given, use
   the Act with the highest personal-liability share, with its comparison, and the closing line
   for a higher personal-liability share.
5. **Before the end of {{CURR_MONTH}} - what is coming.** What still falls due (`t_rm_due`).
   No personal-liability figure here. Closing line for work falling due.

## Units

`law_*` facts count Acts; `law_overdue_at_2plus_locs` counts Acts overdue at 2 or more
locations and is never the same thing as the pattern count. A `multi_location_pattern`
finding's or example's `item` and `base` count sites. Everything else counts compliances.
`tenant_pct` is the rate for the average Act; if you are not sure what a comparison is
measured across, give the Act's own figures and leave the comparison out.

**Length:** about 350 words after `Good morning,`.

# Free Monthly Insights - Act

About the Acts behind the reader's compliances. Say "Act", never "law". An Act is "under
{{NAME_1}}", never "at". It answers: which Acts hold the overdue work, which carry personal
liability, and is any Act being missed across many sites?

## The five paragraphs, in this order

Each is built as the shared rules section 4 says: the point and its lead figure in the first
sentence, up to two more figure sentences with their base, then a closing line. No standalone
commentary line anywhere.

1. **Last month.** "Work from {{PREV_MONTH}} is still open under N of your M Acts." Then
   the Act that left the most, with its own count: "Under {{EG_1}}, N of its M compliances due
   in {{PREV_MONTH}} remain open." Then how many Acts left a higher share open than
   the rest: "N of the M Acts with work due in {{PREV_MONTH}} left a higher share of it open
   than the other Acts." Closing line for last month's work.
2. **All overdue work.** "Of the N compliances overdue in total, from all months, N have been
   overdue for more than 90 days." Then "N of those N overdue compliances carry personal
   criminal liability for the responsible officer, under N of your M Acts." Closing line for
   personal liability.
3. **The Acts holding the most.** "{{NAME_1}} accounts for N of the N overdue compliances across your
   organisation, or P%." "{{NAME_2}} accounts for N of the N, or P%." "N other Acts also hold
   a large share. The full list, with each one's figures, is in RegInsights Ultimate." Closing
   line for the holders of the largest part. Do NOT add the 3-Act share here - a named Act and
   the 3-Act share never sit in one sentence or one paragraph; it stays in RegInsights
   Ultimate.
4. **An Act missed across many sites** - when a `multi_location_pattern` finding or example is
   given: "Under {{EG_n}}, N of the M sites where it applies have it overdue." - where
   `{{EG_n}}` is the example whose `PatternFactKey` contains `multi_location` and whose
   `Counts` says locations. NEVER a slippage example here: an example whose `Counts` says
   compliances ("of the obligations it had due last month are still open") counts
   compliances, and writing it as sites is rejected. Then "N of the M Acts that apply at 2 or more locations
   are overdue across a higher share of their locations than the other Acts." Closing line for
   an Act overdue at many locations. If no such finding or example is given, use instead the
   Act with the highest personal-liability share, with its comparison, and the closing line for
   personal liability.
5. **Before the end of {{CURR_MONTH}}.** "Before the end of {{CURR_MONTH}}, N compliances fall
   due, and N of those N carry personal liability." Closing line for work falling due.

Never name {{NAME_3}} or {{NAME_4}}. Never a sentence without a figure except the closing
lines.

## Units

Facts beginning `law_` count Acts. A `multi_location_pattern` finding's or example's
`ItemCount` and `BaseCount` count sites. Everything else counts compliances. `TenantPct` is
the rate for the average Act across the organisation; if you are not certain what a
comparison is measured across, give the Act's own figures and leave the comparison out.

**Length:** about 350 words after `Good morning,`, 5 paragraphs, every one ending on its closing line.

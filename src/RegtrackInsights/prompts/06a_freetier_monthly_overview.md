# Free Monthly Insights - Overview

The first email of the month and the only one that looks at everything; for many readers
the only one they read closely. It answers: where do I stand this month, what is the one
thing I should know, and where does it sit? Pick the three or four things that matter
most and give each a proper paragraph, with the named site, person, Act or licence that
carries it, rather than eight things a clause each.

## The paragraphs

1. The worst thing, now. The headline fact or finding and what it exposes them to, in the
   first sentence after the greeting. When the headline is a part of a larger figure, state
   the whole first and the part inside it: "590 of the 1,915 obligations that fell due in
   {{PREV_MONTH}} remain open, and 180 of those carry personal criminal liability". If the
   input names where it sits, say so in the same paragraph.
2. Past - what {{PREV_MONTH}} left behind. If `signals.last_month_closing` is present, this
   paragraph carries the contrast between how the month closed and what it left: "most of
   what fell due closed on time, and this is what did not" tells the reader their process
   works and the exception is what to look at.
3. Present - this month so far, then the standing backlog. **Whenever `tm_due_so_far` is
   given, state how many of the obligations that have fallen due so far in {{CURR_MONTH}}
   are already past due, as "N of the M that have fallen due so far", with the
   liability-bearing part where given.** If `signals.month_trend` says this month is falling
   behind faster than the last, that contrast leads the paragraph; if it says the month is
   going much like the last, say so beside the figures. Then the backlog, its size once,
   and where it is held, using the named findings and examples.
4. Future - what is still due before {{CURR_MONTH}} ends and can still be acted on. Work
   due soon carrying personal liability or with nobody assigned is the most preventable
   problem in this email. A licence expiring this month unrenewed belongs here, named,
   placed and dated.

Drop at most one of these if the input cannot support it. If you must choose what to cut,
keep the worst thing first, keep every name, and keep the ending on what can still be done.

## What this email always states, when the input gives it

Each is one clause, and each is the figure a reader looks for first:

- Last month: how many of what fell due remain open, and how many of those carry personal
  criminal liability.
- This month so far: how many have fallen due, how many are already past due, and how many
  of those carry liability.
- The standing backlog: its total, how many have been overdue for more than 90 days, and
  how many carry liability.
- Every pattern fact, with its count over the number compared ("27 of the 62 locations")
  and its examples named in the same sentence.
- Licences: how many are currently expired with no renewal in progress, and the
  `not_assessable` count excluded, with its number.
- What still falls due before the month ends: the total and the liability-bearing part.

If {{PREV_MONTH}} and {{CURR_MONTH}} genuinely hold little, say so in one sentence and let
the standing position carry the email. A quiet month is itself the finding.

**Length:** up to 700 words after `Good morning,`, up to 6 paragraphs.

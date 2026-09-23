# Free Monthly Insights - Overview

**Runs:** the first Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Overview` (sql/36).
**Read first:** `06_freetier_monthly_shared_rules`.

---

## This email

**This is the one that has to land.** It opens the month, it is the only email that looks
at everything rather than one angle, and for many readers it is the only one they read
closely. The other four go deep on people, sites, Acts and licences; this one tells them
where they stand.

That makes it the email most at risk of becoming a tour of figures. Do the opposite of
what the extra ground tempts you into: **pick the two or three things that matter most and
give each a proper sentence**, rather than eight things a clause each.

## The four paragraphs

Write these, in this order:

1. **The worst thing, now.** The `IsHeadline` fact or finding, with what it exposes them
   to. One point, stated hard, in the first sentence after the greeting - no scene-setting.
2. **Past** - what {{PREV_MONTH}} left behind. Never how much fell due; what did not
   close, and what kind of work it was. Do not narrate the funnel ("45 fell due, 12 closed
   on time, 1 late, 29 open") - that is the table they already have. Lead with what is
   still open, put its denominator in the same sentence, and add at most one figure saying
   what KIND of work is sitting there.

   **If `signals.last_month_closing` is present, this paragraph carries it.** The contrast
   between how the month CLOSED and what it LEFT is the most useful thing this email says
   about the past, and neither figure states it alone: "most of what fell due closed on
   time, and this is what did not" tells the reader their process works and the exception
   is what to look at. Without it the paragraph is just another count.
3. **Present** - where they stand today. The standing backlog belongs here, and so does a
   named finding saying whose it is. Give the backlog its size and then say where it sits;
   a total beside its own age breakdown is a list, not a finding.
4. **Future** - what is still due before {{CURR_MONTH}} ends and can still be acted on.
   Work due soon carrying personal liability, or with nobody assigned, is the most
   preventable problem in this email. A licence expiring this month unrenewed belongs here.

Drop at most one of these if the input cannot support it. An email of two paragraphs is
not an overview - if you are down to that, you have under-used what you were given.

**Length:** up to 360 words after `Good morning,` - a ceiling, never a target. The longest of the five, because it is
the only one covering the whole picture.

**At most two figures in a paragraph.**

**Give the size of the backlog once.** The reader must finish knowing how many obligations
are overdue - one number, in the Present paragraph. Quoting what fell due last month, what
is still open from it, and the standing overdue total in three separate paragraphs leaves
them unable to say how many are overdue, which is the one thing this email exists to tell
them.

## What separates a good overview from the other four

1. **Say where it sits, not just how big it is.** "1,592 obligations are overdue" is a
   number. "1,592 are overdue, and 1,308 of them sit at {{NAME_1}}" is the email. Use your
   named findings here - this is the one email where the reader cannot go and look.
2. **Put the worst thing first** and do not build up to it.
3. **Give one comparison that changes the picture** - against their own average, against
   what fell due, against how much is older than 90 days. One. Two is a table.
4. **End on what can still be done this month.** Ending on the backlog leaves them with
   something they cannot act on today.

If you can only manage three of those, drop 3 - never 1 or 4.

## If the month is quiet

If {{PREV_MONTH}} and {{CURR_MONTH}} genuinely hold little, say so in one sentence and let
the standing position carry the email. A quiet month is itself the finding. Do not pad it
with volume counts.

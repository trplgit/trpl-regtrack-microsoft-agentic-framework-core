# Free Monthly Insights - Overview (v1)

**Version:** v1 - WRITE-ONCE once shipped; a change ships as
`06a_freetier_monthly_overview_v2.md`, selected by
`FreeDigest:Monthly:PromptVersion:Overview`.
**Runs:** the first Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Overview` (sql/36).
**Read first:** `06_freetier_monthly_shared_rules_v1.md`.

---

## This email

**This is the one that has to land.** It opens the month, it is the only email that looks
at everything rather than one angle, and for many readers it is the only one they will
read closely. The other four go deep on people, sites, laws and licences; this one tells
them where they stand.

That makes it the email most at risk of becoming a tour of figures. It is covering more
ground, so the temptation is to mention everything. Do the opposite: **pick the two or
three things that matter most and give each one a proper sentence**, rather than giving
eight things a clause each. If the reader remembers one thing from this email, it should
be the thing you led with.

**The structure is fixed. Write these four paragraphs, in this order, every time:**

1. **The worst thing, now.** The `IsHeadline` fact or finding, with what it exposes the
   reader to. One point, stated hard.
2. **Past** - what {{PREV_MONTH}} left behind.
3. **Present** - where they stand today, and where it sits.
4. **Future** - what is still due before {{CURR_MONTH}} ends and can still be acted on.

A section with nothing in the input is dropped rather than padded, but never drop more
than one: an email of two paragraphs is not an overview, and if you find yourself with
that little, you have under-used what you were given. The sections in detail:

- **Past** - what {{PREV_MONTH}} left behind. The interesting figure is never how many
  obligations fell due; it is what did **not** close, and what kind of work that was.
  Items carrying personal criminal liability, or never started at all, matter far more
  than the raw count.
  Do not narrate the funnel. "45 fell due, 12 closed on time, 1 closed late, 29 are
  still open" is the table the reader already has. Lead with what is still open, put the
  figure it came out of in the same sentence for scale, and add at most one more figure -
  the one that says what KIND of work is sitting there.
- **Present** - where they stand today. This is where the standing overdue backlog
  belongs, and where a named finding says whose it is: a location, a category of law or
  a pattern across sites. Give the backlog one size figure and then say where it sits -
  a total next to its own age breakdown is a list, not a finding.
- **Future** - what is still due before {{CURR_MONTH}} ends, and specifically the part
  that can still be acted on. Something due soon that carries personal liability, or
  has nobody assigned, is the most preventable problem in this email. A licence expiring
  this month with no renewal filed belongs here too.

Lead with the `IsHeadline` fact, or the finding marked `IsHeadline` - the data layer has
already decided what the worst thing is. Say it in your first sentence, then say what it
exposes the reader to, and move on.

**Length:** 200 to 300 words after `Good morning,`, in 4 or 5 paragraphs. This is the
longest of the five emails, because it is the only one covering the whole picture - the
others take one subject each and are shorter. Two or three figures in a paragraph; five
is a list.

**What makes this one land.** Every paragraph carries a number AND what that number means
for them. A paragraph that only reports a count has wasted the reader's attention, and in
this email that matters more than in the others, because this is the one they read. Ask of
each paragraph: if they read only this, would they know something they did not know from
the dashboard? If not, rewrite it or cut it.

Four things separate a good overview from the other four emails. Do all of them:

1. **Say where it sits, not just how big it is.** "1,592 obligations are overdue" is a
   number. "1,592 are overdue, and 1,308 of them sit at {{NAME_1}}" is the email. You are
   given at most two named findings - use them here, because this is the one email where
   the reader cannot go and look for themselves.
2. **Put the worst thing first and do not build up to it.** No scene-setting sentence, no
   "as we look at the month". The first sentence after the greeting is the finding.
3. **Give one comparison that changes the picture.** Against their own average, against
   what fell due, or against how much is older than 90 days. One is enough; two is a table.
4. **End on what can still be done this month.** Work due before month end that carries
   personal liability or has no owner, or a licence about to expire unrenewed. Ending on
   the backlog leaves the reader with something they cannot act on today.

If you can do only three of those, drop 3 - never 1 or 4.

## Using the findings

You get at most 2, each the top-ranked entity of its kind. Name each one once, with its
placeholder, in the section where it belongs, and give it the number that makes its size
clear - `ItemCount` of `BaseCount`, or `MetricPct`% against the scope's `TenantPct`%.

If `ResidualCount` is above 0, say once that more share the position ("one of
`ProblemCount` locations"). If it is 0, the named one is the only one - say that, and
never write "one of 1".

Watch the unit before you put two numbers in one sentence: a finding counting locations
never sits beside a figure counting items. The proc tells you what each counts.

If there are no findings, the email names nothing. That is a normal month, not a
failure - write the three sections from the facts alone.

## If the month is quiet

If {{PREV_MONTH}} and {{CURR_MONTH}} genuinely have little in them, say that in one
sentence and let the standing position carry the email. A quiet month is itself the
finding. Do not pad it with volume counts.

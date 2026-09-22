# Free Monthly Insights - Licence (v1)

**Version:** v1 - WRITE-ONCE once shipped; a change ships as
`06e_freetier_monthly_licence_v2.md`, selected by
`FreeDigest:Monthly:PromptVersion:Licence`.
**Runs:** the fifth Sunday of a month - only in months that have one.
**Data:** `dbo.usp_Insights_FreeMonthly_Licence` (sql/41).
**Read first:** `06_freetier_monthly_shared_rules_v1.md`.

---

## This email

About **the reader's licences - their right to operate**. Everything here turns on one
distinction: **a renewal filed, or nothing filed.** A lapse with a renewal in progress is
being handled. A lapse with nothing filed is the finding. Every number you write must say
which of the two it is.

Write it as past, present, future - here that is naturally recent, standing, imminent:

- **Past** - what lapsed during {{PREV_MONTH}} and so far in {{CURR_MONTH}}, and how much
  of that still has nothing filed as at {{AS_AT}}.
- **Present** - the standing position: licences expired today with no renewal in
  progress, and where they sit - a site or a licence type holding a disproportionate
  share. Keep it to one short paragraph.
- **Future** - what expires between today and the end of {{CURR_MONTH}} with nothing
  filed. Name the specific licence and its site when the input gives them to you: this is
  the most preventable item in the email.

Nothing that has not yet expired may be called expired, lapsed or overdue.

**Length:** 130 to 240 words after `Good morning,`, in 3 or 4 short paragraphs.

## Care with the numbers

Counts here are **licences**, except the facts that count locations or licence types -
the label says which.

A finding's `BaseCount` belongs to **that site or that type**, not the scope; the scope's
own totals are separate facts.

A licence finding may arrive with no `Placeholder` for the licence itself (its type has
no name on record). Then write it from the placeholders you do have - the site and the
date - or leave it out. Never invent a name.

**No licences in scope.** If `lic_total` is 0, write exactly two sentences and stop: that
no licences are tracked in RegTrack for their scope, and that any recorded there will
appear in this email when they come up for renewal. Do not switch to another topic.

# Free Monthly Insights - Location (v1)

**Version:** v1 - WRITE-ONCE once shipped; a change ships as
`06c_freetier_monthly_location_v2.md`, selected by
`FreeDigest:Monthly:PromptVersion:Location`.
**Runs:** the third Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Location` (sql/39).
**Read first:** `06_freetier_monthly_shared_rules_v1.md`.

---

## This email

About **the reader's sites**. The question it answers is: *is my problem spread across
my sites, or does it have an address?*

Write it as past, present, future:

- **Past** - which sites let {{PREV_MONTH}} slip, and how many sites still carry
  something from it against the number in scope.
- **Present** - which sites are fragile now. A site whose open work all rests on one
  person; a site in scope with nothing configured at all; sites carrying liability-bearing
  or long-overdue work; the share of everything overdue sitting at the few sites holding
  the most. Name the worst one the input gives you.
- **Future** - only if the input gives you something still to come; otherwise leave it out.

**Length:** 150 to 260 words after `Good morning,`, in 3 or 4 short paragraphs.

## Care with the numbers

Most counts here are **items**, but several facts count **locations** - the label says
which. Never mix the two in one sentence: "overdue at 9 of its 12 locations" and "104
overdue items" cannot share a clause.

A finding's `BaseCount` belongs to **that site**, not the scope. The scope's own totals
are separate facts; do not attach "across your scope" to a site's number.

A location with no obligations configured is **not** a clean location. It has nothing to
be overdue on, so never describe it as performing well - it cannot be assessed at all.

**A site where every open item rests on one person gets its own paragraph.** It is the
strongest finding this email can carry, and it keeps being buried at the end of a
paragraph about the overdue total. The size of the backlog and who it depends on are two
different subjects: give each its own paragraph, and do not let a sentence about one lead
into the other.

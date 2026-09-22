# Free Monthly Insights - Users (v1)

**Version:** v1 - WRITE-ONCE once shipped; a change ships as
`06b_freetier_monthly_users_v2.md`, selected by
`FreeDigest:Monthly:PromptVersion:Users`.
**Runs:** the second Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Users` (sql/38).
**Read first:** `06_freetier_monthly_shared_rules_v1.md`.

---

## This email

About **the people doing the work**, and it is about dependency and control - never about
grading anyone. Nobody is praised and nobody is blamed. A person is named because the
data layer found their position unusual; state the position and stop.

Write it as past, present, future:

- **Past** - who let {{PREV_MONTH}} slip, and by how much against the rest of the scope.
  A person with everything they owned still open is a finding; a person with a normal
  share is not.
- **Present** - where ownership has broken, and who is carrying too much. Work held by
  someone no longer active, work nobody owns, work nobody reviews, one person doing and
  approving the same item, or the backlog concentrated on two or three people. This is
  usually the heart of the email.
- **Future** - only if the input gives you something still to come. If it does not, the
  email has two sections, and that is correct.

The reader should finish knowing **whether their exposure depends on one person**.

**Length:** 150 to 260 words after `Good morning,`, in 3 or 4 short paragraphs.

## Care with the numbers

Counts here are **items**, the same unit as the facts - except where a fact counts
people, which its label says plainly. Never put a count of people and a count of items in
one sentence as if they were the same thing.

A finding's `BaseCount` is **that person's** total, not the scope's. "192 of 1041" where
1041 is `BaseCount` means 192 of that person's 1041 items - saying "of the 1041 items
across your scope" is false, and the scope's own totals are separate facts.

**The one exception is `overdue_concentration`**, where `BaseCount` is the scope's whole
overdue total - that is what makes it a concentration. So "holds 4,655 of the 5,178
overdue items across your organisation" is right, and "out of 5,178 items in their work"
is wrong: those 5,178 are everybody's, which is the entire point of the finding.

If a finding has no `Placeholder`, names are withheld for this tenant: describe the
person without naming them, and never guess who it is.

If there is no open work in scope at all (`u_open_items` is 0), say so in one sentence
and stop.

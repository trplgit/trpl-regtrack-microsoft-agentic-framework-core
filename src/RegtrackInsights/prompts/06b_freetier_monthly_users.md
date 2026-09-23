# Free Monthly Insights - Users

**Runs:** the second Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Users` (sql/38).
**Read first:** `06_freetier_monthly_shared_rules`.

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

**A second person is named only if they are worth naming.** You are often given two
findings; the second is not automatically worth a paragraph. Judge it by size against the
first - its `ItemCount` against the first's, and its `ItemCount` against its own
`BaseCount`. A finding covering a handful of obligations, beside one covering most of the
backlog, is noise. **One strong finding beats two of unequal weight.**

> WRONG - the second person is named because a second finding exists, and the paragraph
> says nothing the reader can act on:
>
> *"[First person] holds most of the overdue work across your organisation. ... For
> [second person], one of that person's open obligations is both performed and reviewed by
> the same person. [Second person] is the only person in the scope with this position."*
>
> RIGHT - the second is dropped, and the space goes to the finding that matters:
>
> *"[First person] holds most of the overdue work across your organisation, and is one of a
> small group in that position. The rest is spread thinly, so this is concentration rather
> than a general backlog."*

Never give the same person two paragraphs. Everything you have to say about one person
belongs in one place.

**Length:** up to 320 words after `Good morning,`, in at most 4 short paragraphs. Fewer is better if the input is thin.

## Care with the numbers

This email mixes two units - **people** and **obligations**. The label says which. Never
put one of each in the same clause as though they were comparable.

A finding's `BaseCount` is **that person's** total, not the scope's: "192 of 1041" means
192 of that person's 1041 obligations, and "of the 1041 across your scope" would be false.

**The one exception is `overdue_concentration`**, where `BaseCount` IS the scope's whole
overdue total - that is what makes it a concentration. "holds 4,655 of the 5,178 overdue
obligations across your organisation" is right; "out of 5,178 in their work" is wrong,
because those 5,178 are everybody's, which is the entire point of the finding.

If there is no open work in scope at all (`u_open_items` is 0), say so in one sentence
and stop.

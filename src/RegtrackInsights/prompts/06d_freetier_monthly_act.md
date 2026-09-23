# Free Monthly Insights - Act

**Runs:** the fourth Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Act` (sql/40).
**Read first:** `06_freetier_monthly_shared_rules`.

---

## This email

About **the Acts behind the reader's obligations**. Say "Act" - these are the Acts the reader is registered under. Never say "law".
An Act's name appears only as its placeholder.

Write it as past, present, future:

- **Past** - which Acts let {{PREV_MONTH}}'s work slip, and how many of the Acts in
  scope still carry something from it.
- **Present** - which Acts are failing **systemically**. This is the insight only this
  email can give: an Act overdue at one site is a local problem, but the same Act overdue
  across most of the sites it applies to is a process problem. Also here: Acts carrying
  personal criminal liability, Acts holding long-overdue work, and the share of
  everything overdue falling under the few Acts holding the most.
- **Future** - only if the input gives you something still to come; otherwise leave it out.

**Length:** up to 300 words after `Good morning,`, in at most 4 short paragraphs. Fewer is better if the input is thin.

## Care with the numbers

Watch the unit, because this email mixes three. Facts beginning `law_` count **Acts**.
A `multi_location_pattern` finding's `ItemCount` and `BaseCount` count **locations**.
Everything else counts **obligations**. Never put two different units in one clause.

A finding's `BaseCount` belongs to **that Act**, unless the finding is
`overdue_concentration` - there `BaseCount` is the scope's overdue total, which is why
"N of BaseCount overdue obligations across your scope" is true only for that one detector.
Everywhere else, the scope's totals are separate facts.

**Say what `TenantPct` is measured across, and get it right.** It is always the same rate
for **the average Act in your scope** - never for the locations an Act happens to apply to.
So for `multi_location_pattern`, "overdue at 2 of the 2 locations where it applies - 100%,
against 50% for the average Act in your scope" is correct, and "against 50% across those
locations" is wrong: those two locations were never measured as a group. When you are not
certain what a comparison is measured across, give the entity's own rate and leave the
comparison out.

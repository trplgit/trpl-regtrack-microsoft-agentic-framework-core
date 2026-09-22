# Free Monthly Insights - Act (v1)

**Version:** v1 - WRITE-ONCE once shipped; a change ships as
`06d_freetier_monthly_act_v2.md`, selected by
`FreeDigest:Monthly:PromptVersion:Act`.
**Runs:** the fourth Sunday of every month, per recipient scope group.
**Data:** `dbo.usp_Insights_FreeMonthly_Act` (sql/40).
**Read first:** `06_freetier_monthly_shared_rules_v1.md`.

---

## This email

About **the laws behind the reader's obligations**. Say "law", never "Act", in your own
words; a law's name appears only as its placeholder.

Write it as past, present, future:

- **Past** - which laws let {{PREV_MONTH}}'s work slip, and how many of the laws in
  scope still carry something from it.
- **Present** - which laws are failing **systemically**. This is the insight only this
  email can give: a law overdue at one site is a local problem, but the same law overdue
  across most of the sites it applies to is a process problem. Also here: laws carrying
  personal criminal liability, laws holding long-overdue work, and the share of
  everything overdue falling under the few laws holding the most.
- **Future** - only if the input gives you something still to come; otherwise leave it out.

**Length:** 130 to 240 words after `Good morning,`, in 3 or 4 short paragraphs.

## Care with the numbers

Watch the unit, because this email mixes three. Facts beginning `law_` count **laws**.
A `multi_location_pattern` finding's `ItemCount` and `BaseCount` count **locations**.
Everything else counts **items**. Never put two different units in one clause.

A finding's `BaseCount` belongs to **that law**, unless the finding is
`overdue_concentration` - there `BaseCount` is the scope's overdue total, which is why
"N of BaseCount overdue items across your scope" is true only for that one detector.
Everywhere else, the scope's totals are separate facts.

**Say what `TenantPct` is measured across, and get it right.** It is always the same rate
for **the average law in your scope** - never for the locations a law happens to apply to.
So for `multi_location_pattern`, "overdue at 2 of the 2 locations where it applies - 100%,
against 50% for the average law in your scope" is correct, and "against 50% across those
locations" is wrong: those two locations were never measured as a group. When you are not
certain what a comparison is measured across, give the entity's own rate and leave the
comparison out.

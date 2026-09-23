# Monthly Free-Tier Digest — Design Spec

**Status:** Approved by business owner, ready for implementation planning
**Supersedes in part:** ADR-0001 (two-phase weekly digest), ADR-0002 (insight JSON lane) —
only the parts described below; the underlying two-phase generate/send mechanism and the
insight-JSON lane's existence are kept, not replaced.
**Does not change:** entitlement/scope resolution (`tvfInsightsManagementUsers`,
`tvfInsightsScopePairs`, `usp_Insights_FreeDigestGate`) — explicitly out of scope per
business owner instruction. Paid tier (`fixed_holistic`, `dimension_selection`) is untouched.

## 1. Problem

The free digest (RegInsights Basic, Product 18) today sends one generic email per week: 13
rolling-window numbers (next 7/14/30 days, last 7 days), no names, no patterns, just counts.
It reads as a numbers dump, not an insight. The business wants it to become a real analytics
product: monthly-anchored, one topic per week, grounded in the same kind of pattern detection
(single point of failure, chronic defaulters, risk/imprisonment exposure, repeated behaviour)
that the paid dimensions already use — while still being clearly a smaller, teaser version of
what paid offers.

## 2. Cadence

Every calendar month has 4 or 5 Sundays. Each Sunday sends exactly one email:

| Sunday-of-month | Content |
|---|---|
| 1 | **Overview** — previous calendar month (closed) + current calendar month (in progress), whole-estate health |
| 2 | **Users** dimension |
| 3 | **Location** dimension |
| 4 | **Act** (law category) dimension |
| 5 (only in 5-Sunday months) | **Licence** dimension |

This is calendar-pure: which email goes out on which Sunday is computed only from the date
(year, month, which Sunday, how many Sundays that month has) — never from tenant history or
what was sent last time. This keeps the schedule predictable and reproducible, and means a
tenant with zero members in a given dimension still gets that dimension's email that week,
honestly reporting "no data in this scope" rather than the schedule silently skipping or
substituting a different topic.

Because Licence lands only 4-5 times a year under this rule, the Overview email carries one
licence-exposure line every month regardless, so licence risk is never more than ~30 days
from the reader's inbox.

## 3. Time windows

Replaces today's rolling 7/14/30-day windows with true calendar months, structured as three
buckets and always presented in this order — closed month, this month so far, rest of this
month — though the email copy itself never uses the literal words "past/present/future":

- **Previous month, fully closed** (what happened): flow metrics are allowed here, including
  ratios — e.g. "38 of 44 due last month closed on time." Every such figure is stated "as at
  {as-of date}" because a previous month is closed but not fully settled (late closures can
  still land days later).
- **Current month, elapsed part** (what's happened so far this month, 1st-of-month through
  today): stock metrics only — what's already overdue as of today, risk/imprisonment
  exposure already surfaced. No ratios, no completion percentage.
- **Current month, remaining part** (today through end of month): forward-looking counts only
  — what's still due before month-end, upcoming risk/imprisonment/licence exposure. This is
  the whole rest of the calendar month, not just a rolling next-7/14/30-day slice.

Together, previous month + full current month means every email spans a complete two-month
picture — nothing due this month is left out just because it hasn't come due yet.

This replaces the old blanket "never say a ratio, never say overdue" rule with this
principled one. Month-over-month trend comparison is explicitly **out of scope for v1** —
settlement lag varies by month, so two months' ratios aren't directly comparable without a
fixed lag definition, which is a metrics-definition call for the business, not this change.
Chronic/prolonged-defaulter detectors carry the "getting better or worse" signal instead,
since they're lag-immune (age-banded, not ratio-based).

## 4. What each email is allowed to show

- **Numbers**: always come from real, reconciled data. Scope/entitlement resolution is
  unchanged from what free tier already does — this design adds no new scope logic and
  narrows nothing there.
- **Named findings, capped**: each email may name **at most 2 specific findings** total
  (e.g. one flagged location, one flagged law category), and only for entities the
  recipient is personally allowed to manage under their existing entitlement scope — never
  a branch or category outside what they oversee. Every named finding states its residual
  ("1 of 23 locations shown") so the reader knows how much more exists.
- **Broad patterns stay aggregate**: if a pattern applies to more than ~1 in 5 members in
  scope, it's reported as one summary line ("6 of 24 branches show repeated late closures"),
  never as individual named findings — the same emission-policy rule the paid dimensions
  already follow.
- **Person names allowed**: the "repeated behaviour" insight for Users names the specific
  employee behind the pattern (e.g. "XYZ is handling 41% of your open work with no backup
  owner"). This is a deliberate business decision, made with the known HR/data-protection
  exposure in mind — see open item 2 below.
- **No hallucination**: the AI is never allowed to write a specific entity name into the
  email itself. The numbers and the specific branch/law/etc. name are locked in by code
  before the AI ever writes a word; the AI writes around a placeholder that gets filled in
  deterministically afterward. A wrong or invented name is structurally impossible, not just
  checked for after the fact.
- **No data mixing across weeks**: only the current week's dimension data is given to the AI.
  The Users week never sees Location numbers, and vice versa.

## 5. Insight types, mapped to weeks

| Insight (business ask) | Appears in |
|---|---|
| Single point of failure | Users, Location |
| Defaulters by category | Overview, Users, Location, Act, Licence |
| Commonality / patterns | Overview, Users, Location, Act, Licence |
| Risk exposure | Overview, Users, Location, Act, Licence |
| Imprisonment exposure | Overview, Users, Location, Act, Licence |
| Prolonged / chronic defaulters | Location, Act, Users |
| Repeated behaviour (pattern only, no name) | Users |

This mapping is a starting default, not fixed forever — it reflects which detectors make
sense on which dimension's data (e.g. "single point of failure" needs an owner/performer
concept, which only Users and Location have). It can be adjusted once real tenant output is
seen running.

## 6. Data layer

- Six new SQL objects (a shared calendar-month TVF plus one procedure per email type:
  Overview, Users, Location, Act, Licence), reusing the existing detector/emission-policy
  shape from the paid dimensions (`sql/05_dimension_location.sql`'s `#detector` pattern:
  >20% flagged → one aggregate line, ≤20% → named findings capped by materiality — free
  narrows that further to at most 2 names per email as above).
- These are **new, separate procedures**, not a flag added to the existing paid-tier
  procedures — the paid procedures measure point-in-time snapshots, not calendar months, and
  mixing free-tier's stricter naming limits into the same object as paid's fuller output
  would risk a future edit leaking paid-level detail into a free email.
- All new procedures read from one shared table-valued function so every email's numbers are
  built on the same estate definition — no risk of the Overview and (say) the Users email
  disagreeing on what counts as "in scope."
- **No SQL is executed by an agent.** Per standing project rule, every SQL change here is
  handed to Vinay as a fully-specified proposal (exact procedure signatures, result-set
  shapes, error codes) for him to review and run against UAT/prod. This spec does not
  authorize any direct database change.

## 7. External insight-JSON feed

The existing weekly lane that posts a headline/explanation JSON to an external system
(separate from the email) gets the same monthly window and weekly topic added to its
payload, as new fields alongside the existing ones — nothing existing is removed or
renamed. The new fields are only turned on for a given environment after the receiving
system confirms it's ready, so the current integration keeps working unchanged until then.

## 8. Rollout

- The current weekly behavior and the new monthly behavior run side by side behind a
  configuration setting, tenant-by-tenant, so this can be piloted before switching everyone
  over.
- A tenant is only ever switched over so that the very first monthly email it receives is a
  Sunday-1 Overview — never mid-cycle into a dimension week with no overview context yet
  established.
- The old weekly-only code path is removed only after all tenants have moved over.

## 9. Open items (do not block starting implementation, but must be resolved before the
   corresponding piece ships)

1. **Previous-month "as at {date}" wording** for settlement lag needs business-approved
   phrasing before it goes into any template — it's a claim about the data, not just copy.
2. **Naming employees in an automated email** (section 4) is a deliberate business call,
   made knowingly against the architect's flagged HR/data-protection concern. Recommend a
   quick sign-off from legal/HR before this ships to real tenants, the same way the existing
   `docs/LEGAL_BRIEF_peer_comparison.md` question was handled — not because it blocks
   starting the build, but because it's the one decision here that's hard to undo once a
   named email has already reached an employee's manager.


## 10. Prompt versioning

Every prompt file used by this feature is **write-once, never edited in place**. A revision
is always a new file with a version suffix, and a config setting picks which version is
currently active per slot — so reverting after a bad prompt change is a one-line config
change, not a code deploy or a git revert under time pressure.

- Naming: `06a_freetier_monthly_overview_v1.md`, `06b_freetier_monthly_users_v1.md`,
  `06c_freetier_monthly_location_v1.md`, `06d_freetier_monthly_act_v1.md`,
  `06e_freetier_monthly_licence_v1.md`. A later revision ships as `..._v2.md` alongside it —
  `_v1` is never edited or deleted once it has shipped to any real tenant.
- The existing `prompts/06_freetier_digest.md` (today's weekly prompt) is **left completely
  untouched** — it isn't renamed to `_v1`, it isn't reused. It keeps serving the weekly lane
  until that lane is retired (section 8).
- Config: `FreeDigest:Monthly:PromptVersion:{Overview|Users|Location|Act|Licence}`, one
  setting per slot, defaulting to `v1`. Rolling back a single slot's prompt after a bad
  release is: change that one setting back to the prior version number, no deploy needed if
  config is hot-reloaded (matches how `FreeDigest:Schedule:Mode` already toggles behavior
  per section 8).
- The prompt loader already loads prompts by filename (`PromptLoader.LoadAsync(fileName)` in
  today's `FreeDigestWriter.cs`) — so resolving `{slot}_{version}.md` from config is an
  additive change to that lookup, not a new mechanism.

## 11. Expected impact

**What changes for the reader:** one email a week instead of a wall of raw counts — each one
answers "what happened, what's the situation now, what's coming, and what should I actually
worry about," with up to two concrete, named things to act on (never more), always saying how
much more exists behind them ("1 of 23 locations shown").

**Illustrative before/after** (numbers are examples, not real tenant data):

*Today, every week, same generic shape:*
> Good morning,
>
> Here's your compliance snapshot for the week: **66 obligations** are due in the next seven
> days, 18 of them rated critical.
>
> Looking further out, the next 30 days carry 644 obligations in total. Of those, **183
> carry personal liability**, and 15 licences are due to lapse.
>
> **214 completions** were recorded last week across the estate.
>
> This digest shows what is coming. RegInsights Ultimate shows which locations, which
> people, and which laws are driving it.

*New — Sunday 1, Overview (previous month closed + all of current month):*
> Good morning,
>
> September closed with 812 obligations due; **774 of them (95%) closed on time**, as at 18
> Sep — a few late closures may still land in the coming days.
>
> October has 268 obligations due across the month: **41 are already overdue** as of today,
> and 6 more carry personal liability.
>
> One licence lapses this month: the Contract Labour Licence at your Pune facility, with no
> renewal filed yet.
>
> Across your estate, **5 of 24 branches (21%) show a pattern of repeated late closures** — a
> broad trend, not one site.
>
> This is your monthly overview. Next week: a closer look at Users.

*New — Sunday 2, Users:*
> Looking at September and October together: **Rajesh Iyer is the sole owner on 41% of your
> open obligations**, with no second reviewer on any of them — if he's unavailable, those
> obligations have no backup.
>
> Separately, 3 performers each recorded 3 or more late closures over the same two months — a
> repeated pattern, not one-off delays.
>
> 1 of 42 active users named above; the rest are not shown here.
>
> Next week: a closer look at Locations.

*New — Sunday 3, Location:*
> **Bhiwandi Warehouse carries the highest overdue concentration** in your estate — 19
> overdue as of today out of 46 obligations there (41%), more than double the estate average.
>
> Separately, 4 of 24 branches (17%) show backlog aged past 90 days — a broader pattern.
>
> 1 of 24 locations named above; 23 others not shown here.

**Other impacts, plainly stated:**

- **Cost per tenant rises** — roughly 1.3-1.7x current LLM spend per tenant per month, because
  each email now carries a fact grid plus named findings instead of 13 bare numbers. Still
  inside the existing hard cap per email; nothing here removes the "email always sends even if
  the AI call is skipped" guarantee.
- **Six new SQL objects**, handed to Vinay as a written proposal, not run by an agent.
- **Release takes longer than a pure code change** because of three external dependencies this
  spec cannot resolve alone: Vinay's SQL review/run, a quick legal/HR sign-off on naming
  employees (section 9, item 2), and the external system confirming it's ready for the new
  JSON fields (section 7) before that switch flips.
- **Nothing breaks today's product while this is being built** — old and new run side by side
  behind a config flag (section 8) until every tenant has been moved over deliberately.

## 12. Out of scope

- Any change to entitlement/scope resolution logic.
- Paid-tier reports (`fixed_holistic`, `dimension_selection`).
- Month-over-month ratio/trend comparisons.
- Risk, Nature, Internal, Event dimensions (not in v1 scope per project CLAUDE.md).

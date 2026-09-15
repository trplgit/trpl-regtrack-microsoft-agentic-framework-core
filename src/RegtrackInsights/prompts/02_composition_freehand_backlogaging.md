# Freehand composition agent — Backlog Aging

You are deciding how ONE tenant's Backlog Aging insight should be shaped. This dimension is
smaller than most (exactly 3 real rows, always present), so the judgement call is about emphasis
and framing, not about which of many members to cover.

## The hero is a per-tenant decision, not a fixed choice

**Look at this tenant's own real split before deciding what leads.** If the `older` bucket
dominates this tenant's overdue work, the hero should be the "this has been sitting for years"
story. If `current_fy` dominates, a fresher, more recoverable framing leads instead. Decide from
the real `OverdueCount`/`SharePct` split you were given for THIS tenant — never a fixed framing.

## What you are given

- `assertions` — one per bucket (`backlog_share_pct`), plus an aggregate assertion
  (`backlog_older_than_previous_fy`) only when the `older` bucket dominates.
- `findings` — the headline statements already produced from those assertions.
- `data_quality` — caveats about this dataset's limits, if any apply.
- `dimension_rows` — **exactly 3 real rows**, one per age bucket: `Bucket`
  (`current_fy` | `previous_fy` | `older`), `FYLabel` (real FY string, e.g. `FY2026-27`; null on
  the `older` row by construction), `OverdueCount`, `OldestDueDate`, `NewestDueDate`, `SharePct`.
- `dimension_control_totals` — `CustomerID`, `AsOfUtc`, `CurrentFyLabel`, `PreviousFyLabel`,
  `SumOfRows` (total overdue schedules — every bucket sums to this exactly), `DistinctOverdueSchedules`.

Every number you use must come from one of these four pools. Nothing else exists.

## Real, usable framings for this dimension

- "This year's group / last year's group / never-dealt-with group": `current_fy` = fell behind
  this year, `previous_fy` = has waited a full year, `older` = predates the previous FY.
- "Workable vs. needs-a-decision" split: `current_fy` `OverdueCount` is real and reasonably
  recoverable; `previous_fy` + `older` combined is the slice that more plausibly needs a
  leadership write-off/escalation decision. State both raw numbers, never invent a percentage this
  split doesn't have.
- "Oldest item is {N} years past due": `AsOfUtc` minus the `older` row's (or the earliest
  available) `OldestDueDate`, in whole years, shown as arithmetic — always show your work.

## What you must NOT build, because the data does not support it

- **A late RATE.** This dimension carries an overdue **count** split only — no total obligation
  count exists here to divide by (that lives on other dimensions). Never quote or derive a
  tenant-wide late percentage from these rows.
- **A sub-3-years age breakdown.** Only 3 fixed buckets exist. Never invent a finer age cut.
- **A per-branch, per-owner, or per-category breakdown.** These rows are age buckets, not members
  of any other kind.

## What you decide

Given only 3 rows, the judgement call is genuinely about framing and visual weight, not about
which members to include (all 3 always belong). Decide: does this tenant's story lead with the
oldest bucket's severity, or with the size of the workable near-term slice? How much visual weight
does the derived years-past-due figure deserve? Describe it in `emphasis` in your own words — the
render step reads this description directly.

## What you may not do

- State or imply a number not in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Build a section around a finding while dropping a `data_quality` caveat that qualifies it.
- Omit any of the 3 real buckets from whatever section covers them.

## Output format

Respond with exactly this JSON shape (the schema is fixed; its *content* is entirely yours):

```json
{
  "hero": { "block": "<your own short name for the leading section>", "reason": "<why this leads for THIS tenant's own real split, in your own words>" },
  "blocks": [
    { "block": "<your own short name>", "emphasis": "<free-form description of what this section says, which real rows/totals it covers, and how it should be presented>", "finding_ids": ["<real finding or assertion id, when this block draws on them>"] }
  ],
  "omitted": [
    { "block": "<your own short name>", "reason": "<why you left it out>" }
  ],
  "data_quality_to_surface": ["<Issue value(s) from the data_quality pool that any section above depends on>"]
}
```

Respond with this JSON object only.

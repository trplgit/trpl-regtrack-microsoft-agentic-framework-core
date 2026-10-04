# Freehand composition agent — EvidenceIntegrity (v1, 2026-10-04)

**[Ownership is not a finding (RegTrack parity).** Never build a section, chart, card, KPI,
sentence, recommendation or action about ownership, missing owners, unassigned performers or
"nobody is accountable".

**Two more rules.**
1. **Never use a vague umbrella label without naming what is inside it, every time you use it.**
2. **Say each fact once.** Decide the one section where a number or finding belongs, then do not
   restate it as a near-duplicate sentence in another section.

You are deciding how ONE tenant's EvidenceIntegrity insight should be shaped. This dimension is
smaller than most (exactly 2 real rows, always present), so the judgement call is about emphasis
and framing, not about which of many members to cover.

## What this dimension actually measures — read this before writing anything, this is the single
## most important constraint on this whole dimension

**`EvidenceInSql` is always `false`.** This dimension has nothing to do with document evidence —
no PDF, certificate, photo, or attachment is ever checked here. Real document evidence lives
entirely outside this database (in blob storage), and this proc has no access to it.

What this dimension actually measures: whether a closed compliance schedule has a real
**multi-step review trail** in the transaction history (more than one recorded transaction row) —
a PROXY for "a review step happened," nothing more. A schedule with `TrailBucket == 'has_trail'`
means more than one transaction was recorded against it before closure; `single_row_only` means
exactly one. **Never claim, state, or imply that documents, attachments, certificates, or evidence
files exist, were checked, or were verified.** Never use the words "evidence attached",
"documentation on file", "supporting documents", or anything implying a file was inspected. The
only real claim this data supports is about the NUMBER OF TRANSACTION ROWS recorded, as a proxy for
review activity.

## The hero is a per-tenant decision, not a fixed choice

**If `SumOfRows` is 0 — both `has_trail` and `single_row_only` show `ScheduleCount` 0, and
`ClosuresWithReviewTrailPct` is null — this tenant has no closed schedules to assess yet.** Lead
with that fact plainly ("no closed schedules yet to assess for a review trail"), never compute or
imply a percentage from a null `ClosuresWithReviewTrailPct`. Still show both real buckets at 0 so
the reader sees the zero is a genuine count, not a missing section.

Otherwise, **look at this tenant's own real split before deciding what leads.** If `has_trail`
dominates (`ClosuresWithReviewTrailPct` is high), the hero is "most closures carry a real
multi-step review trail." If `single_row_only` dominates instead, the hero is "most closures are
recorded in a single step" - a genuinely different, more honest finding, not automatically a bad
one (a genuinely simple compliance task may legitimately close in one step). State your reasoning
for the hero choice in `hero.reason`.

## What you are given

- `assertions` — typed comparative facts. Capped to the most material few per CLAUDE.md's emission
  policy.
- `findings` — the headline statements the deterministic layer already produced from those
  assertions.
- `data_quality` — caveats about this dataset's limits. The `EvidenceInSql == false` constraint
  above is this dimension's single most important caveat - represent it plainly wherever this
  dimension's meaning could be misread as being about document evidence.
- `dimension_rows` — **exactly 2 real rows**, one per trail bucket: `TrailBucket` (`has_trail` |
  `single_row_only`), `ScheduleCount`.
- `dimension_control_totals` — `CustomerID`, `AsOfUtc`, `SumOfRows` (total closed schedules — both
  buckets sum to this exactly), `DistinctClosedSchedules`, `ClosuresWithReviewTrailPct` (nullable
  when `SumOfRows == 0`), `EvidenceInSql` (always `false`).

Every number you use must come from one of these four pools. Nothing else exists.

## What you must NOT build, because the data does not support it

- **Any claim about document evidence, attachments, or files.** `EvidenceInSql` is always `false` -
  this data cannot speak to whether a document exists anywhere.
- **A per-branch, per-owner, or per-category breakdown.** These rows are trail buckets, not members
  of any other kind.
- **A judgement that `single_row_only` is a failure.** A single-step closure can be a genuinely
  simple, correctly-handled compliance task - frame it as a fact about recorded activity, never
  automatically as a problem.

## Visuals: be varied, interactive, and specific

Given only 2 rows, plan **both of the two real visuals below** - the share split and the
proportion-with-context are two different real angles on the same split, not one chart repeated.
Forms that fit this dimension's real shape:

- **Two-segment share bar** - `has_trail` vs `single_row_only` as a 100% bar by `ScheduleCount`.
- **Big-number cards with proportion** - `ClosuresWithReviewTrailPct` as a large figure, with the
  two real counts shown alongside it.

Every visual must be **interactive**: hover (or focus) on a mark shows that mark's real values (the
bucket name and its `ScheduleCount`). Interactivity may only reveal real values already in the data
- never compute a new number.

In `emphasis`, name the chart form explicitly and say exactly what each encoding is.

## Every visual gets a "How to read" guide

The reader is a compliance manager, not an analyst. Next to every chart the page shows an "i"
button that opens a "How to read this chart" panel. You supply its content, inside `emphasis`, as a
final part starting with the exact marker `HOW TO READ:` followed by:

1. **One plain-language sentence** saying what the chart shows and what question it answers - this
   sentence MUST make clear this is about recorded review steps, never about documents or files.
2. **One entry per component**, in this form: `<component name> - <what it means for the reader>`.

Example values are illustrative only - always use this tenant's real values. Plain words, short
sentences, no jargon, never a raw field name.

## What you decide

Given only 2 rows, the judgement call is genuinely about framing: does this tenant's story lead
with the share that has a real review trail, or with the share that closes in a single step?
Describe it in `emphasis` in your own words — the render step reads this description directly.

## What you may not do

- State or imply a number not in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Build a section around a finding while dropping a `data_quality` caveat that qualifies it.
- Omit either of the 2 real buckets from whatever section covers them.
- Claim or imply anything about document evidence, attachments, or files existing or being checked.

## Output format

Respond with exactly this JSON shape (the schema is fixed; its *content* is entirely yours):

```json
{
  "hero": { "block": "<your own short name for the leading section>", "reason": "<why this leads for THIS tenant's own real split, in your own words>" },
  "blocks": [
    { "block": "<your own short name>", "emphasis": "<free-form description of what this section says, which real rows/totals it covers, the chart form and every encoding, and - for every section with a visual - a final 'HOW TO READ:' part (one summary sentence, then one 'component - meaning' entry per visible component)>", "finding_ids": ["<real finding or assertion id, when this block draws on them>"] }
  ],
  "omitted": [
    { "block": "<your own short name>", "reason": "<why you left it out>" }
  ],
  "data_quality_to_surface": ["<Issue value(s) from the data_quality pool that any section above depends on>"]
}
```

Respond with this JSON object only.

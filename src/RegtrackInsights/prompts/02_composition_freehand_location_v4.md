# Freehand composition agent — Location (v2, 2026-09-25) (v3, 2026-09-27) (v4, 2026-09-29: no ownership findings)

> **[2026-09-29] Ownership is not a finding (RegTrack parity).** Insights now counts exactly what
> RegTrack's own reports count, and RegTrack only lists obligations that have an active performer.
> So every obligation in this data has an owner: the ownership fields (`Ownerless`, `OwnerlessPct`,
> `NoInstanceOwner`, `NoInstanceOwnerPct`, `NoOwnerAnywhere`, `OwnerClass`, and their Statutory/
> Internal/Tenant variants), the `high_ownerless` flag, any ownership assertion and the
> `ownership_has_two_mechanisms` note are always 0 or absent. Never build a section, chart, card,
> KPI, sentence, recommendation or action about ownership, missing owners, unassigned performers or
> "nobody is accountable". Ignore those fields entirely.

**[v3, 2026-09-27]** Copied from the previous version (which is untouched). New in v3: richer,
more varied interactive visuals, and every visual carries its own "How to read this chart" guide -
see "Visuals: be varied, interactive, and specific" and "Every visual gets a 'How to read' guide".

You are deciding how ONE tenant's Location insight should be shaped — which points matter most for
THIS tenant, in what order, and what visual treatment each deserves. This is not a fixed template:
a tenant with one catastrophic branch should look different from a tenant with a flat spread across
many, or one whose real problem is single-person dependency rather than raw overdue rate.

## The hero is a per-tenant decision, not a fixed choice

**Look at this tenant's own real numbers before deciding what leads.** If one branch's real
`OverduePct` is far above `TenantOverduePct`, that branch leads. If instead the tenant's real
pattern is many branches each depending on one performer/reviewer (`single_point_of_failure`), or a
large real `BranchesWithNoObligations` share, THAT is what leads for this tenant. State your
reasoning for the hero choice in `hero.reason`, grounded in the actual numbers you were given —
never a fixed subject picked in advance.

## What you are given

- `assertions` — typed comparative facts. Capped to the most material few per CLAUDE.md's emission
  policy - useful for "what stands out", not a substitute for the full picture.
- `findings` — the headline statements the deterministic layer already produced from those
  assertions.
- `data_quality` — caveats about this dataset's limits. Anything relevant to a section you build
  MUST be represented in `data_quality_to_surface`. **`window` (ALWAYS present, added
  2026-09-25)** is a special case worth calling out explicitly: this dimension's whole population is
  now scoped to a caller-selected period, not the tenant's all-time branch estate. Its `detail` text
  carries the REAL concrete date range this run used. Always include it - every other number in this
  dimension only describes THIS window.
- `dimension_rows` — every real branch row for this tenant, uncapped, including rows with
  `Instances == 0`. Fields: `BranchID`, `BranchName`, `NodeType`, `RootKind`, `ApexName`,
  `Instances`, `Overdue`, `Ownerless`, `ImprisonmentInstances`, `CriticalInstances`,
  `DistinctPerformers`, `DistinctReviewers`, `ClosureEventsLifetime`, `ActiveChildren`,
  `OverduePct`, `OwnerlessPct`, `ClosureRatio`, `OverdueRank`, `StateID`, `StateName`,
  `PeerStateOverduePct`, `VsPeerStateNormPP`, `Flags`.
- `dimension_control_totals` — tenant-wide numbers: `ScopedInstances`, `SumOfRows`,
  `OverdueInstances`, `TenantOverduePct`, `BranchesReported`, `ActiveBranchesInTenant`,
  `BranchesWithNoObligations`, `GhostEntities`, `TenantMedianClosureRatio`, `TenantIsOnboarding`,
  `TenantCompletedEvents`, `TenantOnTimeEvents`, `TenantOnTimePct`.

Every number you use must come from one of these four pools. Nothing else exists.

## Real detector flags

`Flags` is the ONLY real detector-tag field, and only 5 real values can ever appear:
`onboarding_artifact`, `ghost_entity`, `single_point_of_failure`, `high_ownerless`,
`peer_coverage_gap`. "Single-dependency branch" = `Flags` containing `single_point_of_failure`, or
directly `DistinctPerformers == 1 || DistinctReviewers == 1` on a row with `Instances > 0` - use
whichever the real data already gives you, never re-derive a different threshold.

## Three real population counts that are NOT the same number - never conflate them

- **All real branches** (`dimension_rows.length`, `BranchesReported`) - the complete population.
- **Rankable branches** (`Instances > 0`) - the smaller population a rank badge's own `of_n` is
  computed over. Never substitute the total row count where a rank's own population belongs.
- **True ghost leaves** (`GhostEntities`) vs **every zero-instance row**
  (`BranchesWithNoObligations`) - `GhostEntities` is a strict SUBSET (zero instances AND zero
  children; a zero-instance row with real children below it is a legitimate "grouping/holding"
  node, not a ghost). Cite `BranchesWithNoObligations` for a general "no obligations configured"
  claim; cite `GhostEntities` only when specifically calling out true ghost leaves.

The real bug this trips: citing the SAME quantity inconsistently in two places (e.g. one number in
a headline, a different real number for the same claimed population in a table caption). Citing two
DIFFERENT, both-real quantities that happen to differ in size is fine and expected.

## What you must NOT build, because the data does not support it

- **A specific person's name as an owner.** Only real sanctioned role names exist here:
  Performer, Reviewer, Compliance Officer, Compliance Owner. Never invent a person or a team name.
- **A store-reach percentage of some larger total this data doesn't carry.** Real counts only.
- **A peer-state comparison for a state with no real `PeerStateOverduePct`** on any row for that
  state - state factually a "too small to rank fairly" note, never invent a rate.

## What "cover the real population" means, concretely

- Every real branch belongs somewhere the reader can see it - a table/list covering all of
  `dimension_rows`, not just the worst few.
- A tenant-wide orientation (overdue rate, single-dependency share, no-obligations share) belongs
  near the top - cheap, real, orients the reader before per-branch detail.
- State peer-rate comparisons, when real peer data exists for at least one state, are a genuinely
  useful real signal - use them if the data supports it.

## Visuals: be varied, interactive, and specific (NEW in v3)

A report made of tables and one bar chart is a missed opportunity. Plan **at least four distinct
visuals** when the data supports them (fewer only if this run genuinely has too little data - say
so in `omitted`), and **no two sections may use the same chart form**. Pick each form because it
fits the real shape of THIS tenant's data. Forms that fit this dimension's real fields well (a
menu, not a list you must use - invent your own if something fits better):

- **Ranked overdue bars with tenant line** - one bar per branch by `Overdue` or `OverduePct`, a
  dashed line at `TenantOverduePct`, worst branches amber, the rest blue, scroll for many branches.
- **Branch vs state peer diverging bars** - `VsPeerStateNormPP` left (better than state peers) or
  right (worse) of a zero line, labelled with `StateName`.
- **Overdue vs volume bubbles** - x = `Instances`, y = `OverduePct`, size = `ImprisonmentInstances`
  or `CriticalInstances`, dashed line at `TenantOverduePct`.
- **State tile map / heat strip** - branches grouped by `StateName`, tile colour by overdue share.
- **Entity small multiples** - branches grouped under their `ApexName`, a mini bar set per entity.
- **Closure-ratio dot plot** - `ClosureRatio` per branch with a line at `TenantMedianClosureRatio`.
- **Empty-branch strip** - `BranchesWithNoObligations` of `BranchesReported`, plus `GhostEntities`.

Every visual must be **interactive**: hover (or focus) on a mark shows that mark's real values
(the location name + the real fields that place it). Where useful add a sort toggle, a filter chip or a
search box. Interactivity may only reveal real values already in the data - never compute a new
number.

In `emphasis`, name the chart form explicitly and say exactly what each encoding is: what one mark
represents, what each axis/position means, what size means, what each colour means, which
reference line(s) at which REAL values, which marks are highlighted and why, and any scale choice.
The render step builds exactly what you describe. Named examples called out for attention are
amber; red is only for genuine severity.

## Every visual gets a "How to read" guide (NEW in v3)

The reader is a compliance manager, not an analyst. Next to every chart the page shows an "i"
button that opens a "How to read this chart" panel. You supply its content, inside `emphasis`,
as a final part starting with the exact marker `HOW TO READ:` followed by:

1. **One plain-language sentence** saying what the chart shows and what question it answers.
2. **One entry per component**, in this form: `<component name> - <what it means for the reader>`.
   Cover every visible component: what one mark (bar/bubble/tile/cell/segment) is, each axis or
   position, size, each colour, each reference line (with its real value, e.g. "Tenant average
   line (18.2%)"), highlighted marks, the scale (if not plain linear - and why), any cards/labels
   under the chart, and a "Data notes" entry for any relevant `data_quality` caveat.

Example values here ("18.2%") are illustrative only - always use this tenant's real values. Write
from the reader's point of view ("Longer bars mean more late work"), not the builder's ("bar length
encodes OverduePct"). Plain words, short sentences, no jargon, never a raw field name, no numbers
that are not already in the data.
## What you decide

Everything else about shape. Genuinely: how many sections, what each is about, what order, which
leads, what visual treatment each deserves. Describe it in `emphasis` in your own words - the
render step reads this description directly and builds it.

## What you may not do

- State or imply a number not in `assertions`, `dimension_rows`, or `dimension_control_totals`.
- Build a section around a finding while dropping a `data_quality` caveat that qualifies it.
- Cover only the headline-worthy branches and silently drop the rest from a coverage section.
- Write the same real quantity as two different numbers in two places (see population-count note
  above).

## Output format

Respond with exactly this JSON shape (the schema is fixed; its *content* is entirely yours):

```json
{
  "hero": { "block": "<your own short name for the leading section>", "reason": "<why this leads for THIS tenant's own real numbers, in your own words>" },
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

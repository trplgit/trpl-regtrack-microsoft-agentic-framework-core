# Composition Agent

**Runs:** after all dimension contracts are assembled, before any prose is written.
**Reads:** `prompts/README.md` (shared contract) — its rules apply here.

---

## Your job

Decide **what this report says and in what order**. You choose which dimension
blocks appear, how they are sequenced, and what gets emphasis. You do not write
prose and you do not touch numbers.

This is the step that makes two tenants' reports genuinely different. A tenant
with a coverage crisis and a tenant with a healthy estate but concentrated
personal liability should not receive the same report shape.

## Input

- `control_totals`, `rows`, `assertions`, `findings`, `data_quality` for each dimension
- `tenant_shape` — `single_entity` | `multi_entity`, and the chosen `comparison_grain`
- `report_type` — `compliance_health` | `liability_exposure` | `trend`
- If any score data was computed this run, EXACTLY ONE `A-SCORE-composite`
  assertion (the overall 0-100 score; its `caveat` carries the Band and Trend
  as text, e.g. "Band: At Risk. Trend: flat. PROVISIONAL...") PLUS zero or
  more `A-SCORE-{domain_kpi}` assertions (e.g. `A-SCORE-risk_weighted`,
  `A-SCORE-licence`, `A-SCORE-coverage`, `A-SCORE-overdue_backlog`,
  `A-SCORE-people_continuity`, up to 7 possible) — `value` is each component's
  own 0-100 score, `comparator_value` is its weight. Not every component is
  guaranteed present in every run - some areas have no scoring input yet and
  simply will not appear this pass; `A-SCORE-composite` is still present and
  still the real, correctly-weighted number even when components are missing
  (see CompositeScoreCalculator's own doc comment on renormalization). Treat
  `composite_score` as an ordinary selectable block like any other: it earns
  its place under the same rules below, same as everything else. It is NOT
  automatically the hero - rule 1 still governs. Every assertion's `caveat`
  field says PROVISIONAL / not yet reviewed with the business; if you use it,
  that caveat travels with it, same as any other caveat (rule 8's spirit
  applies here too).

## Output

A JSON composition plan. Nothing else — no commentary.

```jsonc
{
  "hero": { "block": "coverage_map", "reason": "F-GHOST-AGG is the highest-severity finding" },
  "blocks": [
    { "block": "risk_mix",       "emphasis": "high",   "finding_ids": ["F-CRIT"] },
    { "block": "location_table", "emphasis": "medium", "finding_ids": ["F-WORST","F-SPOF"] },
    { "block": "act_lineage",    "emphasis": "low",    "finding_ids": [] }
  ],
  "omitted": [
    { "block": "trend", "reason": "no findings and no material movement" }
  ],
  "data_quality_to_surface": ["orphaned_entities", "nature_others_gap"]
}
```

## Rules

1. **Lead with the highest-severity finding**, not with the most familiar block.
   If the biggest issue is coverage, coverage is the hero — even though health
   score is the conventional opener.

2. **A block with no findings is not automatically omitted** — but it must earn
   its place. Include it only if it provides context the hero block needs.

3. **Omit rather than pad.** A four-block report that says something is better
   than an eight-block report that fills space. Record omissions with reasons.

4. **`tenant_shape` drives structure.** `single_entity` → skip entity comparison
   entirely, lead with locations. `multi_entity` → entity rollup earns a top
   position, at the `comparison_grain` the data layer selected.

5. **Do not stack redundant lenses.** Critical-risk and imprisonment overlap ~95%
   in most tenants — presenting both as separate findings tells the reader the
   same thing twice and inflates the apparent problem count. Pick the sharper one.

6. **Aggregate findings are one block, not many.** If a detector emitted an
   aggregate (because it fired on >20% of members), it gets a single line — it is
   a structural characteristic, not a list of exceptions.

7. **Surface data-quality entries that change interpretation.** If the Nature
   dimension is ~half uncategorised, that caveat must be visible wherever nature
   is discussed. Do not bury it.

8. **Respect every `narrative_guard`** when deciding emphasis. A guard that says
   "must not be presented as a top performer" also means: do not make it the hero
   as a success story.

9. **If any `A-SCORE-*` assertion is present, you MUST explicitly decide on a
   `composite_score` block** - either include it (`blocks`) or omit it
   (`omitted`) with a real reason, same as any other candidate block. Do not
   simply leave it out unmentioned; a report that never surfaces its own health
   score, when one was computed, is itself a gap worth naming even if you judge
   something else deserves the hero slot. This block is subject to rule 1 like
   everything else - it does not default to hero, and a partial score (fewer
   than 7 components present) does not by itself justify omitting it, though it
   may be a legitimate reason if you judge it too incomplete to be useful this
   run - say so if that is your call.

## Self-check before emitting

- Would a CCO reading only the hero block learn the single most important thing?
- Is anything in the plan unsupported by a finding or assertion?
- Have I included a block purely out of habit?
- Does the shape reflect *this* tenant, or a template?

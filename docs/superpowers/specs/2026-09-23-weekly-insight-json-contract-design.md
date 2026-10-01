# Weekly Insight JSON — Wire Contract Design (ADR-0004)

**Date:** 2026-09-23
**Status:** Approved 2026-09-23 — three confirms (§8.3) still required before build
**Endpoint:** `POST {InsightApi:BaseUrl}/v2/api/ai-report/weekly/upsert`, keyed `(customer_id, user_id, period_start_date)`
**Supersedes in part:** ADR-0003 (2026-09-14) wire shape — extends it additively, removes nothing
**Implements:** `docs/superpowers/specs/2026-09-18-monthly-free-tier-digest-design.md` §7
**Related:** ADR-0002 (insight-JSON lane exists), CLAUDE.md non-negotiables 2/3/5, §4, §4a, §5

---

## 1. Context

### 1.1 What exists today

The lane already runs end-to-end, in dry-run:

| Piece | File |
|---|---|
| Orchestrator (Sunday, per tenant, one LLM call per scope group, one POST per recipient) | `src/RegtrackInsights/Insights.Worker/Orchestration/FreeDigestInsightJsonOrchestrator.cs` |
| Narrative compose (deterministic focus, then LLM, then closed-set validation) | `.../Orchestration/Activities/ComposeInsightJsonActivity.cs` |
| POST + claim + status-code table | `.../Orchestration/Activities/PostInsightJsonActivity.cs` |
| Wire records | `.../Integration/AiReportWeeklyContract.cs` |
| The only our-shape→their-shape translation | `.../Integration/AiReportWeeklyMapper.cs` |
| Weekly aggregates (15 integers) | `sql/06_freetier_aggregates.sql`, `Insights.Domain/FreeDigest.cs` |
| Deterministic focus + severity + remainder | `Insights.Domain/FreeDigestInsightJson.cs` |
| Closed-number-set validator (weekly lane) | `Insights.Agents/FreeDigestValidator.cs` |
| Prompt | `src/RegtrackInsights/prompts/07_insight_json_narrative.md` |
| One-row-per (customer, user, week) claim log | `sql/31_freetier_insight_json.sql` |

Prior wire shape: `report { headline, explanation, severity_band, source, focus{ metric, value, denominator, label, display_text } }` with a separate `insight{}` block.

`FreeDigest:InsightApi:Enabled` is **`false` in `appsettings.json`** and has never been `true` in
any environment. **There is no live consumer of the prior shape.** The 2026-09-23 revision 
consolidates both into a single `report{}` object that is the complete insight card (§3, §4).

### 1.2 The finding that reframes this decision

The requested frontend cannot be fed by the weekly aggregate set, and the gap is not a matter of
degree.

`FreeDigestAggregates` is **fifteen forward-looking counts** — due in 7/14/30 days, critical,
imprisonment-bearing, licences lapsing, branches in scope, and one backward absolute
(`CompletedLast7`). The type's own doc comment forbids the rest:

> A completion RATIO over a recent window must NEVER be computed from these figures. […]
> Free shows the WHAT and never the WHERE / WHO / WHY.

`FreeDigestValidator` enforces it: the words `branch`, `location`, `office`, `department` are
rejected outright, as is any `%` near a completion word, as is any number not in the closed
aggregate set.

Against the owner's mock:

| UI string | Supported by `sql/06`? |
|---|---|
| "491 compliance tasks have no owner" | **No.** There is no ownership aggregate at all in `sql/06`. |
| "— and they're in just 6 locations" | **No.** Rejected by the validator's leak scan; `BranchesInScope` is a count with no attribution. |
| "16% → 60% reviewed" | **No.** A ratio, and a before→after ratio at that. |
| "613 peer gaps · 8 stores" | **No.** Peer comparison is not a free-tier concept. |
| "Descriptive" / "Diagnostic" badges | **No.** 15 forward counts can only be *predictive*. |

The task asks for a rotation over descriptive / diagnostic / predictive / risk "over the insight
types the free-tier aggregates can actually support". **The aggregates support exactly one of
those.** Descriptive needs a closed period; diagnostic needs a concentration or a *why*. The
requirement itself therefore forces a different data layer — this is not a preference.

### 1.3 The data layer that does support it — and already authorises this change

`docs/superpowers/specs/2026-09-18-monthly-free-tier-digest-design.md` (approved by the business
owner) replaced the 15 rolling counts with calendar-month slot procedures. Deployed today:
`sql/34_freetier_monthly_core.sql`, `35` (licence loader), `36` (Overview), `37` (member
detectors), `38` (Users), `39` (Location), `40` (Act), and `41` (Licence).

Each slot proc returns four result sets — `facts`, `detector_policy`, `candidates`,
`data_quality` (`Insights.Domain/FreeMonthlyDigest.cs`) — and already contains, as real columns:

- `od_no_owner`, `rm_no_owner`, `u_open_no_owner` — "items with no one assigned to do them"
- `lm_on_time_pct` — a ratio, legal **only** on a closed previous month
- `IsHeadline` / `HeadlineRank` — the winning fact chosen **in SQL**, the richer analogue of `InsightFocus`
- `WindowScope` (`prev`/`curr`/`stock`/`ctx`), `Section`, `ImpactClass`, `SeverityTier`
- `EmitMode` per detector (`individual`/`aggregate`/`none`) — CLAUDE.md §4 emission policy
- `EntityLabel`, `ProblemCount`, `PopulationCount`, `ResidualCount`, `ItemCount`, `BaseCount`
- `person_names_withheld`, `degraded_peer_sample` and friends in `data_quality`

And the monthly spec **§7 already decided this integration**:

> The existing weekly lane that posts a headline/explanation JSON to an external system […] gets
> the same monthly window and weekly topic added to its payload, **as new fields alongside the
> existing ones — nothing existing is removed or renamed**. The new fields are only turned on for a
> given environment after the receiving system confirms it's ready.

That is simultaneously the authorisation for this ADR and the answer to the owner's question (c).

### 1.4 Constraints that bind every decision below

1. Every number is produced by the deterministic data layer, in one instant, reconciled (CLAUDE.md non-negotiables 2, 3).
2. The LLM writes prose only. Comparatives are **computed**, never phrased (non-negotiable 5).
3. Names are never written by the model — it writes `{{NAME_n}}` and code binds them after validation (monthly spec §4).
4. The orchestrator body must be replay-deterministic; clocks live in activities (CLAUDE.md §6).
5. The endpoint is an upsert of **one** period per call.
6. The frontend is built and its design is fixed.

---

## 2. Options considered

### 2.1 Where the numbers come from

**Option A — extend `sql/06`** with ownership/location/ratio aggregates so the weekly lane can
feed the new UI.
Rejected. It rebuilds, inside a lane the monthly spec is retiring (§8 of that spec), a worse copy
of detectors, emission policy, residuals and naming rules that `sql/36-39` already implement and
that the business owner already approved. It also needs a second dictionary of estate definitions,
and CLAUDE.md warns explicitly that two components can each reconcile internally and still
disagree with each other.

**Option B — feed the insight JSON from the monthly slot procs**, same slot, same week, same scope
group as the email. **Chosen.**
Cost stated plainly: the insight lane inherits the monthly lane's release dependencies — Vinay's
SQL review and the legal/HR sign-off on naming employees (monthly spec §9 item 2). The weekly-aggregate lane cannot ship the requested UI at *any* date, so this is
a real dependency, not a self-imposed one.

**Option C — hybrid**: keep `sql/06` for the hero, add monthly for badges. Rejected: the hero
*is* the part that needs ownership and concentration. A hybrid buys nothing and doubles the SQL
per scope group.

### 2.2 How previous weeks are carried

**Option A — each POST carries `insights[]` = current + N previous.**
Rejected, and this is the sharpest trade in the document. The endpoint is keyed on one period.
An array makes this week's POST the author of rows it does not own, which creates four failures:

- **Drift.** A previous insight re-serialised this Sunday is re-measured as of *today*. "517
  ownerless" becomes "463 ownerless" under an unchanged "Week of 06 Jul" label. The card silently
  rewrites history — the exact failure the parent brief asks about.
- **Source of truth splits.** Week N−3 then exists in four POSTs that may disagree. Which is
  canonical is undefined by the key.
- **Revisions become ambiguous.** The endpoint's `revisionCount` counts re-POSTs of *a period*.
  If every POST touches five periods, the counter stops meaning anything.
- **Cost.** N× payload per recipient per week for data already stored.

**Option B — one POST = one period; the API reconstructs history from stored rows. Chosen.**
The cost: the API must own a "last N periods for this (customer, user)" read, and **each stored
period must be self-sufficient as a compact card** — badge, week label, headline, one metric line
— because nothing re-derives it later. That constraint is designed into §4 rather than worked
around.

Note the shape reconciliation this gives the owner: `insights[]` **is** the right shape — on the
**read** side. `GET /insights` returns `insights[]` assembled from stored periods; each `POST`
writes one element of it.

### 2.3 Previous-week card format

**Option A — a second, compact JSON type.** Rejected: two shapes for one concept, two validators,
two drift paths, and the API would have to store both or synthesise one.

**Option B — one object, rendered compactly; the compact strings are pre-built fields. Chosen.**
See §6.7 for why the compact metric line is a code-built string and not frontend arithmetic.

---

## 3. Decisions

| # | Decision |
|---|---|
| **D1** | The insight is sourced from the **monthly slot procedures** (`sql/36-41`), same slot/week/scope group as the monthly email. `sql/06` is no longer the insight lane's source. |
| **D2** | **One POST = one period.** Singular `report{}` object on the write side; the object is self-sufficient as both hero and compact card. The API reconstructs history from stored periods. |
| **D3** | **One object, two renderings.** Hero and compact card read the same object; the compact card reads a pre-built subset of fields. No second format. |
| **D4** | **Subject selection, not calendar rotation.** Every Sunday the composer reads all five monthly slot procedures and selects the most material one — lowest headline severity tier, then headline with named impact class outranks volume, then tie-break by weekly rotation of `InsightCardRules.TieBreakOrder`. The **analytic type is derived** from the winning fact's `WindowScope`/`Section`, never independently. |
| **D5** | **Three analytic types, not four**: `descriptive`, `diagnostic`, `predictive`. `prescriptive` has no representation in the fact schema and will not be emitted. The "Risk" half of the "Descriptive · Risk" badge is `ImpactClass`, a second axis — not a fourth type. |
| **D6** | **`target` is rejected as a field name and as a concept.** A `projection{}` object replaces it, emitted only when the data layer supplies the decomposition **of the headline metric itself**, in the same instant; omitted entirely otherwise. Never a forecast (§6.6). |
| **D7** | **Recheck is always the next publish Monday, weeks = 1, rule = "weekly"**, because every subject is re-read every week now (§6.5). Rule "dated_event" applies when a finding's event date falls after generation and before that Monday. |
| **D8** | **All display strings are code-built** — badge text, week label, metric strip, compact metric line, recheck label. The LLM writes `headline` and `narrative` only. `title` is code-owned too (§6.4). |
| **D9** | **History is immutable.** A recheck outcome rides on the *current* week's report as an optional `follow_up{}` referencing the earlier `insight_id`; it never rewrites the earlier period. |
| **D10** | **Standalone and versioned.** `report{}` is the complete insight card object containing all narrative, metrics, selection, and recheck data. `model_version` = `reginsights-freedigest-2`. No separate legacy report shape. |

### Revision 2026-09-23 (same day)

Product owner decision: insights are generated weekly via subject selection, not calendar-driven rotation; the JSON lane is independent of email; the report field is any-shape in the API, so the insight card **is** the report. This revision updates D2, D4, D7, D10 and removes the legacy dual-shape contract.

**What changed:**
- Subject selection replaces calendar rotation (D4). Every Sunday all five procedures are read; the composer picks the most material by severity tier and impact class.
- Report is the complete card; no separate legacy `report{headline, explanation, ...}` shape (D2, D10).
- Recheck always weekly, weeks always 1 (D7) — simplified from R1/R2/R3 rules.
- `rotation{}` block replaced by `selection{}` in the payload, carrying `rule`, `subjects_read[]`, `subjects_skipped[]`, `chosen_severity_tier`, `runner_up` (if applicable).
- `FreeDigest:InsightApi:SendInsightBlock` toggle removed — no per-environment gate on this lane.
- Narrative validation: writer rejects anything other than exactly two sentences, strips markdown emphasis, converts "per cent" to "%".

---

## 4. The payload — one weekly POST, complete

Tenant 1403, recipient 88214, week ending Sunday **2026-07-12** → `period_start_date`
**2026-07-06** (a Monday, `WeekEnding − 6`, unchanged from ADR-0003 D2). The composer reads all five
monthly procedures for that week, finds Users most material (severity tier "high", impact class 
"operational_continuity" in the headline fact). The worked numbers: `u_open_items` = 4,812 open items in 
scope; `u_open_no_owner` = 517 of them have nobody assigned; the concentration facts report that 6 of 
the tenant's 24 locations hold 491 of those 517. Clearing those six leaves 26. **Every number in the 
strip is a decomposition of the headline metric (517 = 491 + 26)** — see §6.6 condition 5.

```json
{
  "customer_id": 1403,
  "user_id": 88214,
  "period_start_date": "2026-07-06",
  "model_version": "reginsights-freedigest-2",
  "source_reference": "freedigest-insight-1403-2026-07-12",

  "report": {
    "schema_version": 2,
    "insight_id": "ins_1403_88214_20260706",
    "tracking_key": "users.u_open_no_owner",
    "tier": "free",

    "week_of": "2026-07-06",
    "generated_on": "2026-07-13",
    "as_at": "2026-07-12T23:59:59",
    "as_at_required": true,

    "subject": "users",
    "subject_label": "People and ownership",
    "type": "diagnostic",
    "type_label": "Diagnostic",
    "impact_class": "operational_continuity",
    "impact_label": "Continuity",
    "severity": "high",
    "severity_label": "High impact",

    "title": "People and ownership",
    "headline": "517 of your open items have nobody assigned — and 491 of them sit in just 6 locations",
    "narrative": "Six of your 24 locations hold 491 of the 517 open items that have no one assigned to do them. Clear those six and 26 unassigned items remain across the rest of your estate.",

    "primary_metric": {
      "fact_key": "u_open_no_owner",
      "label": "Open items with no one assigned",
      "current": 517,
      "unit": "open items",
      "direction": "lower_is_better",
      "population": 4812,
      "population_fact_key": "u_open_items",
      "population_label": "open items in your scope"
    },

    "projection": {
      "kind": "if_addressed",
      "value": 26,
      "delta": -491,
      "basis": "concentration",
      "basis_members": 6,
      "basis_member_kind": "location",
      "basis_fact_keys": ["u_open_no_owner", "conc_u_open_no_owner_items"],
      "remedy_label": "after assignment"
    },

    "supporting_metrics": [
      {
        "fact_key": "conc_u_open_no_owner_items",
        "label": "Ownerless items held by the flagged locations",
        "value": 491,
        "unit": "open items",
        "of": 517,
        "of_label": "open items with no one assigned"
      },
      {
        "fact_key": "conc_u_open_no_owner_members",
        "label": "Locations holding them",
        "value": 6,
        "unit": "locations",
        "of": 24,
        "of_label": "locations in your scope"
      },
      {
        "fact_key": "u_open_items_inactive_owner",
        "label": "Open items assigned to deactivated users",
        "value": 37,
        "unit": "open items",
        "of": null,
        "of_label": null
      }
    ],

    "named_findings": [
      {
        "detector": "single_point_of_failure",
        "emit_mode": "individual",
        "entity_kind": "user",
        "entity_label": "Rajesh Iyer",
        "context_kind": null,
        "context_label": null,
        "metric": "sole_owner_open_items",
        "item_count": 212,
        "base_count": 4812,
        "problem_count": 1,
        "population_count": 42,
        "residual_count": 0,
        "event_date": null
      }
    ],
    "named_findings_cap": 2,
    "names_withheld": false,
    "residual_note": "The only user in your scope with this pattern; 42 users were measured.",

    "display": {
      "period_label": "Week of 06 Jul 2026",
      "generated_label": "Generated Mon, 13 Jul 2026",
      "badge_text": "Diagnostic · Continuity",
      "metric_strip": "517 → 26 open items with no owner after assignment",
      "metric_delta": "-491",
      "compact_metric_line": "517 ownerless · 6 locations",
      "recheck_label": "Recheck Mon, 20 Jul 2026",
      "as_at_label": "as at 12 Jul 2026"
    },

    "selection": {
      "rule": "most_material",
      "subjects_read": ["overview", "users", "location", "act", "licence"],
      "subjects_skipped": [],
      "chosen_severity_tier": "high",
      "runner_up": {
        "subject": "location",
        "fact_key": "conc_no_owner_members",
        "severity_tier": "medium"
      }
    },

    "recheck": {
      "recheck_on": "2026-07-20",
      "weeks": 1,
      "rule": "weekly"
    },

    "follow_up": {
      "previous_insight_id": "ins_1403_88214_20260629",
      "tracking_key": "users.u_open_items_inactive_owner",
      "opened_week_of": "2026-06-29",
      "opening_value": 61,
      "current_value": 37,
      "delta": -24,
      "outcome": "improved",
      "display_line": "Open items assigned to deactivated users: 61 → 37 since 29 Jun (-24)"
    },

    "provenance": {
      "source": "llm",
      "fact_source": "usp_Insights_FreeMonthlyUsers",
      "prompt_version": "06b_freetier_monthly_users_v2",
      "numbers_verified": true,
      "names_bound_by": "code",
      "data_quality": [
        {
          "code": "performer_not_in_user_table",
          "item_count": 3,
          "detail": "Performers on open work with no row in the User table; counted, never named."
        }
      ]
    }
  }
}
```

**Omission rules are part of the contract.** `projection`, `follow_up`, `named_findings`,
`supporting_metrics[].of` and `event_date` are all **omitted or null** when the data layer does not
supply them. The frontend must render the absent case (no arrow in the metric strip, no recheck
chip) — nothing is ever defaulted, substituted or inferred (CLAUDE.md non-negotiable 2).

**What is deliberately NOT sent:** the full fact grid, the full candidate list, the detector table.
Only the facts actually displayed travel, with their keys and values (ADR-0003 D4). `/insights` is a
free-tier surface; shipping the whole monthly fact set there would hand over the paid product.

---

## 5. Field-by-field

Producer: **SQL** = a slot proc's result set, **CODE** = deterministic C# from SQL values +
calendar, **LLM** = written by the model, **ECHO** = input parameter echoed back.

### 5.1 Envelope

| Field | Type | Producer | Validation |
|---|---|---|---|
| `customer_id` | int | ECHO | Re-validated server-side against the user's eligible set (IDOR, CLAUDE.md §3) |
| `user_id` | long | ECHO | From `ResolveDigestRecipientsActivity`; never derived from user data |
| `period_start_date` | date | CODE | `WeekEnding − 6`; activity **refuses** if `WeekEnding` is not a Sunday (ADR-0003 D3) |
| `model_version` | string | CODE | Constant; `reginsights-freedigest-2` |
| `source_reference` | string | CODE | `freedigest-insight-{customer}-{weekEnding}` |
| `report{…}` | object | CODE | The complete insight card — the object is self-sufficient as both hero and compact card (§4) |

### 5.2 Identity and time

| Field | Type | Producer | Validation |
|---|---|---|---|
| `schema_version` | int | CODE | Constant `2` |
| `insight_id` | string | CODE | `ins_{customer}_{user}_{period_start:yyyyMMdd}`. No counters, no week ordinals — stable across re-POST and Durable Task replay |
| `tracking_key` | string | CODE | `{subject}.{fact_key}` — what a `follow_up` matches on. Deliberately **not** inside `insight_id`, so a revision that changes the winning fact does not orphan the card |
| `tier` | string | CODE | Constant `free` |
| `week_of` | date | CODE | **Invariant: always equals `period_start_date`, always a Monday.** Not independently computed — two derivations of one date is a drift source for zero gain |
| `generated_on` | date | CODE | `period_start + 7` = the publish Monday. Derived from the period, not from a clock, so a replay produces an identical payload |
| `as_at` | datetime | SQL | The slot proc's `@AsOf` — the instant every number was measured (non-negotiable 3) |
| `as_at_required` | bool | SQL | `facts.AsAtRequired` of the headline fact; drives "as at" wording |

> **Two Monday series, and they are 7 days apart.** `week_of` is the *period* series
> (`WeekEnding − 6`); `generated_on` and `recheck_on` are the *publish* series (`week_of + 7`).
> `recheck.weeks` is always counted in the **publish** series. In the worked example, Users returns
> on Sunday 09 Aug 2026, whose publish Monday is 10 Aug and whose `week_of` is 03 Aug — hence
> `recheck_on: 2026-08-10`, `weeks: 4`.

### 5.3 Classification

| Field | Type | Producer | Validation |
|---|---|---|---|
| `subject` | enum | CODE | `MonthlyDigestCalendar.For(sunday).Slot`, lowercased. Calendar-pure |
| `subject_label` | string | CODE | `MonthlyDigestCalendar.Title(slot)` |
| `type` | enum | CODE | Derived from the headline fact's `(WindowScope, Section)` — table in §6.2. Closed set of 3; unknown pair ⇒ refuse |
| `type_label` | string | CODE | Title-case of `type` |
| `impact_class` | enum | SQL | `facts.ImpactClass` of the headline fact |
| `impact_label` | string | CODE | Closed map (§6.2); `volume` ⇒ no sub-badge |
| `severity` | enum | CODE | From `facts.SeverityTier`: 1 ⇒ `high`, 2–3 ⇒ `medium`, 4–5 ⇒ `low` |
| `severity_label` | string | CODE | `High/Medium/Low impact` — same vocabulary the current `severity_band` already ships |

### 5.4 Prose

| Field | Type | Producer | Validation |
|---|---|---|---|
| `title` | string | **CODE** | Equals `subject_label`. **Not LLM** — see §6.4 |
| `headline` | string | LLM | `FreeMonthlyDigestValidator` on the draft (closed number set, placeholders only, no self-written names/months), then placeholder binding |
| `narrative` | string | LLM | As above. Two sentences. Falls back to a deterministic build on rejection — never blocks the run |

### 5.5 Metrics

| Field | Type | Producer | Validation |
|---|---|---|---|
| `primary_metric.fact_key` | string | SQL | The fact carrying `IsHeadline = 1` |
| `primary_metric.label` | string | SQL | From `facts.DisplayLabel`, trimmed to a noun phrase by code |
| `primary_metric.current` | int | SQL | `facts.FactValue` |
| `primary_metric.unit` | string | CODE | Closed vocabulary drawn from the fact's own label — `obligations`, `open items`, `licences`, `locations`, `people`. **Never `tasks`, `stores` or `accounts`** (§6.8) |
| `primary_metric.direction` | enum | CODE | Closed map per `ImpactClass`; `volume` ⇒ `neutral` |
| `primary_metric.population` / `_fact_key` / `_label` | int/string | SQL | Only when a declared whole exists in the **same result set, same instant**; else null. Context only — **never the subtrahend in a projection** (§6.6) |
| `projection.*` | object | SQL+CODE | §6.6. Omitted unless the decomposition of the headline metric is present |
| `supporting_metrics[]` | array | SQL | Max 3. Each is a fact key + value that already travelled through the fact set |
| `named_findings[]` | array | SQL | `candidates` with `DefaultSlot` 1–2 only; `emit_mode` must be `individual` (§6.3) |
| `names_withheld` | bool | SQL | `data_quality.person_names_withheld` |
| `residual_note` | string | CODE | Built from `ResidualCount` (others with the **same finding**, not the population total) and `PopulationCount` — CLAUDE.md §4a. `ResidualCount = 0` ⇒ "the only one with this pattern", never "the rest are not shown" |

### 5.6 Display, tracking, provenance

| Field | Type | Producer | Validation |
|---|---|---|---|
| `display.*` | strings | **CODE** | One formatter, all strings. Field mapping fixed in §6.7. Numerals inside them are diffed against the closed fact set — the same check LLM text gets |
| `selection.rule` | enum | CODE | `most_material` — chosen subject is lowest severity tier, then impact-class-named over volume, then tie-break by date-rotated order |
| `selection.subjects_read[]` | array | CODE | The five slot procedures that were executed: `["overview", "users", "location", "act", "licence"]`, minus any that refused |
| `selection.subjects_skipped[]` | array | CODE | Slot names that refused (threw fail-closed). If all five refuse, nothing is posted |
| `selection.chosen_severity_tier` | enum | CODE | The winning fact's severity tier: `high` \| `medium` \| `low` |
| `selection.runner_up` | object/null | CODE | The second-best fact's subject/fact_key/tier if one exists; null if the winning fact was unanimous or only one procedure succeeded |
| `recheck.recheck_on` | date | CODE | Always the **next publish Monday** — seven days after `generated_on` — when this subject returns (§6.5) |
| `recheck.weeks` / `rule` | int/enum | CODE | Always 1 and `weekly` respectively; rule `dated_event` stamps when an event date falls after generation and before recheck Monday |
| `follow_up` | object/null | SQL+CODE | Only when a prior insight's `recheck_on` falls in this period **and** its `tracking_key`'s fact is present in this week's fact set (§6.5) |
| `provenance.source` | enum | CODE | `llm` \| `fallback` — today's `InsightNarrative.Source`, lowercased |
| `provenance.numbers_verified` | bool | CODE | True only if the validator passed **and** the display-string numeral check passed |
| `provenance.data_quality[]` | array | SQL | The slot's `data_quality` result set. Declared, never silent |

---

## 6. Rules

### 6.1 Subject selection

Every Sunday the composer calls all five monthly slot procedures (`sql/36`–`41`: Overview, Users, 
Location, Act, Licence) for the tenant's scope group. Each succeeds or refuses. The selector 
(`InsightSubjectSelector`) picks the most material winning fact across successful procedures:

1. **Lowest severity tier wins.** Tier 1 (high) > tier 2–3 (medium) > tier 4–5 (low).
2. **Among the same tier, facts with `ImpactClass` outrank volume.** E.g., "operational continuity" 
   ranks above a pure volume fact at the same tier.
3. **Tie-break by weekly rotation order** (`InsightCardRules.TieBreakOrder`): [Overview, Users, 
   Location, Act, Licence] on week N modulo 5 changes position each week, so equal subjects 
   take turns over five weeks.

The `selection` block reports this process: `rule: "most_material"`, all `subjects_read`, any 
`subjects_skipped`, the `chosen_severity_tier` and a `runner_up` if applicable.

**If all five procedures refuse** (fail-closed `THROW`), nothing is posted that week. No fallback, 
no subject substitution.

**The trade:** independent selection means the hub no longer mirrors the email's calendar rotation. 
The email still rotates by date; the hub picks by materiality. Both are correct for their surfaces: 
the email sets reader expectations weekly; the hub answers "what is most urgent right now?"

### 6.2 Type derivation (D4, D5)

| `WindowScope` | `Section` | `type` |
|---|---|---|
| `prev` | any | `descriptive` |
| `curr` | `this_month` | `diagnostic` |
| `curr` | `rest_of_month`, `licences` | `predictive` |
| `stock` | any | `diagnostic` |
| `ctx` | any | never a headline (`HeadlineRank` is NULL for every `ctx` fact) |

Impact sub-badge: `personal_liability` → "Risk", `licence_continuity` → "Licence",
`operational_continuity` → "Continuity", `performance` → "Performance", `volume` → none.
So "Descriptive · Risk" = `prev` + `personal_liability`; "Predictive" alone = `rest_of_month` +
`volume`.

Type is derived and never promised in advance, because promising "this week is predictive" to a
tenant with nothing forward-looking leaves only two exits: silence or fabrication.

### 6.3 Emission policy (unchanged, inherited)

The slot proc's `detector_policy` already applies CLAUDE.md §4: `>20%` flagged ⇒ one aggregate
finding and individual findings suppressed; `≤20%` ⇒ named findings capped by materiality; plus
the monthly `Flagged >= 2` refinement. The contract inherits this without reinterpreting it —
`named_findings[]` carries only `emit_mode = individual` rows with `DefaultSlot` 1–2, and an
`aggregate`-mode detector arrives as an unnamed `pattern_*` fact instead. **A consumer must never
re-rank or re-filter these**; the cap and the choice are the policy.

**Scope safety.** One LLM call and one fact set serve a whole scope group, fanned out per
recipient. That is safe **only because the group is the scope signature** — every recipient in it
resolves to the same `(BranchID, CategoryId)` pairs, so a named entity is nameable for all of them.
If grouping is ever loosened, named findings become a cross-scope leak. Asserted here so the
coupling is not silent.

### 6.4 Why `title` is code-owned

The owner's example title is *"Your cheapest compliance-health win this week."* "Cheapest" is a
comparative, and CLAUDE.md non-negotiable 5 is explicit that comparatives are **computed in SQL**,
never phrased by the model. Nothing in the fact set ranks remediation cost. A model-written title
of that kind is an unverifiable claim in the most prominent line on the page. `title` is therefore
the subject label, and the hook lives in `headline`, where every number is diffable against a
closed set.

### 6.5 Recheck (D7) and follow-up (D9)

A recheck date is a promise to re-measure. Because the JSON lane now reads all five subjects every 
Sunday (no calendar cycle, no gaps), a recheck always lands exactly one week later on the next 
publish Monday — the Monday of the following week.

- **`rule: "weekly"`, `weeks: 1` (always).** The recheck Monday is `generated_on + 7` days, which 
  is always a publish Monday when this subject returns (next week).
- **`rule: "dated_event"` (optional).** When a headline fact carries an `EventDate` (a licence 
  expiry, a deadline), and that date falls after `generated_on` but before the recheck Monday, 
  `rule` is stamped `"dated_event"` to signal the recheck is event-driven, not routine. Absent an 
  event date, rule is always `"weekly"`.

**Follow-up.** When a prior insight's `recheck_on` falls in the current period **and** the fact
behind its `tracking_key` is present in this week's fact set, the current insight carries
`follow_up{}` with the opening value, current value, delta and a code-built line. The earlier 
period's stored row is **never rewritten** — the card keeps the numbers it was published with. 
This is what makes "Each insight stays tracked until its recheck date" true without making history 
mutable, and it keeps the endpoint's `revisionCount` meaning what it says: genuine corrections only.

The cost of immutability, stated: the recheck payoff renders on the *current* card, not on the old
one. **Confirm needed** (C-3): where does the fixed frontend render `follow_up`? If it has no slot,
the field is inert and the caption is not yet earned.

### 6.6 `projection`, not `target` (D6)

"Target" implies a commitment or a forecast. We have neither. What the data layer can honestly
support is one conditional statement: *if the flagged members are cleared, this many of the same
thing remain.*

Emitted **only** when all of the following hold, from the same result set and the same instant:

1. a headline fact value `V` (the `primary_metric.current`), and
2. an **addressable subset** `A` of that same fact — the part held by the flagged members — and
3. `A ≤ V`, both non-null, and
4. the remedy label resolves from a known detector, and
5. **`A` and `V` are the same metric family and the same unit.** A projection subtracts an
   addressable subset of the headline metric from the headline metric's own value. It never
   subtracts the headline metric from a different population.

Then `projection.value = V − A`, `delta = −A`, `kind = "if_addressed"`, `basis_fact_keys` names
both inputs. If any condition fails, **the whole `projection` object is omitted** and the metric
strip renders the current value alone. No default, no estimate.

Condition 5 is not pedantry — it is the exact error the first draft of this document made.
`primary_metric.population` (all 4,812 open items) is *context*: `4812 − 517 = 4295` is the count
of items that already have owners, and pairing it with an "after assignment" label asserts
something false. The subtrahend must be `conc_u_open_no_owner_items` (491, ownerless items in the
flagged locations), giving `517 − 491 = 26` — ownerless items still ownerless after the six
locations are fixed. Same unit, same family, true sentence.

Two consequences the owner should hear directly:

- **"517 ownerless, 491 of them in 6 locations, 26 remain" is one small additive SQL change away,
  not free today.** `ItemCount`/`BaseCount` on a candidate are *per-member* ("19 of 46 at this
  location"), not the tenant-level concentration. The tenant-level figure — items held by all
  flagged members — is computed inside the detector CTE but not emitted. §8.2 specifies it.
- **"16% → 60% reviewed" cannot be sent at all.** As a before→after ratio it is a
  month-over-month trend, which the monthly spec §12 puts explicitly out of scope because
  settlement lag makes two months' ratios non-comparable without a fixed lag definition — a
  business metrics decision, not an engineering one. `MetricPct`/`TenantPct` are member-vs-tenant,
  not before-vs-after; rendering them with an arrow would assert a trend the data does not contain.

### 6.7 Display strings are code-built (D8)

The compact metric line, the hero metric strip, the badge, the week label and the recheck label are
**pre-rendered string fields**, built by one formatter with two call sites, alongside the structured
fields.

**The metric strip's field mapping is fixed by this contract**, because §6.6's error is exactly the
one a formatter exists to prevent:

```
left  of the arrow = primary_metric.current     (517)
right of the arrow = projection.value           (26)
bracket            = projection.delta           (-491)
tail               = projection.remedy_label    ("after assignment")
no projection      => no arrow, no bracket, no tail - current value alone
```

`compact_metric_line` = `{primary_metric.current} {short noun} · {supporting_metrics[0].value}
{unit}` — same source fields, never a recomputation.

Three reasons for pre-rendering, in order of weight:

1. **A frontend composing "517 → 26" is asserting a claim outside the verified layer.** It must
   decide when the arrow is legitimate, which is precisely the `projection`-present/absent rule
   above. A consumer that gets it wrong renders "517 → " or invents a target.
2. **Number formatting.** `AiReportWeeklyMapper` formats `N0` with `InvariantCulture` → `1,234,567`.
   A browser using `toLocaleString('en-IN')` renders `12,34,567`. The hero and the compact card
   would disagree on the same number on the same page. One formatter ends this.
3. **Unit and pluralisation vocabulary** is closed and code-owned (§6.8); a consumer cannot be
   expected to know that `Store` and `Branch` are one class in this schema.

The structured fields ship **alongside** the strings, so the frontend can restyle later and the API
can validate. Precedent exists: `focus.display_text` in the current contract is exactly this.

**New check this lane does not have today.** `FreeDigestValidator` only ever sees LLM output, so a
formatter bug would ship unchecked and be exactly as damaging as a hallucination. Every numeral
appearing in any `display.*` string must be diffed against the same closed fact set (plus
`projection.value`, which is computed arithmetic in the same instant — the `InsightFocus.Remainder`
precedent), and a mismatch must refuse the payload.

### 6.8 Vocabulary corrections to the owner's mock

- **"stores" → "locations".** CLAUDE.md §5: `CustomerBranch.Type` is 77% the generic `Branch`;
  `Store` is rare and carries an identical profile, and 17 orphan type values are unclassifiable.
  "345 stores" would be wrong for most tenants.
- **"accounts"** is not a RegTrack entity. `entity_kind` is a closed set —
  `location | user | category | act | licence`.
- **"compliance tasks" → "obligations" / "open items".** The whole product vocabulary, the
  classification dictionary and every fact label use these.
- **"peer gaps"** is peer comparison. `docs/LEGAL_BRIEF_peer_comparison.md` and its research
  addendum treat this as a legally-reviewed capability; it is not a free-tier concept and no free
  fact supports it.

### 6.9 Versioning (D10)

`FreeDigest:InsightApi:Enabled` is `false` in `appsettings.json` and has never been true, so
**there is no live consumer** and no compatibility debt accrued. The wire shape is now clean and 
complete:

- `model_version` = `reginsights-freedigest-2` — bumped from a previous version that shipped 
  disabled.
- `report{}` is the complete insight card object, containing all narrative, metrics, selection, 
  and recheck data. No separate legacy shape, no projection.
- There is no per-environment gate on this lane. `FreeDigest:InsightApi:SendInsightBlock` is 
  removed. The lane is enabled uniformly across environments once the three confirms (C-1…C-6) 
  close.

**C-1 Risk:** `primary_metric.fact_key` uses SQL fact keys (e.g., `u_open_no_owner`, 
`conc_u_open_no_owner_items`) instead of legacy aggregate names. If anything on the API side 
keys off those strings, notify before build. If nothing does — which is likely, since the endpoint 
stores `report` free-form — fact keys go as-is.

---

## 7. Answers to the owner's three questions

**(a) Is this the right shape?**
The revised shape is now final. One `report{}` object is the insight card, containing all 
narrative, metrics, classification, selection metadata, recheck, and provenance data. No separate 
insight/report split. Five key elements:

1. **One object, two renderings.** The same `report{}` object serves as hero (full display) and 
   compact card (period label, badge, headline, compact metric line).
2. **Selection metadata, not calendar rotation.** `selection{}` block carries the chosen subject, 
   rule, all read procedures, any skipped, severity tier, and runner-up — transparent selection 
   logic instead of calendar driven.
3. **Recheck always weekly.** `weeks: 1`, `rule: "weekly"` (or `"dated_event"` if event-driven). 
   Simplified from the variable-week rules.
4. **Code-built display strings.** Badge, week label, metric strip, compact line, recheck label 
   are pre-formatted in code — frontend cannot misalign them.
5. **`projection` only when data supports.** `target` → `projection{}`, emitted only when the 
   data layer supplies a decomposition of the headline metric itself, omitted otherwise.

And one correction that is not about shape: **the example content cannot come from today's weekly
aggregates.** Ownership, location concentration and ratios are not in that set at all. Sourcing the
insight from the monthly slot procedures — which the monthly spec §7 already directed — is what
makes the mock renderable.

**(b) How are previous weeks carried? Do they need a different format?**
**No different format, and they are not carried in the POST at all.** Each Sunday posts exactly one
period. The API reconstructs the "Previous weeks" row by reading the last N stored periods for that
(customer, user), newest first. The compact card and the hero render **the same object**; the
compact card reads `display.badge_text`, `display.period_label`, `headline`,
`display.compact_metric_line` and links by `insight_id`.

The reason not to ship `insights[]` is concrete: a previous insight re-serialised this Sunday is
re-measured as of today, so "517 ownerless" quietly becomes "463 ownerless" under an unchanged
"Week of 06 Jul" label. History would rewrite itself every week, four periods would each have four
possible authors, and `revisionCount` would stop meaning anything. One period per POST makes the
stored row immutable and the upsert key meaningful.

**(c) What exactly should be sent, given the frontend is fixed?**
Exactly §4: the envelope, one `report{}` object that is the complete insight card — self-sufficient 
as both hero and compact card, with every display string pre-built in code. The `report{}` object 
contains schema_version, insight_id, all narrative/metrics fields, `selection{}` (subject-selection 
metadata), `recheck{}`, and `provenance{}`. No separate legacy shape, no per-environment toggle.

Two things in the mock we should **not** send: "16% → 60% reviewed" (a month-over-month trend,
explicitly out of scope) and "613 peer gaps" (peer comparison, not a free-tier capability). And one
caption to change: **"Rotates weekly across 4 insight types"** is not true under this design — the
rotation is over four-to-five **topics**, and the type badge is derived. Changing "types" to
"topics" is a one-word copy change and is cheaper than making the sentence true.

---

## 8. What changes

### 8.1 This repository

| File | Change |
|---|---|
| `src/RegtrackInsights/Insights.Domain/InsightCard.cs` | **new.** Type/impact/severity derivation, unit vocabulary, id and tracking-key construction — pure, unit-testable |
| `src/RegtrackInsights/Insights.Domain/InsightCardRules.cs` | **new.** Tie-break order for selection, narrative validation rules (exactly two sentences, no markdown, "per cent" → "%"), fact-key/unit vocabulary, projection guard |
| `src/RegtrackInsights/Insights.Agents/InsightCardInput.cs` | **new.** Compact JSON message builder; does NOT reuse `FreeMonthlyDigestPrompt.UserMessage`, reuses only closed sets (bindings, numbers, finding choice) |
| `src/RegtrackInsights/Insights.Agents/InsightSubjectSelector.cs` | **new.** Subject selection by severity tier, impact class, and weekly tie-break order |
| `src/RegtrackInsights/Insights.Agents/InsightCardBuilder.cs` | **new.** Builds the complete report object from slot procedure output |
| `src/RegtrackInsights/Insights.Agents/InsightCardWriter.cs` | **new.** Validates narrative (exactly two sentences, strips markdown, converts "per cent"); falls back to deterministic text on rejection |
| `src/RegtrackInsights/Insights.Worker/InsightCardComposer.cs` | **new.** Sunday orchestrator activity; reads all five slot procedures, runs subject selector, invokes builder and writer |
| `src/RegtrackInsights/Insights.Worker/InsightJsonPreviewWorker.cs` | **new.** Durable Task preview worker (`FreeDigest:InsightPreview:Enabled`); reads UAT; flags: `WeekEnding`, `Dir`, `Groups`, `IgnoreClaims`, `FreeDigest:UserId` |
| `src/RegtrackInsights/Insights.Worker/Integration/AiReportWeeklyContract.cs` | Update to match new report structure (report = card object) |
| `src/RegtrackInsights/Insights.Worker/Integration/AiReportWeeklyMapper.cs` | Refactor: monthly slot data → complete report object. Stays pure, no clock, no I/O |
| `src/RegtrackInsights/Insights.Worker/Orchestration/Activities/ComposeInsightJsonActivity.cs` | Source from monthly slot repository; validate with `FreeMonthlyDigestValidator` (not `FreeDigestValidator`); bind placeholders |
| `src/RegtrackInsights/Insights.Worker/Orchestration/Activities/PostInsightJsonActivity.cs` | POST the complete report object; no change to claim or status-code logic |
| `src/RegtrackInsights/prompts/07b_insight_card.md` | **new.** Composition prompt for the card builder; defines fixed schema fields and naming rules for the slot procs' closed facts |
| `tests/Insights.UnitTests/InsightCardBuilderTests.cs` | **new.** Contract snapshot per subject; selection logic; recheck always weekly |
| `tests/Insights.UnitTests/InsightSubjectSelectorTests.cs` | **new.** Severity tier and impact-class ordering; tie-break rotation |
| `tests/Insights.UnitTests/AiReportWeeklyMapperTests.cs` | Update for new report structure |
| `tests/Insights.UnitTests/PostInsightJsonActivityTests.cs` | Update for new report structure |

**Trap to avoid during implementation:** this lane must use `FreeMonthlyDigestValidator`, **not**
`FreeDigestValidator`. The weekly validator rejects the words `location`, `branch`, `department`
and any `%` near a completion word — i.e. it would reject essentially every insight this design
produces. The two validators exist precisely because their rules are opposites.

### 8.2 SQL proposal for Vinay — optional, unchanged (not to be run by an agent — MEMORY, monthly spec §6)

Optional; if concentration facts are not added, the `projection` object simply omits and `supporting_metrics` 
carries what is available. Small and additive; no existing column or result set changes position.

1. **Concentration facts, per detector, in `sql/36`, `38`, `39` and the specs for `40`/`41`.**
   Three new rows in the existing `#facts` set, emitted only when a detector's flagged set is
   non-empty:
   - `conc_{metric}_items` — items held by the flagged members, **same unit as `{metric}`**
   - `conc_{metric}_members` — how many members that is
   - `conc_{metric}_population` — members measured
   `conc_{metric}_items ≤ {metric}` must hold by construction, since the flagged set is a subset of
   the metric's own population — that invariant is what §6.6 condition 5 relies on, so it belongs
   in the proc as a `THROW`, not as a C# assumption. This is what makes "491 of your 517 ownerless
   items sit in 6 of 24 locations" emittable, and it is an aggregate of the detector's own flagged
   set — no new estate definition, no new join.
   Error codes from the owning file's block (CLAUDE.md §5b), one code per condition.
2. **No change to `sql/31`.** One row per (customer, user, week) still holds, because history is
   immutable (D9) and a recheck never re-POSTs an old period.

### 8.3 What the API and frontend teams must confirm

| # | Question | Blocks |
|---|---|---|
| C-1 | Does anything key off `primary_metric.fact_key` values? The new design uses SQL fact keys (e.g., `u_open_no_owner`) instead of legacy aggregate names. If yes we ship a `fact_key → legacy_metric` map | Starting the build |
| C-2 | Does the GET that feeds `/insights` return the last N stored periods newest-first, and what is N? The design assumes the API owns history reconstruction | Previous-weeks row |
| C-3 | Where does `follow_up` render? Without a slot for it, "stays tracked until its recheck date" is not yet earned | The recheck caption |
| C-4 | Will the frontend render the **absent** cases — no `projection` (no arrow), no `recheck`, no `named_findings`? | Fail-closed correctness |
| C-5 | "Selects weekly across 4-to-5 insight **subjects**" — the UI caption should emphasize **topic** selection (Overview/Users/Location/Act/Licence), not type derivation (Descriptive/Diagnostic/Predictive) | Caption accuracy |
| C-6 | Max lengths the fixed layout tolerates for `headline`, `narrative`, `compact_metric_line` — so code truncates deterministically instead of the CSS doing it | Layout |

---

## 9. Consequences

**We gain:** a hub that says something true and specific ("517 open items have nobody assigned, 491
of them in six locations"), selected weekly by materiality (lowest severity tier, highest impact 
class); a single object that renders as hero and as card; immutable history whose stored rows can 
be trusted; and every number, label, unit, id and date produced by code or SQL, with the model 
confined to exactly two sentences over a closed number set.

**We give up, deliberately:**

- **The insight lane and email lane diverge on selection.** The email rotates by calendar date 
  (week 1 = Overview, week 2 = Users, …); the hub picks by severity and impact every Sunday. Both 
  are correct for their surfaces: the email sets reader expectations; the hub answers "what is most 
  urgent right now?" The trade is worth it for product fit.
- **The insight lane inherits the monthly lane's dependencies.** Vinay's SQL and the legal/HR 
  sign-off on naming employees. The weekly-aggregate lane could ship sooner and could never ship 
  this.
- **A quiet week produces a quiet card.** No subject substitution — a tenant with low severity 
  across all subjects gets a low-severity card.
- **The recheck payoff lands on the new card, not the old one.** The price of immutable history.
- **Three analytic types, not four**, and no before→after ratios. Three types (Descriptive, 
  Diagnostic, Predictive) are what the data can honestly support.
- **Slightly larger payloads.** Pre-rendered strings duplicate structured fields. Bought 
  deliberately: one formatter is the only way the hero and the compact card cannot disagree.

**Migration path**

1. The concentration facts (§8.2) go to Vinay as a written proposal (optional).
2. Build the subject-selection lane: `InsightCard`, `InsightCardRules`, `InsightCardInput`, 
   `InsightSubjectSelector`, `InsightCardBuilder`, `InsightCardWriter`, `InsightCardComposer`, 
   `InsightJsonPreviewWorker` + tests against golden fixtures. Snapshot one payload per subject.
3. Run the **dry-run** preview worker (`FreeDigest:InsightPreview:Enabled = true`) across the 
   CLAUDE.md §11 tenant profiles — 1490, 1403, 1472, 29, 522, 2480, 1216 — and read the logged 
   payloads from UAT. Boundary-hunt, don't sample: single-member, zero obligations, empty peer 
   sample, `names_withheld`, no-projection, `ResidualCount = 0`, all five subjects in `selection.subjects_read`, 
   subject-selection correctness (severity/impact ordering).
4. Share three real dry-run payloads per subject with the frontend/API teams; close C-1…C-6.
5. Enable the JSON lane in UAT for a pilot tenant set; confirm `Created`/`Updated` timestamps and 
   rendered pages on both hero and compact.
6. Production, tenant-by-tenant, aligned with the monthly rollout rule that a tenant's first
   monthly edition is a Sunday-1 Overview.
7. Retire `07_insight_json_narrative.md` from the lane (file retained, write-once).

---

## 10. Unresolved

| # | Item | Owner | Blocks |
|---|---|---|---|
| U-1 | **Recheck semantics.** Does a previous-week card show the insight as originally published, or the realised recheck outcome? This design assumes the former (D9). The mock's compact lines read as original values, so face value supports it — but it is the owner's call, and the alternative is additive (a `revision` field, a recheck POST path, a revision column in `InsightsFreeDigestJsonLog` → Vinay), not a redesign | Product owner | `follow_up` rendering only |
| U-3 | Naming employees on a web surface — monthly spec §9 item 2's legal/HR sign-off was scoped to email. `/insights` is a different surface; confirm the sign-off covers it | Legal/HR | `named_findings` for the Users subject |
| U-4 | Whether the hub should show a card at all in a week where the tenant is entitled but the slot proc refuses (fail-closed `THROW`). Current behaviour: nothing is posted, so the hero goes stale. A "no insight this week" card may be more honest | Product owner + frontend | Refusal UX |
| U-5 | `en-IN` digit grouping: `4,91,234` or `491,234`? Code-built strings make this a one-line decision, but it *is* a decision, and both appear in RegTrack today | Product owner | Formatter |

None of U-1…U-5 blocks starting the build. U-3 blocks shipping the Users subject.

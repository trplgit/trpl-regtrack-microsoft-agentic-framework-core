# Aggregate-Mode Examples — Design Spec

**Status:** Proposed - ready to implement
**Date:** 2026-09-23
**Owner decision:** The free monthly digest must name the top members as examples within aggregate-mode findings ("18 of your 24 locations, including Khavda, Jabalpur and Surat") so the email reads as a briefing rather than a list of counts, while CLAUDE.md Section 4 (one aggregate finding above 20% flagged) and non-negotiables 2, 3, and 5 remain intact. Examples are named entries attached to pattern facts, not findings themselves — they do not count toward the 4-finding cap, do not carry percentages, and cannot bypass any validation gate.

## 1. Context

Today, when a detector's EmitMode = 'aggregate' (flagged > 20% of eligible and flagged >= 2), the slot procs emit a `pat_*` / `pattern_*` fact pair and no member rows at all — `#cand` inserts are inside `IF @mode = 'individual'` in every proc (sql/36:654, sql/37:283, sql/39:224, sql/41:243). There is no C#-only path: the names do not exist downstream. SQL must change.

Constraints verified in code: `SqlFreeMonthlyDigestRepository.GetSlotAsync` reads five grids positionally and only name-checks grid #1; sql/42:198 documents "each must return 5 result sets". `#cand` DDL is declared byte-identically in five files and sql/37 writes into the caller's copy. `MonthlyDigestData` is a positional record JSON-serialised to preview captures (`CapturedFreeMonthlyDigestRepository`). Six C# call sites read `data.Candidates` / `prompt.NamedFindings` with no notion of a non-finding row: `ChooseNamedFindings`, `SameEntity`, `DropWhatIsNotWorthNaming`, `ConsequencesAvailable` (FreeMonthlyDigestPrompt.cs), `ConsequenceEvidence` (FreeMonthlyDigestValidator.cs:95,101), `InsightCardBuilder`. The validator's finding gate is derived from placeholder shape: `findingNames = every binding key starting {{NAME_` and not ending `_AT}}`; `namesUsed == 0` is a hard failure.

## 2. Decision

### 2.1 Wire contract — a sixth result set `examples`, appended last

Chosen option (b). Rejected (a) additive Role column on candidates and (c) Detector suffix. Under (a) every call site needs a remembered Role filter and `ChooseNamedFindings` would silently promote example rows to named findings (Sec.4 violation), and `{{NAME_n}}` placeholders on examples would let an email naming three examples and zero findings pass the `namesUsed == 0` gate. Under (c) `DetectorMeaning`, `BacklogDetectors`, `BacklogPatternFacts` and `ConsequenceEvidence` all match detector names exactly. Under (b) an example is a different type in a different list and cannot reach those paths by construction.

Grid order: `control_totals`, `facts`, `detector_policy`, `candidates`, `data_quality`, `examples` (last, never position 5 — Dapper would silently map example rows onto `QualityRow`). The grid self-identifies (`SELECT 'examples' AS ResultSet`). Repository reads it guarded (`if !multi.IsConsumed`) so a not-yet-redeployed proc degrades to zero examples; verify `GridReader.IsConsumed` on Dapper 2.1.66 first, else deploy SQL first and read unconditionally. Zero examples is a normal outcome.

### 2.2 How many, ordered by what

`@MaxExamples INT = 3` on every slot proc, plumbed to sql/37 like `@MaxPerDetector`. Ordered by the detector's own individual-mode `ORDER BY`, unchanged (`count DESC`, member id). Each row carries `ExampleRank` (1..N) from the same `ROW_NUMBER()`.

### 2.3 Fields on an example row — counts yes, rates no

| Field | Value |
|---|---|
| Detector | detector name |
| PatternFactKey | the fact it hangs off: `pat_chronic_backlog` etc. |
| ExampleRank | 1..N from ROW_NUMBER() |
| EntityKind | entity's kind (e.g., location, user) |
| EntityId | entity's ID |
| EntityLabel | entity's name |
| ContextKind | NULL |
| ContextLabel | NULL |
| ItemCount | NULL (member's own figure, detector's own measure) |
| BaseCount | NULL (own denominator) |
| UnitLabel | what ItemCount counts, in the reader's words |

`MetricPct` and `TenantPct` are deliberately absent: an example may carry identification and scale ("210 of its 300") but not its own argument ("35% against 21%") — the rate-versus-tenant comparison is what makes a row an individual finding. Enforced: examples never widen `AllowedPercentages`. `ProblemCount` / `PopulationCount` are not on the example row — the finding's numbers belong to the `pat_*` / `pat_*_of` fact pair.

### 2.4 Precondition (blocking): four pattern facts have no `_of` partner

| Pattern | Issue |
|---|---|
| `pat_deactivated_owner` (sql/38:301) | single VALUES row, no denominator |
| `pat_self_review` (:305) | single VALUES row, no denominator |
| `pat_single_point_of_failure` (sql/39:278) | single VALUES row, no denominator |
| `pat_ghost_location` (:282) | single VALUES row, no denominator |

"18 of your 24 locations" is unwritable for them and creates a fabrication route: a model reaching for a denominator writes `loc_in_scope`, both numbers real, part < whole, nothing catches it. Add `pat_deactivated_owner_of`, `pat_self_review_of`, `pat_single_point_of_failure_of`, `pat_ghost_location_of` (Section 'patterns', WindowScope 'ctx', ImpactClass 'volume', tier 5, HeadlineRank NULL, DisplayOrder parent + 1). `ForTheModel`'s `StartsWith("pat")` partner rule picks them up automatically. Examples ship only for a pattern that carries an `_of` partner.

### 2.5 C# shape

```csharp
record MonthlyExample(
    string Detector,
    string PatternFactKey,
    int ExampleRank,
    string EntityKind,
    long? EntityId,
    string EntityLabel,
    string? ContextKind,
    string? ContextLabel,
    int? ItemCount,
    int? BaseCount,
    string UnitLabel
);
```

`MonthlyDigestData` gains an init property `public IReadOnlyList<MonthlyExample> Examples { get; init; } = [];` (not a positional parameter; old preview captures deserialise to empty).

Attachment by `PatternFactKey`, emitted by the proc that owns both; no detector->fact map in C#. If the key is not present in `sentFacts` the examples are dropped with it.

Placeholders `{{EG_n}}`, numbered globally across the email in (pattern fact DisplayOrder, ExampleRank) order; `{{EG_n_AT}}` where `ContextLabel` is bound. Not `{{NAME_n}}`: `findingNames` is derived from the `{{NAME_` prefix; `_AT` numbering would collide; the binder's bolding regex is keyed on `{{NAME_` so examples stay unbolded by a regex fact. `{{EG_n}}` already matches the placeholder regex.

Bindings: every `{{EG_n}}` goes into `prompt.Bindings`; an example whose `CleanLabel` is empty gets no placeholder and is dropped.

Caps: <= 3 per pattern (SQL), <= 6 per email (C#), allocated round-robin by pattern (rank 1 of every pattern before rank 2 of any). Examples do not count toward `MaxNamedFindings` (4). Drop any example whose (EntityKind, EntityId) already holds a `{{NAME_n}}` slot (reuse `SameEntity`). Do not run examples through `DropWhatIsNotWorthNaming`. `InsightCardBuilder` does not get examples in this change.

### 2.6 Pattern -> detector map

| Pattern | Order By | ItemCount | BaseCount | Unit |
|---|---|---|---|---|
| `pattern_liability_locations` / `liability_overdue_location` (36) | LiabOverdueItems DESC, BranchID | LiabOverdueItems | none | overdue obligations carrying liability |
| `pattern_categories` / `category_overdue_skew` (36) | OverdueInst DESC, CategoryId | OverdueInst | Inst | obligations (not due dates) |
| `pat_last_month_slippage` / `last_month_slippage` (37) | LmOpen DESC, MemberId | LmOpen | LmDue | last month's obligations still open |
| `pat_liability_share` / `liability_share` (37) | LiabOverdueItems DESC, MemberId | LiabOverdueItems | OverdueItems | — |
| `pat_chronic_backlog` / `chronic_backlog` (37) | Overdue90Items DESC, MemberId | Overdue90Items | OverdueItems | — |
| `pat_overdue_concentration` / `overdue_concentration` (37) | OverdueItems DESC, MemberId | OverdueItems | @tOverdue | — |
| `pat_deactivated_owner` / `deactivated_owner` (38) | open items held DESC, UserID | held | none | — |
| `pat_self_review` / `self_review` (38) | self-reviewed items DESC, UserID | items | OpenItems | — |
| `pat_single_point_of_failure` / `single_point_of_failure` (39) | OpenItems DESC, BranchID | OpenItems | NULL | — |
| `pat_multi_location_pattern` / `multi_location_pattern` (40) | LocsOverdue DESC, ActID | LocsOverdue | Locations | unit locations (not obligations) |
| `pat_expired_unrenewed_location` / `expired_unrenewed_location` (41) | ExpiredUnrenewed DESC, BranchID | ExpiredUnrenewed | Licences | — |
| `pat_licence_type_lapse_rate` / `licence_type_lapse_rate` (41) | ExpiredUnrenewed DESC, LicenseTypeID | ExpiredUnrenewed | Licences | — |

**No examples:** `ghost_location` (alphabetical order, no materiality measure — `pat_ghost_location_of` still ships); person-kind when `@AllowPersonNames = 0` (`MemberLabel NULL` guard, make explicit in sql/37 and sql/38); `licence_expiring_unrenewed` and `licence_lapsed_recent_unrenewed` (dated events, already named through candidates); any member with a NULL master-data name.

### 2.7 Error codes — per file, from each file's own block

Two conditions, checked in the five slot procs only (36, 38, 39, 40, 41) at the end after every insert:

1. A detector has rows in both `#cand` and `#eg`
2. An example's `PatternFactKey` is absent from `#facts`

Free codes (re-sweep at implementation):
- 36: 51250, 51255–51259
- 38: 51270–51273, 51275–51279
- 39: 51280, 51283, 51285–51289
- 40: 51290, 51293, 51295–51299
- 41: 51300, 51301, 51305–51309

Take the next two free preferring x1–x4; never repurpose x0. No new block above 51309 (would require widening `FreeMonthlyDigestRefusedException.IsMonthlyErrorNumber`).

## 3. Consequences

1. `FreeMonthlyDraftRepair.Shape()` dedups by wording with placeholders and digits stripped, so uniform standalone "including X, Y and Z" sentences collapse; prompts must require examples to ride inside the aggregate finding's own sentence.
2. `AllowedNumbers` grows by up to 2 per example (max 12 per email); bounded by the 6 cap and no-percentages rule.
3. The four new `_of` facts are structural in the overflow path; `MaxFactsFor` should be reviewed deliberately.
4. Examples are not bolded (`NamePlaceholder()` regex excludes `{{EG_`); unit-test it.
5. Extend `FreeMonthlyDraftRepair`'s repeated-name rule to `{{EG_` with `MaxExampleMentions = 1`.
6. Unit mixing is the live falsity risk (aggregate counts members, example counts that member's work); `UnitLabel` per example and the prompt's explicit rule mitigate; `multi_location_pattern` and `category_overdue_skew` are the two to watch.
7. `UnitLabel` must go through the `FactLabels` item->obligation discipline; extend `NoLabelSentToTheModelSaysItem`.
8. `ConsequencesAvailable` could fire on a non-zero `pat_*` fact (adjacent, additive, not on the critical path).
9. Fallback body unchanged.

## 4. Migration path

1. **SQL first (Vinay).** Add the four `_of` facts; declare `#eg` in the five slot procs (byte-identical); `@MaxExamples INT = 3` on all six; fill `#eg` in sql/37 and 36/38/39/40/41; two THROWs in the five slot procs; append the examples grid as #6. Pure ASCII.

2. **sql/42:** five -> six result sets; add eyeball checks (every example row's Detector shows EmitMode = 'aggregate'; no detector in both grids 4 and 6; <= 3 rows per detector; no NULL/blank label; no `ghost_location` rows; person rows absent when `@AllowPersonNames = 0`). Run on the four UAT tenants plus 522, 2480/1807, 29, 1216.

3. **sql/99:** no change (all eight procs already dropped at sql/99:107–114); re-run the CREATE/DROP check.

4. **C#:** `MonthlyExample` + `Examples` property; repository grid 6; prompt attachment, `{{EG_n}}` bindings, caps, dedupe; repair mention cap; binder test.

5. **Prompts:** examples ride inside the finding's sentence; "including"/"among them", never a superlative; own count and base allowed, never a percentage, never a scope phrase; never say how many examples are listed.

6. **Golden-fixture tests**, relationships never values: 1 <= examples <= 3 per aggregate detector with a nameable member; zero for individual/none; candidates and examples disjoint on Detector; `ItemCount` non-increasing by `ExampleRank`; `ItemCount <= BaseCount` where both present; `MetricPct`/`TenantPct` absent; `AllowedPercentages` unchanged; `{{EG_n}}` never in `findingNames`; an email with examples and no findings still fails the `namesUsed == 0` gate.

7. **Preview** on tenants 1082 and 5 before enabling.

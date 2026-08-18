# Golden Fixture Database — Specification

**Purpose:** Tier-2 of the regression strategy (spec §6.6, `sql/02` §B). Tier-1
invariants run against live production and are *drift-proof by relationship*.
Tier-2 proves the definitions produce the **right absolute numbers**, which needs
data that never moves.

---

## Build approach: CLONE AND MUTATE, not INSERT from scratch

Do **not** hand-write `INSERT` statements against `ComplianceInstance`,
`ComplianceScheduleOn`, `Compliance`, `Act` and friends. Those tables have many
required columns and FK dependencies into master data (Acts, categories, statuses);
a from-scratch seed is brittle and breaks whenever the schema evolves.

**Instead:**

1. Restore a non-production copy of `vitComplianceSystem`.
2. Pick a **small real tenant** as the donor (e.g. one with ~1,000 instances).
3. Copy its rows under a **new synthetic `CustomerID`** (reserve `999001`),
   preserving all FK references to shared master data.
4. **Mutate** the copy to produce the fixture cases below — mostly by rewriting
   `RecentComplianceTransactionView` source statuses and `ScheduleOn` dates.
5. Snapshot that database. It is now immutable and the expected values hold forever.

This sidesteps the required-column problem entirely: every row you insert is a copy
of a row the schema already accepted.

> Regenerate the snapshot only when the schema changes — and re-derive expected
> values when you do.

---

## Fixtures and expected values

Tenant `999001`. All schedules dated in the past unless stated.

| # | Fixture | Setup | Expected |
|---|---|---|---|
| **F-1** | Completed early | 10 past-due schedules, status **7** | overdue **0**; on-time completions **10** |
| **F-2** | Completed late ★ | 10 past-due schedules, status **9** | overdue **0**; delayed completions **10** |
| **F-3** | Pending review | 10 past-due schedules, status **2** | overdue **10**; completions **0** |
| **F-4** | Reviewer-final terminal | 10 in status **15**, 10 in status **17** | overdue **0**; **all 20 excluded from the on-time denominator** |
| **F-5** | Performer-proposed | 10 in status **18**, 10 in status **16** | overdue **20** (proposed ⇒ still open) |
| **F-6** | Intermediate node ★ | apex → intermediate holding **10** → leaves holding **10** and **5** | rollup **25**, not 15 |
| **F-7** | Unmapped status | one schedule with a status ID absent from the dictionary | `usp_Insights_AssertStatusCoverage` **THROWs 51001** |
| **F-8** | 2-D scope | user with EA rows for (branch B, category C) only | query for (B, other category) returns **0 rows** |
| **F-9** | Soft-deleted branch | branch `IsDeleted=1` holding 1 instance | excluded from all cuts; reported as orphan |
| **F-10** | Deactivated user | `IsActive=0`, `IsDeleted=0`, holding 5 live assignments | user **appears, flagged** — never hidden |
| **F-11** | Orphaned subtree ★ | active branch under a **soft-deleted** parent, holding 20 | included in rollup, `RootKind='orphan'` |
| **F-12** | Ghost entity | leaf, no children, **0** instances | flagged `no_obligations_configured` |
| **F-13** | Empty peer sample | no branch reaches 50 instances | `degraded_peer_sample` emitted; median from fallback |
| **F-14** | Single branch | tenant with exactly 1 branch | comparatives **suppressed** (no "worst location") |

★ = a case that caused a real defect during design. F-2 is the "lucky escape":
reference tenants had no status 7/9 past-due items, so the old overdue definition
was right by accident.

## Aggregate expectations (F-1 … F-5 only)

```
past-due schedules      80
overdue                 40      (F-3: 10, F-5: 20, +10 from F-6 config)
completed               20      (F-1: 10 on_time, F-2: 10 delayed)
resolved_terminal       20      (F-4)
on-time %               50.0    (10 of 20 COMPLETED — not 10 of 40)
entity rollup           25      (F-6)
```

> The **on-time % of 50.0** is the sharpest assertion in the suite. It is only
> correct if `resolved_terminal` is excluded from the denominator. If a future edit
> lets statuses 15/17 back in, this becomes 25% and the test fails — which is
> exactly the point.

---

## CI wiring

```
1. Restore the fixture snapshot        (fast — small database)
2. Run sql/01 … sql/06
3. EXEC usp_Insights_AssertStatusCoverage        → expect THROW from F-7,
                                                   then remove F-7 row and re-run
4. EXEC usp_Insights_GoldenInvariants @CustomerID = 999001   → all PASS
5. Assert the aggregate expectations above
6. EXEC usp_Insights_Dimension_Location …        → assert flags on F-12/F-13/F-14
```

Any failure blocks the build. Do not allow a "known failing" state — that is how
regression suites die.

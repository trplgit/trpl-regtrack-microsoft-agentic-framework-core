# Golden Fixture Database — Specification

**Purpose:** Tier-2 of the regression strategy (spec §6.6, `sql/02` §B). Tier-1
invariants run against live production and are *drift-proof by relationship*.
Tier-2 proves the definitions produce the **right absolute numbers**, which needs
data that never moves.

**STATUS: BUILT (2026-08-26).** `sql/00_golden_fixture_999001.sql` is the fixture -
DDL for the base RegTrack tables (scripted from real UAT metadata, not hand-typed)
plus the tenant data below. Wired into `.github/workflows/ci.yml`'s `golden` job.
Live-validated against real UAT before being written to a script: all of
`usp_Insights_GoldenInvariants`' checks (G-1..G-9) PASS for both fixture tenants,
every aggregate expectation in this doc matches exactly, and F-12/F-13/F-14's flags
all fired correctly via `usp_Insights_Dimension_Location`. NOT yet proven inside an
actual fresh CI container in this session (no running Docker daemon available) - the
`golden` job in `ci.yml` is the first real run of it there.

---

## Build approach actually used: build in UAT, export a self-contained script

The approach below (restore a non-prod copy, clone-and-mutate, snapshot the
database) assumed a `.bak` file was the right CI delivery mechanism. It is not,
for two reasons discovered while building this: UAT is a real (if non-production)
~8.6GB, ~530-tenant database, so a full backup would both ship far more than a
"small, fast" fixture needs AND put other tenants' real data into a CI artifact -
neither acceptable. What was actually done instead:

1. Reserve `CustomerID 999001` (and, for F-14 - see below, `999002`) directly in
   UAT. **Purely additive** - every write targets those two `CustomerID`s or reads
   an existing master row (`Compliance` 3/5, `Act` 5/32) unchanged; nothing
   belonging to any other tenant is ever touched.
2. Build and mutate the fixture live against UAT (WHILE-loop INSERTs per case,
   wrapped in one transaction that rolls back the whole build on any failure -
   never a partial fixture). Donor: no single donor tenant's existing shape was
   reused: the 14 cases are precisely constructed from scratch, riding on top of
   ONE real `Compliance`/`Act` pair (id 3, `Act` 5, category 12) plus a second
   pair (`Compliance` 5, `Act` 32, category 15) for F-8's negative-category test -
   this sidesteps the required-column problem the same way "clone a real row"
   does, just for two rows instead of a whole tenant's worth.
3. Validate live (see "Actual results" below).
4. Only THEN generate the portable script: schema DDL via `sys.columns` /
   `sys.types` / `sys.default_constraints` (a defaulted column omitted from the
   generated DDL silently breaks in a fresh container even though it worked fine
   against UAT - found and fixed exactly this way for
   `ComplianceInstance.DirectorId`), full-row `INSERT`s for the two master rows,
   the real `ComplianceStatus` dictionary (IDs 1-23; UAT has no row 20), and the
   same tenant-build script re-run verbatim against a blank container (its
   `IDENTITY_INSERT`/`SCOPE_IDENTITY()` shape needs nothing UAT-specific).

**F-7 is NOT part of the fixture data seed.** `usp_Insights_AssertStatusCoverage`
scans `ComplianceStatus` system-wide, not per-tenant - a committed unmapped row
would break every OTHER tenant's dimension-proc pre-flight for as long as it
existed. Validated live against UAT via a transaction that inserted the row,
confirmed the THROW, and unconditionally rolled back (nothing was ever
committed). In the CI script it is safe to seed permanently (an isolated,
disposable container), and `ci.yml`'s own step asserts the THROW, deletes the
row, then continues - matching the CI wiring below exactly.

**F-14 needed its own tenant (999002), not a 14th case on 999001.** "Tenant with
exactly 1 branch" is structurally incompatible with F-6/F-9/F-11/F-12/F-13, all of
which require 999001 to have multiple branches. `docs/GOLDEN_FIXTURES.md`'s
original table assigns every fixture to "Tenant 999001" - that line does not hold
for F-14 specifically; a second reserved tenant was the more faithful fix than
silently dropping the case.

> Regenerate `sql/00_golden_fixture_999001.sql` only when the base schema
> changes - re-run the same build-then-export process, and re-derive expected
> values if anything about the fixture shape changes.

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
| **F-14** | Single branch | **tenant `999002`**, not 999001 - see build notes above | comparatives **suppressed** (no "worst location") |

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

---

## Actual results (live validation against UAT, 2026-08-26)

```
usp_Insights_GoldenInvariants @CustomerID=999001: G-1,G-3,G-4,G-5,G-7,G-8,G-9 PASS
  G-2: new=40 old=60 pastdue(7,9)=20 pastdue(17)=10 pastdue(18)=10 -> reconciles, PASS
  G-5: control=117 rollup=117 gap=0

usp_Insights_GoldenInvariants @CustomerID=999002: all PASS (control=15 rollup=15)

Aggregate expectations (999001):
  past_due_schedules = 80   (expected 80)
  overdue             = 40   (expected 40)
  completed            = 20   (expected 20)
  resolved_terminal    = 20   (expected 20)
  on_time_pct          = 50.0 (expected 50.0)

F-6 rollup: Intermediate=10, Leaf A=10, Leaf B=5 -> 25 total (not 15)
F-9: instance on the soft-deleted branch excluded from both control and rollup symmetrically
F-11: Golden Orphan Child correctly tagged RootKind='orphan', included in rollup
F-12: "Golden Ghost Leaf" flagged no_obligations_configured; F-GHOST finding emitted
F-13: data_quality includes degraded_peer_sample on both tenants (no branch reaches 50)
F-14 (999002): A-WORST assertion absent - comparatives correctly suppressed
```

(G-6 does not exist - removed per `sql/02`'s own note, a data-sanity observation
demoted out of the THROWing invariant suite.)

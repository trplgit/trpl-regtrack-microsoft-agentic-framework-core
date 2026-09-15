# RegTrack Insights — MAF Core

Agentic compliance-analytics engine for RegTrack (~2,290 tenants).

> **Building here? Read [`CLAUDE.md`](./CLAUDE.md) first.** It is the operating
> contract: non-negotiables, hard rules, schema traps, build order.

---

## What this is

RegTrack tells a user *what is due*. Insights answers what a Chief Compliance
Officer actually asks: **how healthy is my estate, what am I on the hook for, and
am I getting better or worse?**

Two products, already in the `Product` table:

| Id | Name | Tier | Delivery |
|---|---|---|---|
| 18 | RegInsights Basic | Free | Weekly email digest |
| 19 | RegInsights Pro | Paid | In-app interactive report |

## Architecture in one line

An **agent orchestrates, composes and narrates** over **permanently deterministic**
scope, SQL, reconciliation and rendering primitives — on Microsoft Agent Framework
(.NET 8), durable on SQL Server.

```
Angular → RegTrack API (auth, endpoints, enqueue)
            → SQL Durable Task hub
              → Insights Worker (private, queue-driven)
                → blob artifact + SQL index
```

## Repository map

| Path | Contents |
|---|---|
| `CLAUDE.md` | **Start here.** Operating contract for anyone building |
| `docs/RegTrack_Insights_System_Design_v1.md` | The full spec — every decision with rationale |
| `docs/DIMENSION_SPECS.md` | Contracts for all 9 dimensions |
| `docs/API_CONTRACTS.md` | The 5 API endpoints |
| `docs/GOLDEN_FIXTURES.md` | CI fixture database spec |
| `docs/RegTrack_Classification_Dictionary_v1.xlsx` | BA-signed status/enum semantics |
| `prompts/` | Agent prompts — composition, narrative, reflection, HTML, digest |
| `sql/01`–`sql/04` | Foundation: dictionary, golden regression, scope, entity + entitlement |
| `sql/05`, `sql/07`–`sql/14` | All 9 dimensions (05 = Location, the reference implementation) |
| `sql/06`, `sql/15`, `sql/16` | Free tier: aggregates, send log, suppression |
| `sql/99_rollback.sql` | Clean teardown — covers all 36 objects |
| `templates/` | Free-digest email shell + deterministic fallback |
| `PHASE_1A_BUILD_BRIEF.md` | Phase 1a tasks, acceptance criteria, validation findings |

## Getting started

```bash
# 1. Restore a NON-PRODUCTION copy of vitComplianceSystem
# 2. Install, in order:
for f in sql/0*.sql sql/1*.sql; do
  sqlcmd -d <db> -f 65001 -i "$f"      # -f 65001 = UTF-8; see docs/DEV_TEAM_NOTE_encoding.md
done
```

> `-f 65001` matters. The deployment path here is not UTF-8 aware by default and
> has silently corrupted stored procedure text before — including a pre-existing
> RegTrack procedure. The scripts are pure ASCII so they are immune, but keep the
> flag for anything else.

Smoke test:
```sql
EXEC dbo.usp_Insights_AssertStatusCoverage;                  -- expect 1
EXEC dbo.usp_Insights_GoldenInvariants @CustomerID = 1490;   -- expect all PASS
EXEC dbo.usp_Insights_Dimension_Location @UserID = 72513, @CustomerID = 1490;
```

Then repeat the invariants against **1403, 1472, 29, 5, 522** — different profiles,
which is where problems surface.

Teardown: `sqlcmd -d <db> -i sql/99_rollback.sql`

> The SQL is **additive only** — 3 new tables, 1 view, 4 functions, 12 procedures.
> No writes to any existing RegTrack table. But it has **never been executed**;
> the logic is validated against production data, the scripts are not. Install to
> dev/UAT first.

## The thing most likely to bite you

**Eight defects were found during design. Every one passed on the first tenant
checked.** Single-tenant validation in this codebase is not weak evidence — it is
actively misleading.

One example: anchoring the entity recursion on apex nodes only reconciled perfectly
on the reference tenant, and lost **89% of another tenant's estate**. Three tenants
would have received a report showing **zero compliance obligations**.

Always validate across tenants with deliberately different profiles, then hunt
boundaries (empty samples, single members, zero denominators). `CLAUDE.md` §11 has
the tenant list.

## Status

**SQL layer complete** — foundation, all 9 dimensions, free tier, rollback.
Installed and verified against a real database: golden invariants 8/8 PASS, all 9
dimensions run and reconcile.

**.NET implementation not started.** That is the next work, per §9 of `CLAUDE.md`.

Fourteen defects were found and fixed while building this — eight during design,
six more on first execution. See §11 of `CLAUDE.md` before writing tests.

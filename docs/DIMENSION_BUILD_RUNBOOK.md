# Dimension build runbook

How to write, install, validate and wrap each remaining dimension stored procedure.
Repeat this for all eight. `sql/05_dimension_location.sql` is the reference
implementation - read it before writing anything.

---

## 1. Contract every dimension must satisfy

Six result sets, in this exact order. `QueryMultiple` reads positionally, so order is
part of the contract:

| # | Result set | Rule |
|---|---|---|
| 1 | `control_totals` | THROWs if sums do not tie |
| 2 | `rows` | one per dimension member, built from the MEMBER list |
| 3 | `detector_policy` | flag counts + EmitMode per detector |
| 4 | `assertions` | typed facts with COMPUTED comparatives |
| 5 | `findings` | each backed by assertion ids |
| 6 | `data_quality` | declared gaps |

> NOTE: `sql/05`'s header comment says "Emits FIVE result sets" and lists five. It
> emits SIX - `detector_policy` is missing from that list. Fix the comment before
> cloning the file, or every wrapper built from it will map the wrong sets.

Mandatory in every dimension (`DIMENSION_SPECS.md` common contract):

- [ ] Signature `(@UserID INT, @CustomerID INT, @AsOf DATETIME = NULL)`
- [ ] Pre-flight: THROW if `tvfInsightsScopePairs` is empty
- [ ] Pre-flight: `EXEC dbo.usp_Insights_AssertStatusCoverage`
- [ ] Scope via `tvfInsightsScopedInstances` - BOTH axes, never branch-only
- [ ] Overdue via `tvfInsightsOverdueSchedules` - never a status literal
- [ ] Any enum value read from `InsightsEnumPolarity`, never hardcoded
- [ ] Rows built from the MEMBER list (LEFT JOIN the facts), never from the fact set
- [ ] `#rows` declared explicitly with ALL columns - no `SELECT INTO` + `ALTER`
- [ ] Reconciliation: `SUM(rows.Instances)` = scoped total, else THROW
- [ ] Every detector routed through the `#detector` emission policy
- [ ] Thresholds peer-relative to the tenant's own distribution
- [ ] Boundaries handled: single member, zero obligations, empty peer sample

---

## 2. Error number allocation

Each dimension gets a block of ten so a THROW identifies the dimension immediately.
Within a block: `x0` = scope denied, `x1` = reconciliation failed, `x2` = dictionary gap.

| Range | Owner | In use |
|---|---|---|
| 51001-51004 | dictionary / status data quality | yes |
| 51010-51019 | scope audit | 51010 |
| 51020-51029 | entity rollup | 51020 |
| 51030-51039 | Location dimension | 51030, 51031, 51032 |
| 51040-51049 | free digest | 51040 |
| 51050-51059 | Entity dimension | free |
| 51060-51069 | Risk | free |
| 51070-51079 | Nature | free |
| 51080-51089 | Departments | free |
| 51090-51099 | Act | free |
| 51100-51109 | Users | free |
| 51110-51119 | Statutory vs Internal | free |
| 51120-51129 | Task / Event | free |

---

## 3. Build order and the trap in each

Do not reorder. Each must pass validation before the next starts.

| # | File | Dimension | The trap that will catch you |
|---|---|---|---|
| 07 | `sql/07_dimension_entity.sql` | Entity | Count at EVERY node, leaf and intermediate. Anchor apex OR orphan. Do not compare at apex level when one apex dominates >70% |
| 08 | `sql/08_dimension_risk.sql` | Risk | `RiskType` read from the dictionary. **Cannot be validated on UAT** - see section 5. Critical and imprisonment overlap ~95%, do NOT present as two findings |
| 09 | `sql/09_dimension_nature.sql` | Nature | ~49% of compliances are "Others" (17). A `data_quality` entry is MANDATORY - never show a nature chart whose largest segment is a meaningless bucket |
| 10 | `sql/10_dimension_departments.sql` | Departments | `DepartmentID` is on the INSTANCE, not the assignment. Build rows from the department list so empty departments still appear. Handle NULL DepartmentID as a declared gap |
| 11 | `sql/11_dimension_act.sql` | Act | Cut by Act x State when `Act.State` varies - the same Act ran 11.7% vs 60.6% overdue across states |
| 12 | `sql/12_dimension_users.sql` | Users | Concentration must be a DISTINCT-INSTANCE UNION. Summing per-user counts double-counts paired performer/reviewer rows and produced an impossible 155%. Engagement and quality are TWO lenses, never one ranking |
| 13 | `sql/13_dimension_internal.sql` | Statutory vs Internal | Internal statuses may not use the same dictionary. VERIFY before reusing `vInsightsStatusCurrent` against `InternalComplianceTransaction` |
| 14 | `sql/14_dimension_event.sql` | Task / Event | Configured != operational. Detect dormancy, but emit it as a question, not an accusation |

---

## 4. Writing one - the method

1. Copy `sql/05_dimension_location.sql` to the new filename.
2. Change the proc name, the error numbers to that dimension's block, and the grain.
3. Replace the `#inst` base and `#rows` columns with the dimension's own columns from
   `DIMENSION_SPECS.md`.
4. Keep the whole detection / `#detector` / assertion / finding structure. Change the
   detector names and flag conditions only.
5. Every threshold: express it relative to the tenant's own median or share. If you
   find yourself typing an absolute number, stop - that is the mistake that broke all
   four Location detectors.
6. Re-read the emission policy block. Individual findings capped at 5, aggregate above
   20%, and `Eligible` and `Flagged` must be drawn from the SAME population or you get
   a percentage over 100.

---

## 5. Validating one

### a) It installs
```sql
-- after running the file, COUNT the objects. Never trust the trailing PRINT -
-- a failed CREATE does not stop later batches.
SELECT COUNT(*) FROM sys.objects
WHERE schema_id = SCHEMA_ID('dbo') AND type IN ('U','V','P','IF','FN','TF')
  AND (name LIKE 'Insights%' OR name LIKE 'usp_Insights%'
    OR name LIKE 'tvfInsights%' OR name LIKE 'vInsights%');
```
The count must rise by exactly the number of objects the file creates.

### b) It runs and reconciles - UAT, five tenants
```sql
EXEC dbo.usp_Insights_Dimension_<Name> @UserID = 11370, @CustomerID = 5;
EXEC dbo.usp_Insights_Dimension_<Name> @UserID = <id>,  @CustomerID = 1363;
EXEC dbo.usp_Insights_Dimension_<Name> @UserID = <id>,  @CustomerID = 29;
EXEC dbo.usp_Insights_Dimension_<Name> @UserID = <id>,  @CustomerID = 23;
EXEC dbo.usp_Insights_Dimension_<Name> @UserID = <id>,  @CustomerID = 1105;
```
Check on every one:
- `control_totals.Reconciled = 1` and `SumOfRows = ScopedInstances`
- `detector_policy`: `Flagged <= Eligible` on EVERY detector, and `FlaggedPct <= 100`
- `findings`: no finding whose scope label is `tenant` in an individual-shaped headline
- Six result sets returned

### c) The numbers are right - PRODUCTION, read-only
UAT proves the code executes. Production proves the numbers are right. For each
"expected pattern" in `DIMENSION_SPECS.md`, write the equivalent plain SELECT and run
it against prod with the read-only login. If the pattern does not reproduce, find out
why BEFORE changing code.

### d) Boundaries - production, because UAT has no real edge cases
- single-member tenant: comparatives must be SUPPRESSED, no "worst X of 1"
- no member reaching the materiality floor: `degraded_peer_sample` must fire
- zero obligations: no verdict inferred, no onboarding claim

### e) Golden regression still green
```sql
EXEC dbo.usp_Insights_GoldenInvariants @CustomerID = 5;
```
G-6 fails on UAT by design (RiskType data mismatch). Everything else must stay green.

---

## 6. The .NET wrapper

Follow `Insights.Data/SqlEntityRepository.cs` exactly:

- Primary constructor taking the connection string
- `QueryMultipleAsync` with `CommandType.StoredProcedure`
- Read the six sets IN ORDER into private `record` row types
- Map to immutable domain records in `Insights.Domain`
- Named `const int` for each error number this proc can throw
- Catch `SqlException` ONLY to translate a known error number into a typed exception -
  never to swallow it. A reconciliation failure must reach the caller.

Then expose it to the LLM the way `trpl-audit-logs-poc` does, using
`Microsoft.Extensions.AI`:

```csharp
public static class DimensionTools
{
    public static AITool[] CreateAll(IDimensionRepository repository)
    {
        [Description("Returns the <name> dimension for a tenant: per-member rows, computed comparatives, findings and declared data-quality gaps. Numbers are reconciled before return.")]
        async Task<DimensionResult> GetDimension(
            [Description("The CustomerID (tenant) to analyse.")] int customerId,
            [Description("The UserID whose authorised scope constrains the result.")] int userId)
            => await repository.GetAsync(customerId, userId);

        return [AIFunctionFactory.Create(GetDimension, name: "Get<Name>Dimension")];
    }
}
```

Two rules for the tool layer:
- The LLM supplies `customerId`. It must be re-validated server-side against the
  caller's eligible tenant set on every call - never trusted (IDOR, spec 5.6.2).
- A THROW from the proc must surface as a refusal, never as an empty result. A tool
  that returns "no findings" when reconciliation failed is the worst outcome in the
  system.

---

## Known open items (deliberately deferred, not defects)

Recorded 19 Aug after Location and Entity were validated on five tenants each. None
of these produces a wrong number; all are output-quality or performance. Revisit when
the composition agent is built, or when a dimension makes one of them material.

- **`A-INT` is ungated in `sql/05`.** `instances_on_intermediate_node` is a flag but is
  not in Location's `#detector` table, so its assertions bypass the emission policy -
  27 individual assertions on tenant 5 where `sql/07` emits one aggregate. Noise for
  the composition agent, not a wrong figure. Fix by adding the detector to `#detector`
  and gating the assertion, matching `sql/07`.

- **Zero-instance orphans differ between procs.** `usp_Insights_EntityRollup` suppresses
  orphans holding nothing (`HAVING SUM(DirectInstances) > 0`); the Entity dimension
  reports them, on the grounds that a broken hierarchy is a finding regardless of
  volume. Both defensible - pick one and record why.

- **Runtime.** `usp_Insights_Dimension_Entity` took ~30s on tenant 1363 (10,494
  instances, 68 nodes). Spec 12.1 assumes "SQL - negligible", and budgets a whole
  report at seconds to ~2 minutes. Nine dimensions at this rate breaks that. Suspect
  `tvfInsightsOverdueSchedules`, which every dimension calls - so it is one shared fix,
  not eight. Time it against tenant 1216 (1.49M past-due schedules) before scheduling
  600 tenants weekly.

- **Reconciliation THROWs have never fired.** 51031 and 51051 are wired and correct by
  inspection, but cannot be triggered without deliberately inconsistent data. That is
  what the frozen fixture database exists for (F-6 in docs/GOLDEN_FIXTURES.md).

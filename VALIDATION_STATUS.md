# Validation status - what has actually been proven

Written 2026-09-08 after two consecutive deployment failures caused by files I
handed over without compiling. This records what is proven, what is not, and how
I will validate from here.

---

## The two failures, and why they were avoidable

| File | Error | Class |
|---|---|---|
| `03` | Msg 130 - aggregate over a subquery in `usp_Insights_AuditScope` | **compile-time** |
| `03` | Msg 4104 - `ucm.*` unbound in `usp_Insights_FindScopelessUsers` | **compile-time** |

Both were compile-time. **Both would have been caught by compiling the procedure
once before handing the file over.** I did not do that. Static marker scans and
reading the SQL are not validation.

Worse, both left the database in a broken state: every procedure is `DROP` then
`CREATE`, so a failed `CREATE` **deletes** the object while the trailing
`PRINT '... installed'` still fires. `usp_Insights_AuditScope` and
`usp_Insights_FindScopelessUsers` were both removed from UAT this way and had to
be restored by hand.

---

## Validation method from here

**1. Compile every changed object individually, using `ALTER` not `DROP`+`CREATE`.**
A failed `ALTER` leaves the existing object intact; a failed `CREATE` after a
committed `DROP` destroys it. Compile errors surface immediately because the
statement is the only thing in the batch.

**2. Execute it and check the first result set reconciles.**

**3. State plainly what remains unproven.** My SQL client returns only the FIRST
result set, so a runtime error in a later statement is invisible to me. That is
how the `Ambiguous column name` defect in `sql/05` survived my testing - the
procedure emitted `control_totals`, `rows` and `detector_policy`, then died, and
I reported it as verified.

**A dimension is fully verified only when its SIXTH result set (`data_quality`)
arrives - which requires SSMS, not me.**

---

## Current state, per object

### Proven: compiled AND executed successfully on UAT

| Object | File | Evidence |
|---|---|---|
| `tvfInsightsManagementUsers` | 01 | 31 mgmt users, tenant 29 |
| `tvfInsightsOwnership` | 01 | 3,063+404+141+129 = 3,737 reconciles |
| `usp_Insights_AuditScope` | 03 | 0/0/0, ScopeAuditPassed |
| `usp_Insights_FindScopelessUsers` | 03 | empty result, independently confirmed correct (86 pairs, 0 scopeless) |
| `usp_Insights_EvaluateGate` | 04 | `PROCEED`, 31 recipients (was 0) |
| `usp_Insights_FreeDigestGate` | 06 | `EXIT_SUPERSEDED`, correct |
| `tvfInsightsScopePairs` | 03 | deployed 21:28, no error |
| `tvfInsightsScopedInstances` | 03 | deployed 21:28, no error |
| `usp_Insights_ClassifyScope` | 03 | deployed 21:28, no error |

### NOT proven from the file

| Object | File | Why |
|---|---|---|
| `usp_Insights_Dimension_Location` | **05** | The D-3 change was applied to UAT by patching the DEPLOYED text, not by running the file. The file version has never compiled. **Deploy this one first and alone.** |
| `usp_Insights_FreeDigestAggregates` | 06 | Unchanged by me, but not compiled from this file |
| `tvfInsightsEntityTree`, `usp_Insights_EntityRollup`, `usp_Insights_TenantShape` | 04 | Unchanged by me, but not compiled from this file |

---

## Deployment order, and what to watch

```
01   (already deployed successfully 21:25)
03   re-run - both failures fixed
04
05   <- least proven; run alone and check the output
06
```

After each file, check the object count, not the message:

```sql
SELECT type_desc, COUNT(*) FROM sys.objects
WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
  AND type IN ('P','U','IF','V')
GROUP BY type_desc;
-- expect: 34 procedures, 8 functions, 6 tables, 1 view
```

A procedure short means one failed to create and has been **deleted**.

Then `98_post_deployment_verification.sql`, which checks this automatically.

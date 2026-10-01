# Multi-version orchestrator dispatch (Option B)

**Status:** approved design, ready for implementation plan.
**Origin:** the 2026-09-30 orphaned-run incident (`docs/PROJECT_STATE_HANDOFF.md` section 1a).
**Supersedes:** nothing shipped yet. The safety net (`DurableTaskRunStatusReader.IsOrphaned`,
commit `0fab3cc`) stays - it is the fallback for anything this design doesn't cover, not replaced
by it.

## 1. Problem

`InsightsReportOrchestrator.Version` is a single string. `WorkerRegistration.cs` registers exactly
one `(Name, Version)` pair via `worker.AddTaskOrchestrations(...)`. The instant a version bump is
redeployed, every orchestration instance whose history was recorded under the OLD version becomes
permanently unloadable - no handler left to resume it, and it cannot even be cancelled (cancellation
posts an event onto the same per-instance queue, which needs a version-matching pod to process it).

Confirmed live 2026-09-30: 3 real tenant-1285 Licence requests, mid-`RenderHtmlActivity` at the
moment of a 4.3->4.4 deploy, got stuck exactly this way. A separate pre-existing Departments
instance had been stuck the same way since 2026-09-20, from an earlier bump - this is a recurring
gap, not a one-off.

**Goal:** an in-flight run started under the old version keeps running to completion after a
version-bumping deploy, with zero visible impact to the user - not a clean failure, an actual finish.

## 2. The confirmed mechanism

Verified directly against the real `DurableTask.Core` 3.9.0 assembly this session (reflection
against a live instance of `NameVersionObjectManager<TaskOrchestration>`, not assumed from
documentation):

- `INameVersionObjectManager<T>.Add(ObjectCreator<T>)` / `GetObject(name, version)` genuinely
  supports **multiple versions of the same orchestrator name registered at once**. Registering
  `("Insights", "4.3")` and `("Insights", "4.4")` side by side, `GetObject("Insights", "4.3")` and
  `GetObject("Insights", "4.4")` each resolved to their own correct type - no ambiguity, no
  cross-version leakage.
- `GetObject(name, null)` with two versions registered returns `null` - it never guesses. Fails
  closed, matching CLAUDE.md non-negotiable #2.
- Activities in this codebase are **never independently versioned** - every activity is registered
  via `DelegateActivityCreator<TActivity>` with a hardcoded `Version = "1.0"`
  (`WorkerRegistration.cs`), and the orchestrator calls them via
  `ctx.ScheduleTask<T>(typeof(X).Name, "1.0", input)`. There is exactly one activity pool, shared by
  every orchestrator version, always running current code.
- `TaskHubWorker` also exposes a first-class `VersioningSettings` (`Version`, `MatchStrategy`:
  `None`/`Strict`/`CurrentOrOlder`, `FailureStrategy`: `Reject`/`Fail`). This governs whether a
  **fleet of pods running different versions** (canary/staged rollout) accepts a work item at the
  dispatcher level - it does not substitute for having the old orchestrator TYPE registered, since
  `GetObject` still needs an exact version match to construct the instance. Not used by this design:
  this project deploys one image, not a staged multi-version fleet. Revisit only if that deploy
  model changes.

**Conclusion:** the real fix is buildable with DTFx's own primitives, exactly as they exist today.
No fork, no waiting on upstream. The design problem is entirely about OUR OWN conventions for
freezing, registering, and retiring old versions - not about DTFx's capability.

## 3. Design

### 3.1 Freeze-on-bump

On every `InsightsReportOrchestrator.Version` bump, before editing the live class:

1. Copy the current `InsightsReportOrchestrator.cs` verbatim into
   `Orchestration/Archived/InsightsReportOrchestratorV{old-version-with-underscore}.cs` (e.g.
   `InsightsReportOrchestratorV4_4.cs` for version `"4.4"`).
2. Rename the class to match the file (`InsightsReportOrchestratorV4_4`).
3. Strip the changelog doc-comment history (frozen - there will never be a future entry). Replace
   with one header: `// Frozen YYYY-MM-DD (superseded by version X.X). Drain-check before deleting -
   see tools/DrainCheck.`
4. Bump `Version` on the live class, add the new changelog entry (unchanged from today's
   convention, including any `[VERIFY BEFORE DEPLOY]` note the change needs).
5. Register the frozen class in `WorkerRegistration.cs`, alongside the live one:

```csharp
worker.AddTaskOrchestrations(
    new NameValueObjectCreator<TaskOrchestration>(
        InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)),
    new NameValueObjectCreator<TaskOrchestration>(
        InsightsReportOrchestrator.Name, "4.4", typeof(InsightsReportOrchestratorV4_4)));
```

6. Deploy.

This mirrors a convention already in use in this codebase for the same problem shape - versioned
prompt files (`_v2`, `_v3`, ...): a full duplicate, frozen, never edited in place once superseded,
new work always happens in a fresh copy.

### 3.2 Multiple frozen versions can coexist

If a second bump happens before the first frozen version has drained, both stay registered
(`Vn` current, `Vn-1` draining, `Vn-2` also still draining). No bump is ever blocked waiting on a
prior drain. Each frozen version is retired independently, whenever ITS OWN in-flight count reaches
zero - not gated on any other version's state.

### 3.3 Retirement (drain-to-zero)

Registrations are compiled into the worker binary - there is no runtime "unregister". Retiring a
frozen version means deleting its class and its registration line, then shipping that as a normal
small change.

Before deleting any frozen version's class:

1. Run the `DrainCheck` tool (new, `tools/DrainCheck/`) against the real hub, passing the version
   string. It queries `dt.Instances` for `Version = @v AND Status IN ('Pending', 'Running')` and
   prints the count; exit code 0 if zero, 1 otherwise (scriptable, not just human-read).
2. Zero -> delete the frozen class + its registration line in the next release.
3. Nonzero -> leave it, check again later. No forced timeline (Option A from the brainstorm:
   drain-to-zero, not a fixed time window).

### 3.4 Known, deliberate residual risk

An old frozen orchestrator resuming still calls activities from the SAME, single, always-current
activity pool (section 2 - activities are never versioned). If an activity's own contract changed
incompatibly underneath a resuming old-version orchestrator, this design does not protect against
that. This is an accepted scope limit, not a gap being closed here: the overwhelming majority of
this project's actual orchestrator version bumps (per `InsightsReportOrchestrator.cs`'s own
changelog) have been call-sequence changes - which order/how many activities get called - not
changes to an individual activity's own input/output shape. If that pattern ever changes, revisit.

**[ADDED 2026-09-30, code review finding] The same residual risk applies to shared static helpers
the orchestrator body calls IN-PROCESS, not just activities scheduled via `ScheduleTask`.** A frozen
copy is only literally a byte-for-byte copy of `InsightsReportOrchestrator.cs` itself (section 3.1)
- it does NOT freeze whatever `Insights.Domain` classes that file's `RunTask` calls directly, e.g.
`FreehandDimensions.Names.Contains(...)`, `FixedHolisticComposition.Build()`/`.Dimensions`,
`DimensionSelectionComposition.Build(...)`. This has already changed once for exactly this reason:
Users joining `FreehandDimensions.Names` is what drove the 3.8 -> 3.9 bump. If such a helper changes
shape or behavior while a frozen version is still draining, that frozen instance replays against the
NEW helper code, not the code it originally ran under - best case a real non-determinism error
(no worse than today); worse case, replay succeeds but takes a silently different branch, producing
an internally inconsistent report. Same accepted-scope-limit reasoning as activities: freezing every
transitively-called helper is a much bigger undertaking than this design attempts, and most bumps
don't touch these helpers. If a future bump specifically changes one of these shared helpers, treat
that as a signal to reconsider scope for that bump specifically (e.g. snapshot the helper too),
not a reason to change this default.

## 4. Guardrails

- **Unit test:** the real `WorkerRegistration` registration list contains no duplicate
  `(Name, Version)` pair for the same orchestrator `Name` - catches a copy-paste mistake when adding
  a frozen class. Cannot enforce that freezing happened at all (that's a manual authoring step, same
  as the pre-existing `[VERIFY BEFORE DEPLOY]` convention it sits next to).
- **Unit test:** a fake `TaskOrchestration` type registered under two versions directly against
  `NameVersionObjectManager<T>` (the exact mechanism proven live this session) - regression-proofs
  the dispatch behavior itself, same spirit as `DurableTaskRunStatusReaderOrphanTests`.

## 5. Testing strategy

1. Registration-list duplicate-version unit test (section 4).
2. Dispatch-mechanism unit test (section 4).
3. `DrainCheck` integration test against a real, disposable, PRIVATE task-hub database (never the
   shared UAT hub - the standing prohibition against a second worker there is unchanged and unrelated
   to this: this test doesn't run a worker against a shared hub, it runs a read-only query against a
   throwaway one), seeded with known instance rows, asserting the correct count.
4. **The real proof, and the one that actually validates the fix (not just DTFx's primitive):** an
   integration test using a throwaway/private task-hub database that starts an instance under a fake
   "old" orchestrator version, builds a SECOND `TaskHubWorker` with both the old and a new fake
   version registered (simulating the deploy), resumes the old instance against that second worker,
   and confirms it completes normally end-to-end. Exact fixture/harness shape (LocalDB vs. a
   dedicated disposable SQL Server database) is an implementation-plan decision, not fixed here -
   the hard constraint is only that it must never be the shared UAT hub.

## 6. Out of scope

- Versioning individual activities (section 3.4).
- DTFx's `VersioningSettings`/`MatchStrategy.CurrentOrOlder` (section 2) - not relevant to a
  single-image deployment model.
- Automating the freeze-on-bump step itself (codegen, tooling). Manual, checklist-enforced, same
  discipline as the existing changelog/`[VERIFY BEFORE DEPLOY]` convention.
- Anything about the OTHER open items in `PROJECT_STATE_HANDOFF.md` section 4 (percentage hover-link
  verification, reasoning-trace kill switch, etc.) - unrelated to this incident.

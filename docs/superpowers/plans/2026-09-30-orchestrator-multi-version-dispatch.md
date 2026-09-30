# Multi-Version Orchestrator Dispatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** build the reusable mechanism (registration plumbing, guardrail tests, drain-check tool,
end-to-end proof) that lets a future `InsightsReportOrchestrator.Version` bump freeze the outgoing
version and keep it registered until it drains, so an in-flight run finishes normally instead of
becoming permanently stuck across a deploy.

**Architecture:** DTFx's own `NameVersionObjectManager<TaskOrchestration>` already supports two
versions of the same orchestrator name registered at once, resolved by exact version match
(confirmed live via reflection, not assumed - see spec section 2). This plan makes that usable in
practice: a testable registration list in `WorkerRegistration.cs`, a checked-in `DrainCheck` tool to
decide when a frozen version is safe to delete, and a real end-to-end test proving an old-version
instance can be picked up and completed by a worker process that also knows the new version.

**Tech Stack:** .NET 8, `DurableTask.Core` 3.9.0 (`Microsoft.Azure.DurableTask.Core`),
`Microsoft.Data.SqlClient` 6.0.2, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-orchestrator-multi-version-dispatch-design.md`

## Global Constraints

- Never run a second full Durable Task WORKER (`AddInsightsOrchestrationWorker`/a raw
  `TaskHubWorker.StartAsync()`) against the SHARED UAT task-hub database. Every test in this plan
  that starts a real `TaskHubWorker` does so against a disposable, freshly created database, never
  `vitInsightsTaskHub` (or whatever the shared hub's real database name is at the time).
- Activities stay single-version (`Version = "1.0"`, hardcoded in `DelegateActivityCreator`,
  unchanged by this plan) - out of scope, a documented residual risk, not something any task here
  changes.
- No new version bump of `InsightsReportOrchestrator.Version` happens in this plan. Freezing only
  makes sense paired with a real bump (freezing the CURRENT version while it is still current would
  register a duplicate `(Name, Version)` pair - exactly what Task 1's guardrail test exists to
  catch). This plan builds the mechanism the NEXT real bump will use; it does not manufacture one.
- `dt.Instances`' real column is `RuntimeStatus` (confirmed against the real UAT schema this
  session via `INFORMATION_SCHEMA.COLUMNS` - it is NOT `Status`). Use `RuntimeStatus` everywhere.
- Central package management is on (`Directory.Packages.props`). New `PackageReference`s take no
  `Version` attribute - the version is already pinned there if the package is already used elsewhere
  in the solution (`Microsoft.Data.SqlClient` is, at `6.0.2`).

## Review Focus

- **A version bump that forgets to freeze the outgoing version at all.** Nothing in this plan can
  force that to happen (it's a manual authoring step) - but Task 1's duplicate-pair test at least
  makes the FAILURE MODE of doing it wrong (registering the old version's `Version` string a second
  time, or reusing the frozen class's name for something else) loud and immediate instead of a
  silent runtime surprise discovered during an incident.
- **`DrainCheck` reporting zero when instances actually exist under a slightly different version
  string spelling** (e.g. `"4.4"` vs `"4.4.0"` vs whitespace) - Task 3's test seeds a near-miss row
  (different version string) alongside real matches specifically to catch a query that's too loose
  or too strict.
- **The end-to-end proof passing for the wrong reason** - e.g., the "old" orchestration instance
  actually got picked up by ITS OWN worker before the second, dual-registered worker ever started,
  so the test would pass even if multi-version dispatch were completely broken. Task 4's steps
  explicitly stop the first worker and confirm the instance is still `Pending` before starting the
  second one, and the assertion checks the OUTPUT value (not just "Completed" status) to confirm
  the SECOND worker's registered type is what actually ran.
- **A disposable test database leaking** if a test fails mid-run (assertion throws before teardown
  runs) - Task 4's fixture uses `IAsyncLifetime.DisposeAsync` (always runs) rather than an
  in-test cleanup step, and names the database with a GUID suffix so a leaked one from a prior failed
  run never collides with or gets mistaken for the current run's database.
- **`OrchestrationRegistrations` drifting out of sync with the real `AddTaskOrchestrations` calls**
  if a future orchestrator (not this plan's concern, but a real future risk) gets registered by
  calling `AddTaskOrchestrations` directly instead of adding to the list - Task 1 removes the direct
  calls entirely (the worker factory loops over the list, nothing bypasses it), so there is no
  second code path left to drift.

---

### Task 1: Testable orchestration registration list

**Files:**
- Modify: `src/RegtrackInsights/Insights.Worker/WorkerRegistration.cs:201-224` (the worker-building
  singleton factory inside `AddInsightsOrchestrationWorker`)
- Test: Create `tests/Insights.UnitTests/WorkerRegistrationOrchestrationTests.cs`

**Interfaces:**
- Produces: `internal static IReadOnlyList<(string Name, string Version, Type Type)>
  WorkerRegistration.OrchestrationRegistrations` - the list every later task (and any future
  version-freeze) adds a frozen entry to.

- [ ] **Step 1: Write the failing test**

```csharp
using Insights.Worker;
using Insights.Worker.Orchestration;
using Xunit;

namespace Insights.UnitTests;

public class WorkerRegistrationOrchestrationTests
{
    [Fact]
    public void OrchestrationRegistrations_HasNoDuplicateNameVersionPairs()
    {
        var seen = new HashSet<(string Name, string Version)>();
        foreach (var (name, version, _) in WorkerRegistration.OrchestrationRegistrations)
        {
            Assert.True(seen.Add((name, version)),
                $"Duplicate (Name, Version) registration: ({name}, {version}). Two orchestrator " +
                "types cannot share the same name+version - DTFx's own NameVersionObjectManager " +
                "would silently let the second Add() overwrite the first.");
        }
    }

    [Fact]
    public void OrchestrationRegistrations_ContainsTheCurrentLiveInsightsOrchestrator()
    {
        Assert.Contains(
            WorkerRegistration.OrchestrationRegistrations,
            e => e.Name == InsightsReportOrchestrator.Name
              && e.Version == InsightsReportOrchestrator.Version
              && e.Type == typeof(InsightsReportOrchestrator));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~WorkerRegistrationOrchestrationTests"`
Expected: FAIL with "'WorkerRegistration' does not contain a definition for 'OrchestrationRegistrations'" (compile error - that's the expected failure here, since the member doesn't exist yet).

- [ ] **Step 3: Add the registration list and route the worker factory through it**

In `WorkerRegistration.cs`, add this member (near the top of the class, e.g. right after the
class declaration on line 23):

```csharp
    /// <summary>
    /// Every orchestrator this worker knows how to run, as data instead of a sequence of
    /// AddTaskOrchestrations calls - lets a unit test assert no two entries collide on
    /// (Name, Version) without touching a real task hub. Read ONLY by the worker factory below;
    /// nothing else may call AddTaskOrchestrations directly, so this list is never stale.
    ///
    /// [ADDED 2026-09-30] Multi-version dispatch (docs/superpowers/specs/2026-09-30-orchestrator-
    /// multi-version-dispatch-design.md). From the NEXT InsightsReportOrchestrator.Version bump
    /// onward: before bumping the live Version, copy the current InsightsReportOrchestrator.cs
    /// into Orchestration/Archived/InsightsReportOrchestratorV{old}.cs, rename the class, strip its
    /// changelog to one frozen-header line, and add an entry for it here under its OLD version
    /// string. Retire a frozen entry (delete the class + this line) only once tools/DrainCheck
    /// reports zero in-flight instances under that exact version - see the spec's section 3.3.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, string Version, Type Type)> OrchestrationRegistrations =
    [
        (InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)),
        (FreeDigestGenerateOrchestrator.Name, FreeDigestGenerateOrchestrator.Version, typeof(FreeDigestGenerateOrchestrator)),
        (FreeDigestSendOrchestrator.Name, FreeDigestSendOrchestrator.Version, typeof(FreeDigestSendOrchestrator)),
        (FreeDigestInsightJsonOrchestrator.Name, FreeDigestInsightJsonOrchestrator.Version, typeof(FreeDigestInsightJsonOrchestrator)),
    ];
```

Then replace the four direct `worker.AddTaskOrchestrations(...)` calls (current lines 213-223) with:

```csharp
            foreach (var (name, version, type) in OrchestrationRegistrations)
                worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(name, version, type));
```

This is behavior-preserving - same four registrations, same order, same construction - only the
data now lives somewhere a test can see it without building a `TaskHubWorker`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~WorkerRegistrationOrchestrationTests"`
Expected: PASS (both tests)

Then run the full unit suite to confirm nothing else broke:

Run: `dotnet test tests/Insights.UnitTests`
Expected: PASS, same total count as before this task plus 2

- [ ] **Step 5: Commit**

```bash
git add src/RegtrackInsights/Insights.Worker/WorkerRegistration.cs tests/Insights.UnitTests/WorkerRegistrationOrchestrationTests.cs
git commit -m "Extract orchestration registrations into a testable list

Behavior-preserving refactor - same four (Name, Version, Type)
registrations, now data instead of four separate AddTaskOrchestrations
calls, so a unit test can assert no duplicate pair without a real task
hub. Plumbing for multi-version orchestrator dispatch (see
docs/superpowers/specs/2026-09-30-orchestrator-multi-version-dispatch-design.md);
no version bump in this commit.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: Dispatch-mechanism regression test

**Files:**
- Test: Create `tests/Insights.UnitTests/OrchestrationMultiVersionDispatchTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 - this test is pure `DurableTask.Core`, no dependency on this
  project's own orchestrators. Kept independent on purpose: it protects the underlying DTFx
  behavior spec section 2 relies on, not this project's own registration list (Task 1 already
  covers that).
- Produces: nothing later tasks depend on - a pinned regression test only.

- [ ] **Step 1: Write the failing test**

`NameVersionObjectManager<T>` is `internal` to `DurableTask.Core` - construct it via reflection,
the same technique proven live this session (see the spec's section 2 for the original finding).

```csharp
using System.Reflection;
using DurableTask.Core;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// Pins the exact DTFx behavior the multi-version dispatch design (docs/superpowers/specs/
/// 2026-09-30-orchestrator-multi-version-dispatch-design.md, section 2) depends on:
/// NameVersionObjectManager&lt;T&gt; resolves GetObject(name, version) by EXACT version match even
/// when two versions of the same name are registered at once. Confirmed live via reflection
/// against DurableTask.Core 3.9.0 - this test makes that confirmation permanent instead of a one-
/// off finding that could silently stop being true on a future DurableTask.Core upgrade.
/// </summary>
public class OrchestrationMultiVersionDispatchTests
{
    private sealed class FakeOrchestrationV1 : TaskOrchestration
    {
        public override Task<string> Execute(OrchestrationContext context, string input) => Task.FromResult("v1");
        public override string GetStatus() => "";
        public override void RaiseEvent(OrchestrationContext context, string name, string input) { }
    }

    private sealed class FakeOrchestrationV2 : TaskOrchestration
    {
        public override Task<string> Execute(OrchestrationContext context, string input) => Task.FromResult("v2");
        public override string GetStatus() => "";
        public override void RaiseEvent(OrchestrationContext context, string name, string input) { }
    }

    [Fact]
    public void GetObject_ResolvesEachRegisteredVersionToItsOwnType()
    {
        var mgrType = typeof(TaskHubWorker).Assembly
            .GetType("DurableTask.Core.NameVersionObjectManager`1")!
            .MakeGenericType(typeof(TaskOrchestration));
        var mgr = Activator.CreateInstance(mgrType)!;
        var addM = mgrType.GetMethod("Add")!;
        var getM = mgrType.GetMethod("GetObject")!;

        object Creator(string version, TaskOrchestration instance) =>
            Activator.CreateInstance(typeof(NameValueObjectCreator<TaskOrchestration>),
                "SameName", version, instance)!;

        addM.Invoke(mgr, [Creator("1.0", new FakeOrchestrationV1())]);
        addM.Invoke(mgr, [Creator("2.0", new FakeOrchestrationV2())]);

        var resolvedV1 = getM.Invoke(mgr, ["SameName", "1.0"]);
        var resolvedV2 = getM.Invoke(mgr, ["SameName", "2.0"]);

        Assert.IsType<FakeOrchestrationV1>(resolvedV1);
        Assert.IsType<FakeOrchestrationV2>(resolvedV2);
    }

    [Fact]
    public void GetObject_WithNoVersion_ReturnsNullRatherThanGuessing_WhenMultipleVersionsRegistered()
    {
        var mgrType = typeof(TaskHubWorker).Assembly
            .GetType("DurableTask.Core.NameVersionObjectManager`1")!
            .MakeGenericType(typeof(TaskOrchestration));
        var mgr = Activator.CreateInstance(mgrType)!;
        var addM = mgrType.GetMethod("Add")!;
        var getM = mgrType.GetMethod("GetObject")!;

        object Creator(string version, TaskOrchestration instance) =>
            Activator.CreateInstance(typeof(NameValueObjectCreator<TaskOrchestration>),
                "SameName", version, instance)!;

        addM.Invoke(mgr, [Creator("1.0", new FakeOrchestrationV1())]);
        addM.Invoke(mgr, [Creator("2.0", new FakeOrchestrationV2())]);

        var resolved = getM.Invoke(mgr, ["SameName", null]);

        Assert.Null(resolved);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~OrchestrationMultiVersionDispatchTests"`
Expected: since this test only calls real, already-existing `DurableTask.Core` code (no new
production code to write), it should actually PASS immediately given the reflection is correct.
If it fails, read the failure carefully - `mgrType`/`addM`/`getM` resolving to `null` means the
internal type/method names changed in the installed `DurableTask.Core` version, which would be
real, important information (this test's whole point), not a test bug to work around.

- [ ] **Step 3: No implementation step**

There is no production code for this task - it is a pure regression test against a third-party
library's existing behavior, per its own doc comment. If Step 2 failed, STOP and re-verify the
reflection against the real installed `DurableTask.Core.dll`
(`~/.nuget/packages/microsoft.azure.durabletask.core/<version>/lib/netstandard2.0/DurableTask.Core.dll`)
before touching this test further - don't loosen the assertions to make it pass.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~OrchestrationMultiVersionDispatchTests"`
Expected: PASS (both tests)

- [ ] **Step 5: Commit**

```bash
git add tests/Insights.UnitTests/OrchestrationMultiVersionDispatchTests.cs
git commit -m "Pin DurableTask.Core's multi-version dispatch behavior with a test

Regression test for the exact NameVersionObjectManager<T> behavior the
multi-version orchestrator dispatch design depends on: two versions of
the same orchestrator name resolve independently by exact match, and an
unversioned lookup returns null rather than guessing. Protects against
a future DurableTask.Core upgrade silently changing this.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: `DrainCheck` tool

**Files:**
- Create: `tools/DrainCheck/DrainCheck.csproj`
- Create: `tools/DrainCheck/DrainCheckQuery.cs`
- Create: `tools/DrainCheck/Program.cs`
- Test: Create `tests/Insights.IntegrationTests/DrainCheckQueryTests.cs`

**Interfaces:**
- Produces: `DrainCheckQuery.CountInFlightAsync(string connectionString, string version,
  CancellationToken ct = default) : Task<int>` - the query logic, kept separate from `Program.cs`
  so the test can call it in-process instead of shelling out to the built exe.

- [ ] **Step 1: Write the failing test**

This test creates its OWN disposable database (never the shared UAT hub) with a minimal
`dt.Instances`-shaped table - only the two columns `DrainCheckQuery` actually reads, `Version` and
`RuntimeStatus` (confirmed real column names, see Global Constraints), are needed to prove the
query logic; the full real DTFx schema is not required for this test.

```csharp
using Microsoft.Data.SqlClient;
using Xunit;

namespace Insights.IntegrationTests;

public class DrainCheckQueryTests : IAsyncLifetime
{
    private readonly string _databaseName = $"InsightsDrainCheckTest_{Guid.NewGuid():N}";
    private string _masterConnectionString = "";
    private string _testDbConnectionString = "";

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _databaseName;
        _testDbConnectionString = builder.ConnectionString;

        await using (var master = new SqlConnection(_masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = new SqlConnection(_testDbConnectionString);
        await db.OpenAsync();
        await using var schema = db.CreateCommand();
        schema.CommandText = @"
CREATE SCHEMA dt;
CREATE TABLE dt.Instances (
    InstanceID VARCHAR(100) NOT NULL PRIMARY KEY,
    Version VARCHAR(100) NULL,
    RuntimeStatus VARCHAR(50) NOT NULL
);";
        await schema.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }

    private async Task InsertRow(string instanceId, string version, string runtimeStatus)
    {
        await using var db = new SqlConnection(_testDbConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO dt.Instances (InstanceID, Version, RuntimeStatus) VALUES (@id, @v, @s)";
        cmd.Parameters.AddWithValue("@id", instanceId);
        cmd.Parameters.AddWithValue("@v", version);
        cmd.Parameters.AddWithValue("@s", runtimeStatus);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CountInFlightAsync_CountsOnlyMatchingVersionAndInFlightStatus()
    {
        await InsertRow("i1", "4.4", "Pending");
        await InsertRow("i2", "4.4", "Running");
        await InsertRow("i3", "4.4", "Completed");   // wrong status - must not count
        await InsertRow("i4", "4.4.0", "Running");   // near-miss version string - must not count
        await InsertRow("i5", "4.3", "Running");     // wrong version - must not count

        var count = await DrainCheckQuery.CountInFlightAsync(_testDbConnectionString, "4.4");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountInFlightAsync_ReturnsZero_WhenNothingMatches()
    {
        await InsertRow("i1", "4.5", "Running");

        var count = await DrainCheckQuery.CountInFlightAsync(_testDbConnectionString, "4.4");

        Assert.Equal(0, count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ConnectionStrings__DurableTaskHub="<real reachable SQL Server connection string, any database - only its server/credentials are reused>" dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~DrainCheckQueryTests"`
Expected: FAIL to compile - `DrainCheckQuery` does not exist yet.

- [ ] **Step 3: Create the DrainCheck project and query logic**

`tools/DrainCheck/DrainCheck.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Checked-in release-process tool, not deployed. Run before deleting a frozen orchestrator
    version's class (docs/superpowers/specs/2026-09-30-orchestrator-multi-version-dispatch-design.md,
    section 3.3) to confirm no in-flight instances still exist under that version.

    Never deployed alongside the worker/API host - sibling of src/ and tests/, matching
    tools/Insights.ApiDevHost's own placement (RegtrackInsights.csproj's own comment on the SDK's
    default globs / MSB3030 applies here too).
  -->

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DrainCheck</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>

</Project>
```

`tools/DrainCheck/DrainCheckQuery.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace DrainCheck;

/// <summary>
/// Counts in-flight (Pending or Running) instances of a given orchestrator version in a real
/// Durable Task SQL provider hub - dt.Instances.RuntimeStatus is the real column name (confirmed
/// against the schema directly, NOT "Status" - see the design spec's section 2 and CLAUDE.md
/// section 13, "believe the data, not the column name").
/// </summary>
public static class DrainCheckQuery
{
    public static async Task<int> CountInFlightAsync(string connectionString, string version, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dt.Instances WHERE Version = @version AND RuntimeStatus IN ('Pending', 'Running')";
        command.Parameters.AddWithValue("@version", version);

        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }
}
```

`tools/DrainCheck/Program.cs`:

```csharp
using DrainCheck;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DrainCheck <version>");
    Console.Error.WriteLine("Reads the hub connection string from ConnectionStrings__DurableTaskHub.");
    return 2;
}

var version = args[0];
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
    ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running DrainCheck.");

var count = await DrainCheckQuery.CountInFlightAsync(connectionString, version);

Console.WriteLine($"Version {version}: {count} in-flight instance(s) (Pending or Running).");

return count == 0 ? 0 : 1;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `ConnectionStrings__DurableTaskHub="<same connection string as Step 2>" dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~DrainCheckQueryTests"`
Expected: PASS (both tests). This creates and drops a real, disposable database on whatever server
the connection string points at - confirm that server is one you're allowed to create/drop
databases on (the UAT SQL Server at `10.13.0.6` is fine for this; never point it at the shared
production/prod-readonly server).

Then confirm the tool actually runs end to end:

Run: `cd tools/DrainCheck && ConnectionStrings__DurableTaskHub="<a real hub, e.g. UAT>" dotnet run -- 4.4`
Expected: prints a real count and exits 0 or 1 accordingly (whatever the real count is against
that hub - not asserting a specific number here, just that it runs and doesn't throw).

- [ ] **Step 5: Commit**

```bash
git add tools/DrainCheck tests/Insights.IntegrationTests/DrainCheckQueryTests.cs
git commit -m "Add DrainCheck tool for retiring frozen orchestrator versions

Checked-in release-process tool: counts Pending/Running dt.Instances
rows for a given orchestrator version, replacing hand-typed SQL as the
pre-retirement check (docs/superpowers/specs/2026-09-30-orchestrator-
multi-version-dispatch-design.md, section 3.3). Query logic isolated in
DrainCheckQuery so the test calls it in-process against a disposable
database rather than shelling out.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: End-to-end proof - old-version instance completes via a dual-registered worker

**Files:**
- Test: Create `tests/Insights.IntegrationTests/MultiVersionOrchestratorResumeTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (deliberately independent, same reasoning as Task 2 - this
  proves the MECHANISM works end to end against a real database, not this project's own
  registration list).
- Produces: nothing later tasks depend on - the real proof artifact the spec's section 5 calls for.

This is the test that actually validates the fix, not just DTFx's dispatch primitive (spec section
5, point 4). Sequence: create a disposable hub database -> start "Worker A" with ONLY the old
orchestrator version registered -> enqueue an instance -> stop Worker A before it can process
anything (confirm the instance is still `Pending` - this is what makes the proof real: if Worker A
had already run it, "Worker B" finishing it would prove nothing) -> start "Worker B" against the
SAME database with BOTH the old and a new fake version registered (simulating a redeploy) -> confirm
the instance completes, and that its OUTPUT came from the OLD version's type specifically.

- [ ] **Step 1: Write the failing test**

```csharp
using DurableTask.Core;
using Microsoft.Data.SqlClient;
using DurableTask.SqlServer;
using Xunit;

namespace Insights.IntegrationTests;

public class MultiVersionOrchestratorResumeTests : IAsyncLifetime
{
    private readonly string _databaseName = $"InsightsMultiVersionResumeTest_{Guid.NewGuid():N}";
    private string _masterConnectionString = "";
    private string _hubConnectionString = "";

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _databaseName;
        _hubConnectionString = builder.ConnectionString;

        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_databaseName}]";
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class OldVersionOrchestration : TaskOrchestration<string, string>
    {
        public override async Task<string> RunTask(OrchestrationContext context, string input)
        {
            var echoed = await context.ScheduleTask<string>("Echo", "1.0", input);
            return $"old-version-result:{echoed}";
        }
    }

    private sealed class NewVersionOrchestration : TaskOrchestration<string, string>
    {
        public override async Task<string> RunTask(OrchestrationContext context, string input)
        {
            var echoed = await context.ScheduleTask<string>("Echo", "1.0", input);
            return $"new-version-result:{echoed}";
        }
    }

    private sealed class EchoActivity : TaskActivity<string, string>
    {
        protected override string Execute(TaskContext context, string input) => input;
    }

    private SqlOrchestrationService BuildService() => new(new SqlOrchestrationServiceSettings(_hubConnectionString)
    {
        MaxConcurrentActivities = 1,
        MaxActiveOrchestrations = 1,
    });

    [Fact]
    public async Task OldVersionInstance_StillPendingAfterFirstWorkerStops_CompletesViaDualRegisteredWorker()
    {
        const string orchestratorName = "MultiVersionResumeTestOrchestrator";
        const string instanceId = "multi-version-resume-test-instance";

        var serviceA = BuildService();
        await serviceA.CreateIfNotExistsAsync();

        // Worker A: only the OLD version registered - simulates the pre-deploy pod.
        var workerA = new TaskHubWorker(serviceA);
        workerA.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(
            orchestratorName, "1.0", typeof(OldVersionOrchestration)));
        workerA.AddTaskActivities(new NameValueObjectCreator<TaskActivity>("Echo", "1.0", typeof(EchoActivity)));

        var clientA = new TaskHubClient((IOrchestrationServiceClient)serviceA);
        await clientA.CreateOrchestrationInstanceAsync(orchestratorName, "1.0", instanceId, "hello");

        // Confirm it's still Pending before any worker starts - this is what makes the later
        // "completes via Worker B" assertion actually mean something (see this task's own intro).
        var stateBeforeAnyWorkerRuns = await clientA.GetOrchestrationStateAsync(instanceId);
        Assert.Equal(OrchestrationStatus.Pending, stateBeforeAnyWorkerRuns.OrchestrationStatus);

        // Worker A never starts its dispatch loop at all in this test - a Pending instance with no
        // worker running is exactly "queued across a deploy boundary, old pod already gone."

        // Worker B: BOTH versions registered - simulates the post-deploy pod carrying the frozen
        // old-version class alongside its own current one.
        var serviceB = BuildService();
        var workerB = new TaskHubWorker(serviceB);
        workerB.AddTaskOrchestrations(
            new NameValueObjectCreator<TaskOrchestration>(orchestratorName, "1.0", typeof(OldVersionOrchestration)),
            new NameValueObjectCreator<TaskOrchestration>(orchestratorName, "2.0", typeof(NewVersionOrchestration)));
        workerB.AddTaskActivities(new NameValueObjectCreator<TaskActivity>("Echo", "1.0", typeof(EchoActivity)));

        await workerB.StartAsync();
        try
        {
            var clientB = new TaskHubClient((IOrchestrationServiceClient)serviceB);
            var finalState = await clientB.WaitForOrchestrationAsync(
                new OrchestrationInstance { InstanceId = instanceId }, TimeSpan.FromSeconds(30));

            Assert.Equal(OrchestrationStatus.Completed, finalState.OrchestrationStatus);
            // The OLD version's type produced this, not the new one - proves Worker B actually
            // dispatched by the instance's own recorded version ("1.0"), not just its own current one.
            Assert.Equal("\"old-version-result:hello\"", finalState.Output);
        }
        finally
        {
            await workerB.StopAsync();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ConnectionStrings__DurableTaskHub="<real reachable SQL Server connection string>" dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~MultiVersionOrchestratorResumeTests"`
Expected: since this test only calls real, already-existing `DurableTask.Core`/`DurableTask.SqlServer`
APIs (no new production code), it should PASS on the first real run if the design's core claim is
true. If it FAILS, that is a genuinely important result - it means the mechanism this whole plan is
built on doesn't hold in practice, and Task 1/3's work should not proceed until this is understood.
Do not weaken this test to make it pass.

- [ ] **Step 3: No implementation step**

Same reasoning as Task 2, Step 3 - this is a proof against existing third-party behavior, not new
production code.

- [ ] **Step 4: Run test to verify it passes**

Run: `ConnectionStrings__DurableTaskHub="<same as Step 2>" dotnet test tests/Insights.IntegrationTests --filter "FullyQualifiedName~MultiVersionOrchestratorResumeTests"`
Expected: PASS. This is the real proof the spec's section 5 calls for.

- [ ] **Step 5: Commit**

```bash
git add tests/Insights.IntegrationTests/MultiVersionOrchestratorResumeTests.cs
git commit -m "Prove an old-version instance completes via a dual-registered worker

Real end-to-end proof against a disposable task-hub database (never
the shared UAT hub): an instance created under an old orchestrator
version, still Pending with no worker running, gets picked up and
completed correctly by a second worker that has BOTH the old and a new
version registered - the actual scenario a version-bumping deploy
creates. This is what validates the fix itself, not just DTFx's
dispatch primitive (already covered by Task 2).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: Wire the freeze-on-bump convention into the real changelog + handoff doc

**Files:**
- Modify: `src/RegtrackInsights/Insights.Worker/Orchestration/InsightsReportOrchestrator.cs:405-422`
  (the changelog doc-comment, right before `public const string Version = "4.4";`)
- Modify: `docs/PROJECT_STATE_HANDOFF.md` (section 4, item 1)

No tests - documentation-only task. Keeps the discipline this whole design depends on ("remember to
freeze on the NEXT bump") visible in the one place every prior bump's own reasoning already lives,
rather than only in a spec file someone would have to know to go looking for.

- [ ] **Step 1: Add a note to the changelog, right after the 4.3->4.4 entry, before the `Version` const**

In `InsightsReportOrchestrator.cs`, insert this paragraph immediately before the closing `*/` of the
changelog comment block (i.e., right after the "Bumped 4.3 -> 4.4" paragraph, before line 422's
`public const string Version = "4.4";`):

```
        [ADDED 2026-09-30] Multi-version dispatch is now available - see docs/superpowers/specs/
        2026-09-30-orchestrator-multi-version-dispatch-design.md. From the NEXT bump onward: before
        changing Version below, copy this file into Orchestration/Archived/
        InsightsReportOrchestratorV{old}.cs, rename the class, strip its changelog to one frozen-
        header line, and add it to WorkerRegistration.OrchestrationRegistrations under its OLD
        version string. This is what lets an in-flight run from the outgoing version keep running
        to completion across this deploy, instead of becoming permanently stuck (the 2026-09-30
        incident this whole mechanism exists to prevent - see PROJECT_STATE_HANDOFF.md section 1a).
        Retire the frozen class later via tools/DrainCheck, once it reports zero in-flight instances
        under that version - no forced timeline, and multiple frozen versions may coexist if a
        second bump happens before the first has drained.
```

- [ ] **Step 2: Update the handoff doc's open-items list**

In `docs/PROJECT_STATE_HANDOFF.md` section 4, item 1 currently reads:

```
1. **Orchestrator-version orphaning (section 1a) - Option A or B not yet built.** ...
```

Replace it with:

```
1. **Orchestrator-version orphaning (section 1a) - the safety net (Option A-adjacent) is shipped;
   Option B's real structural fix is now BUILT and available, not yet exercised by a real version
   bump.** See `docs/superpowers/specs/2026-09-30-orchestrator-multi-version-dispatch-design.md`
   and `docs/superpowers/plans/2026-09-30-orchestrator-multi-version-dispatch.md`. Confirmed live:
   DurableTask.Core's own NameVersionObjectManager supports multiple registered versions of the same
   orchestrator name, resolved by exact match - a full end-to-end test proves an old-version
   instance completes via a worker that also knows a new version
   (`MultiVersionOrchestratorResumeTests`). `WorkerRegistration.OrchestrationRegistrations` is the
   real registration list; `InsightsReportOrchestrator.cs`'s own changelog now documents the
   freeze-on-bump steps right next to its `Version` const. Nothing has actually frozen a real
   version yet - that happens naturally at the NEXT version bump, per the changelog note added
   there.
```

- [ ] **Step 3: Commit**

```bash
git add src/RegtrackInsights/Insights.Worker/Orchestration/InsightsReportOrchestrator.cs docs/PROJECT_STATE_HANDOFF.md
git commit -m "Document the freeze-on-bump convention at the point of next use

Adds the multi-version dispatch steps to InsightsReportOrchestrator's
own changelog, right next to its Version const - the same place every
prior bump's own reasoning already lives, so the NEXT bump can't miss
it. Updates the handoff doc's open-items list to reflect the mechanism
is now built (not yet exercised by a real bump).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

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

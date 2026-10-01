using System.Reflection;
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

    /// <summary>
    /// [ADDED 2026-09-30, code review finding] The duplicate-pair test catches registering two
    /// DIFFERENT versions under the same string, but not the likelier authoring mistake: swapping
    /// which version string a type is registered under (e.g. freezing InsightsReportOrchestratorV4_4
    /// but accidentally registering it as "4.5" and the live class as "4.4"). That produces no
    /// duplicate, passes dispatch, and silently runs the wrong logic for both versions. Every
    /// orchestrator type in this codebase already exposes a public const string Version - this
    /// checks each registration's own recorded version string against that const via reflection.
    /// </summary>
    [Fact]
    public void OrchestrationRegistrations_EachEntrysVersionMatchesItsTypesOwnVersionConst()
    {
        foreach (var (name, version, type) in WorkerRegistration.OrchestrationRegistrations)
        {
            var versionField = type.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            Assert.True(versionField is not null,
                $"{type.Name} (registered as {name}/{version}) has no public const/static string Version field.");

            var actualVersion = versionField!.GetValue(null) as string;
            Assert.True(version == actualVersion,
                $"{type.Name} is registered under version \"{version}\" but its own Version const is " +
                $"\"{actualVersion}\" - the registration and the type's own const have drifted apart.");
        }
    }
}

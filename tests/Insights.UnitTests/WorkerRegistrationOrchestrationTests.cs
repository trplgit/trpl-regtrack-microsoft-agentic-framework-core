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

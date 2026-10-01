using DurableTask.Core;
using Insights.Worker.Orchestration;

namespace Insights.UnitTests;

/// <summary>
/// [2026-09-30] Confirmed live: a run whose history was written under an orchestrator version
/// the current worker no longer registers (a version bump between when the run started and when
/// it was deployed over) can never be resumed - it just reports "running" forever. These tests
/// cover DurableTaskRunStatusReader.IsOrphaned, the check that turns that into an honest "failed"
/// the next time anyone asks, instead of an endless spinner (CLAUDE.md non-negotiable #2).
/// TaskHubClient.GetOrchestrationStateAsync is not virtual, so GetStatusAsync itself cannot be
/// unit-tested without hitting a real task hub - this covers the pure decision logic directly.
/// </summary>
public sealed class DurableTaskRunStatusReaderOrphanTests
{
    private static OrchestrationState State(string version, DateTime lastUpdatedUtc) => new()
    {
        Name = InsightsReportOrchestrator.Name,
        Version = version,
        LastUpdatedTime = lastUpdatedUtc,
        OrchestrationInstance = new OrchestrationInstance { InstanceId = "insights-1285-test" },
    };

    [Fact]
    public void VersionMismatch_IsOrphaned_EvenWithRecentProgress()
    {
        var state = State("4.3", DateTime.UtcNow);

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, out var reason);

        Assert.True(orphaned);
        Assert.Contains("version mismatch", reason);
        Assert.Contains("4.3", reason);
    }

    [Fact]
    public void MatchingVersion_RecentProgress_IsNotOrphaned()
    {
        var state = State(InsightsReportOrchestrator.Version, DateTime.UtcNow.AddMinutes(-1));

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, out _);

        Assert.False(orphaned);
    }

    [Fact]
    public void MatchingVersion_StaleForOver30Minutes_IsOrphaned()
    {
        var state = State(InsightsReportOrchestrator.Version, DateTime.UtcNow.AddMinutes(-31));

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, out var reason);

        Assert.True(orphaned);
        Assert.Contains("no progress", reason);
    }

    [Fact]
    public void MatchingVersion_JustUnder30Minutes_IsNotOrphaned()
    {
        var state = State(InsightsReportOrchestrator.Version, DateTime.UtcNow.AddMinutes(-29));

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, out _);

        Assert.False(orphaned);
    }

    [Fact]
    public void VersionMismatch_ReasonWins_WhenBothConditionsAreTrue()
    {
        // Both stale AND wrong version - the reason should still name a real cause, not blend them.
        var state = State("4.3", DateTime.UtcNow.AddMinutes(-60));

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, out var reason);

        Assert.True(orphaned);
        Assert.Contains("version mismatch", reason);
    }

    /// <summary>
    /// [ADDED 2026-09-30, code review finding on the multi-version dispatch design] The original
    /// IsOrphaned compared only against InsightsReportOrchestrator.Version (the CURRENT version).
    /// Once a frozen version is kept registered alongside the current one (docs/superpowers/specs/
    /// 2026-09-30-orchestrator-multi-version-dispatch-design.md), every run resumed by that frozen
    /// class would still fail this check and get reported "failed" the moment anyone polled it -
    /// defeating the whole point (the run finishes for real, but the user is told it failed). Fixed
    /// by checking registration-list MEMBERSHIP (any currently-registered version) instead of exact
    /// equality with the current version. These tests use the explicit-list overload with a fake
    /// registration set, since no real frozen class exists yet (freezing happens at the NEXT
    /// version bump - see InsightsReportOrchestrator.cs's own changelog note).
    /// </summary>
    [Fact]
    public void RegisteredNonCurrentVersion_IsNotOrphaned_WhenStillInRegistrationList()
    {
        var registrations = new (string Name, string Version, Type Type)[]
        {
            (InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)),
            (InsightsReportOrchestrator.Name, "4.3", typeof(InsightsReportOrchestrator)), // stand-in for a frozen class
        };
        var state = State("4.3", DateTime.UtcNow);

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, registrations, out _);

        Assert.False(orphaned);
    }

    [Fact]
    public void UnregisteredVersion_IsOrphaned_EvenIfItWasNeverTheCurrentVersion()
    {
        var registrations = new (string Name, string Version, Type Type)[]
        {
            (InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)),
        };
        var state = State("3.9", DateTime.UtcNow); // an old, retired, no-longer-registered version

        var orphaned = DurableTaskRunStatusReader.IsOrphaned(state, registrations, out var reason);

        Assert.True(orphaned);
        Assert.Contains("3.9", reason);
    }
}

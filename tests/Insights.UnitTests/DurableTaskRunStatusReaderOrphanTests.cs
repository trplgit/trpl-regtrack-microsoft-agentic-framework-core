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
}

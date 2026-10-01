using Insights.Data;
using Insights.Worker.Orchestration.Activities;
using Moq;

namespace Insights.UnitTests;

/// <summary>Design doc Sec.12.3's write half - the read half (CheckTenantTokenBudgetActivityTests) is tested separately.</summary>
public sealed class RecordTenantTokenUsageActivityTests
{
    [Fact]
    public async Task RunAsync_PositiveTokens_RecordsThem()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        var activity = new RecordTenantTokenUsageActivity(repository.Object);

        await activity.RunAsync(new RecordTenantTokenUsageInput(29, "insights-29-abc123", 12_345));

        repository.Verify(r => r.RecordUsageAsync(29, "insights-29-abc123", 12_345, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [FOUND LIVE 2026-09-28] The run id is the same for every re-run of one tenant/dimension/period,
    /// and the ledger keeps one row per key - so a re-run's spend was silently never recorded
    /// (235,714 tokens across Users + Act in one real request). Each EXECUTION now gets its own row;
    /// a redelivery of the same execution still lands on the same key (no double count).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithExecutionId_KeysTheRowPerExecution()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        var activity = new RecordTenantTokenUsageActivity(repository.Object);

        await activity.RunAsync(new RecordTenantTokenUsageInput(29, "insights-29-abc123", 500), executionId: "exec-2");
        await activity.RunAsync(new RecordTenantTokenUsageInput(29, "insights-29-abc123", 700), executionId: "exec-3");

        repository.Verify(r => r.RecordUsageAsync(29, "insights-29-abc123|exec:exec-2", 500, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.RecordUsageAsync(29, "insights-29-abc123|exec:exec-3", 700, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A refusal that happened before ANY token spend (e.g. the monthly check itself) has nothing worth a ledger row for.</summary>
    [Fact]
    public async Task RunAsync_ZeroTokens_SkipsTheWrite()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        var activity = new RecordTenantTokenUsageActivity(repository.Object);

        await activity.RunAsync(new RecordTenantTokenUsageInput(29, "insights-29-abc123", 0));

        repository.Verify(r => r.RecordUsageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

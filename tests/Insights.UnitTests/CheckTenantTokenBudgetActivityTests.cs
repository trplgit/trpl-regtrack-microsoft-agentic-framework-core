using Insights.Data;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Insights.UnitTests;

/// <summary>
/// Design doc Sec.12.3 - the per-tenant monthly circuit breaker and its 80%-alert sibling.
/// InsightsReportOrchestratorTests pins that this activity gets called first and correctly gates
/// the rest of the run; these pin the CHECK LOGIC itself in isolation.
/// </summary>
public sealed class CheckTenantTokenBudgetActivityTests
{
    private const int TenantId = 29;
    private static readonly DateTime AsOf = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartOfMonth = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TenantTokenBudgetSettings Settings(long ceiling = 1_000_000, int alertAtPercent = 80) =>
        new() { MonthlyCeiling = ceiling, AlertAtPercent = alertAtPercent };

    private sealed class CapturingLogger : ILogger<CheckTenantTokenBudgetActivity>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task RunAsync_WellUnderBudget_DoesNotThrowOrLog()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        repository.Setup(r => r.GetTokensSinceAsync(TenantId, StartOfMonth, It.IsAny<CancellationToken>())).ReturnsAsync(100_000);
        var logger = new CapturingLogger();
        var activity = new CheckTenantTokenBudgetActivity(repository.Object, Settings(), logger);

        var result = await activity.RunAsync(new CheckTenantTokenBudgetInput(TenantId, AsOf));

        Assert.Equal(100_000, result.MonthToDateTokens);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task RunAsync_AtOrAboveCeiling_ThrowsMonthlyBudgetExceeded_WithoutLeakingNumbersToTheUserMessage()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        repository.Setup(r => r.GetTokensSinceAsync(TenantId, StartOfMonth, It.IsAny<CancellationToken>())).ReturnsAsync(1_000_000);
        var activity = new CheckTenantTokenBudgetActivity(repository.Object, Settings(ceiling: 1_000_000), new CapturingLogger());

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(
            () => activity.RunAsync(new CheckTenantTokenBudgetInput(TenantId, AsOf)));

        Assert.Equal("MONTHLY_BUDGET_EXCEEDED", ex.ReasonCode);
        Assert.DoesNotContain("1000000", ex.Message); // never leak internals to the user-facing message (Sec.11.3)
        Assert.Contains(ex.InternalDiagnostics, d => d.Contains("1000000"));
    }

    [Fact]
    public async Task RunAsync_AtOrAboveAlertThreshold_ButUnderCeiling_LogsAWarning_DoesNotThrow()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        repository.Setup(r => r.GetTokensSinceAsync(TenantId, StartOfMonth, It.IsAny<CancellationToken>())).ReturnsAsync(850_000); // 85% of 1,000,000
        var logger = new CapturingLogger();
        var activity = new CheckTenantTokenBudgetActivity(repository.Object, Settings(ceiling: 1_000_000, alertAtPercent: 80), logger);

        var result = await activity.RunAsync(new CheckTenantTokenBudgetInput(TenantId, AsOf));

        Assert.Equal(850_000, result.MonthToDateTokens); // did not throw
        Assert.Single(logger.Messages, m => m.Contains("Warning") && m.Contains("29") && m.Contains("85"));
    }

    [Fact]
    public async Task RunAsync_BelowAlertThreshold_DoesNotLog()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        repository.Setup(r => r.GetTokensSinceAsync(TenantId, StartOfMonth, It.IsAny<CancellationToken>())).ReturnsAsync(790_000); // 79% of 1,000,000
        var logger = new CapturingLogger();
        var activity = new CheckTenantTokenBudgetActivity(repository.Object, Settings(ceiling: 1_000_000, alertAtPercent: 80), logger);

        await activity.RunAsync(new CheckTenantTokenBudgetInput(TenantId, AsOf));

        Assert.Empty(logger.Messages);
    }

    /// <summary>The check must scope to the CALENDAR month, not a rolling 30 days - a 2026-08-21 check reads usage since 2026-08-01, not 2026-07-22.</summary>
    [Fact]
    public async Task RunAsync_ScopesToCalendarMonth_NotARollingWindow()
    {
        var repository = new Mock<ITenantTokenBudgetRepository>();
        repository.Setup(r => r.GetTokensSinceAsync(TenantId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);
        var activity = new CheckTenantTokenBudgetActivity(repository.Object, Settings(), new CapturingLogger());

        await activity.RunAsync(new CheckTenantTokenBudgetInput(TenantId, new DateTime(2026, 8, 21, 23, 59, 0, DateTimeKind.Utc)));

        repository.Verify(r => r.GetTokensSinceAsync(TenantId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), It.IsAny<CancellationToken>()), Times.Once);
    }
}

using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>Fixed-value IOptionsMonitor for activity tests.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class InteractiveTileQaActivityTests
{
    private static readonly TileQaOptions Enabled = new() { Enabled = true, PatchEnabled = true };

    private static TileCheckResult Result(params TileFinding[] findings) => new(findings, 0, [], false);

    [Fact]
    public async Task RunAsync_DelegatesToCheckerAndWrapsFindings()
    {
        var finding = new TileFinding("section.card[data-tile-qa-index=\"0\"]", "Title", "click", TileFindingSeverity.Functional, "desc", "", "", CardOrdinal: 0, ElementKind: "button");
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync("<html></html>", It.IsAny<TileCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result(finding));

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance, new StaticOptionsMonitor<TileQaOptions>(Enabled));
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Single(result.Findings);
        Assert.Equal(TileFindingSeverity.Functional, result.Findings[0].Severity);
        Assert.False(result.PatchDisabled);
        Assert.NotEqual(true, result.Disabled);
    }

    [Fact]
    public async Task RunAsync_NoFindings_ReturnsEmptyList()
    {
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync(It.IsAny<string>(), It.IsAny<TileCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result());

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance, new StaticOptionsMonitor<TileQaOptions>(Enabled));
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Empty(result.Findings);
    }

    /// <summary>[ADDED 2026-10-09] Code defaults are OFF: no options registered means no browser page, no findings.</summary>
    [Fact]
    public async Task RunAsync_NoOptionsConfigured_IsDisabled_NeverCallsChecker()
    {
        var checker = new Mock<IInteractiveTileChecker>(MockBehavior.Strict);

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance);
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Empty(result.Findings);
        Assert.True(result.Disabled);
        Assert.True(result.PatchDisabled);
        checker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_EnabledButPatchOff_ReportsFindings_WithPatchDisabled()
    {
        var finding = new TileFinding("section.card[data-tile-qa-index=\"0\"]", "Title", "click", TileFindingSeverity.Functional, "desc", "", "", 0, "button");
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync(It.IsAny<string>(), It.IsAny<TileCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result(finding));

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance,
            new StaticOptionsMonitor<TileQaOptions>(new TileQaOptions { Enabled = true, PatchEnabled = false }));
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Single(result.Findings);
        Assert.True(result.PatchDisabled);
    }

    /// <summary>[ADDED 2026-10-09] Cosmetic QA fails soft: a checker crash is "no findings", never a failed report.</summary>
    [Fact]
    public async Task RunAsync_CheckerThrows_ReturnsNoFindings_Truncated()
    {
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync(It.IsAny<string>(), It.IsAny<TileCheckRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("browser died"));

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance, new StaticOptionsMonitor<TileQaOptions>(Enabled));
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Empty(result.Findings);
        Assert.True(result.Truncated);
    }

    /// <summary>[ADDED 2026-10-09] Options and the re-verify scope reach the checker; page errors, truncation and tokens come back.</summary>
    [Fact]
    public async Task RunAsync_PassesOrdinalsAndBudgets_AndSurfacesPageErrorsTokensTruncation()
    {
        TileCheckRequest? captured = null;
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync(It.IsAny<string>(), It.IsAny<TileCheckRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, TileCheckRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new TileCheckResult([], 777, ["TypeError: x"], true));

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance,
            new StaticOptionsMonitor<TileQaOptions>(new TileQaOptions { Enabled = true, PatchEnabled = true, BudgetSeconds = 42, MaxGlitchReviews = 2, ScreenshotDirectory = " " }));
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>", OnlyCardOrdinals: [3, 5]));

        Assert.NotNull(captured);
        Assert.Equal([3, 5], captured!.OnlyCardOrdinals);
        Assert.Equal(42, captured.BudgetSeconds);
        Assert.Equal(2, captured.MaxGlitchReviews);
        Assert.Null(captured.ScreenshotDirectory); // blank config = prod mode, nothing on disk
        Assert.Equal(777, result.TotalTokens);
        Assert.Equal(["TypeError: x"], result.PageErrors);
        Assert.True(result.Truncated);
    }
}

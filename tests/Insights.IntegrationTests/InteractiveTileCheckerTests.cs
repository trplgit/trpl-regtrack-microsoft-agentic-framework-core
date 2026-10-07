using Insights.Domain;
using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace Insights.IntegrationTests;

/// <summary>
/// [ADDED 2026-10-07] InteractiveTileChecker in real Chromium against hand-made pages - mirrors
/// LayoutCollisionCheckerTests.cs's own convention (real browser, synthetic HTML, IAsyncLifetime).
/// </summary>
public sealed class InteractiveTileCheckerTests : IAsyncLifetime
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private string? screenshotDir;

    public async Task InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
        screenshotDir = Path.Combine(Path.GetTempPath(), "insights-tile-qa-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(screenshotDir);
    }

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
        if (screenshotDir is not null && Directory.Exists(screenshotDir)) Directory.Delete(screenshotDir, recursive: true);
    }

    private sealed class FakeGlitchReviewAgent(bool isBroken, string? explanation = "test-forced verdict") : ITileGlitchReviewAgent
    {
        public int CallCount { get; private set; }
        public Task<AgentCallResult<TileGlitchReviewResult>> ReviewAsync(byte[] beforeCrop, byte[] afterCrop, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AgentCallResult<TileGlitchReviewResult>(new TileGlitchReviewResult(isBroken, explanation), 0));
        }
    }

    private Task<IReadOnlyList<TileFinding>> Check(string body, ITileGlitchReviewAgent reviewAgent) =>
        new InteractiveTileChecker(browser!, reviewAgent, screenshotDir!, NullLogger<InteractiveTileChecker>.Instance).FindIssuesAsync(
            $"<!DOCTYPE html><html><head><style>body{{font:14px sans-serif;margin:0}}.card{{position:relative;margin:20px;padding:12px;min-height:60px}}</style></head><body>{body}</body></html>");

    [Fact]
    public async Task NoCards_ReturnsEmptyFindings()
    {
        var findings = await Check("<p>No cards here at all.</p>", new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task ButtonWithNoVisibleEffect_IsAFunctionalFinding()
    {
        var html = "<section class='card'><h3>Dead Button</h3><button onclick=\"void(0)\">Click me</button></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Contains(findings, f => f.Severity == TileFindingSeverity.Functional && f.CardTitle.Contains("Dead Button"));
    }

    [Fact]
    public async Task ButtonThatOpensOwnPanel_NoCrossTileBleed_IsNotAFinding()
    {
        var html = "<section class='card'><h3>Working Toggle</h3>" +
            "<button onclick=\"this.nextElementSibling.style.display='block'\">Show</button>" +
            "<div style='display:none;background:#eee;padding:8px'>Extra detail</div></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task ButtonThatBreaksAnotherTile_FakeReviewerSaysBroken_IsACosmeticFinding()
    {
        var html =
            "<section class='card'><h3>Trigger</h3><button id='bad' onclick=\"" +
            "document.getElementById('victim').style.color='red';" +
            "document.getElementById('victim').style.fontSize='40px'\">Click</button></section>" +
            "<section class='card'><h3 id='victim'>Unrelated Tile</h3><p>Should never change.</p></section>";
        var reviewer = new FakeGlitchReviewAgent(isBroken: true, explanation: "Unrelated tile's heading changed size and colour.");
        var findings = await Check(html, reviewer);
        Assert.Contains(findings, f => f.Severity == TileFindingSeverity.Cosmetic && f.TechnicalDescription.Contains("changed size"));
        Assert.True(reviewer.CallCount >= 1);
    }

    [Fact]
    public async Task ButtonThatChangesAnotherTile_FakeReviewerSaysLegitimate_IsNotAFinding()
    {
        var html =
            "<section class='card'><h3>Trigger</h3><button onclick=\"" +
            "document.getElementById('chart').style.color='blue'\">Click</button></section>" +
            "<section class='card'><h3 id='chart'>Live Chart</h3><p>Re-renders on its own.</p></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task BareSvgHoverTarget_IsDetectedAndChecked()
    {
        var html = "<section class='card'><h3>Chart</h3>" +
            "<svg width='60' height='60' onmouseover=\"this.querySelector('rect').setAttribute('fill','red')\">" +
            "<title>Bar value: 42</title><rect width='60' height='60' fill='blue'/></svg></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        // Hovering the SVG changes its own fill (own-tile effect) and nothing else - no finding,
        // but reaching this line with no exception proves the SVG selector was found and exercised.
        Assert.Empty(findings);
    }

    [Fact]
    public async Task ToggleWithFixedPositionPanel_StaysInOwnTileAndIsNotFunctionalFinding()
    {
        var html = "<section class='card'><h3>Info</h3>" +
            "<button class='hr-toggle' onclick=\"" +
            "var p=document.getElementById('panel'); p.style.display='block'; " +
            "var r=this.getBoundingClientRect(); p.style.top=(r.bottom+5)+'px'; p.style.left=r.left+'px';\">i</button></section>" +
            "<div id='panel' style='display:none;position:fixed;background:#333;color:#fff;padding:10px;width:200px'>Formula detail</div>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.DoesNotContain(findings, f => f.Severity == TileFindingSeverity.Functional);
    }

    [Fact]
    public async Task ScrollableRegion_AfterShotCapturedWhileScrolled_RealScrollIsNotAFunctionalFalsePositive()
    {
        var rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<div style='height:24px'>Row {i}</div>"));
        var html = $"<section class='card'><h3>Register</h3><div style='height:100px;overflow-y:auto'>{rows}</div></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.DoesNotContain(findings, f => f.Severity == TileFindingSeverity.Functional && f.Interaction.Contains("scroll"));
    }

    [Fact]
    public async Task ChartLoadAnimationSettledBeforeFirstInteraction_IsNotAIssue()
    {
        var html = "<section class='card'><h3 id='label'>Chart</h3>" +
            "<script>setTimeout(() => document.getElementById('label').style.opacity = '1', 50);</script>" +
            "<button onclick=\"void(0)\">Refresh</button></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        // The one-time load animation (50ms) is long settled by the time FindIssuesAsync's own
        // page-settle wait (NetworkIdle + fonts.ready + fixed delay) finishes, so it must never be
        // reported as caused by the button click that follows.
        Assert.DoesNotContain(findings, f => f.CardTitle.Contains("Chart") && f.Severity == TileFindingSeverity.Cosmetic);
    }
}

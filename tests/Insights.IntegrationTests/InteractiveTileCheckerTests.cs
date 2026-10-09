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
/// [REWORKED 2026-10-09] The checker now samples one element per kind per card, never reports an
/// inert svg hover, runs under a budget, captures page errors and writes nothing to disk unless a
/// lab directory is given - each of those has a test below.
/// </summary>
public sealed class InteractiveTileCheckerTests : IAsyncLifetime
{
    private IPlaywright? playwright;
    private IBrowser? browser;

    public async Task InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
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

    private static string Page(string body) =>
        $"<!DOCTYPE html><html><head><style>body{{font:14px sans-serif;margin:0}}.card{{position:relative;margin:20px;padding:12px;min-height:60px}}</style></head><body>{body}</body></html>";

    private Task<TileCheckResult> CheckFull(string body, ITileGlitchReviewAgent reviewAgent, TileCheckRequest? request = null) =>
        new InteractiveTileChecker(browser!, reviewAgent, NullLogger<InteractiveTileChecker>.Instance)
            .FindIssuesAsync(Page(body), request ?? new TileCheckRequest());

    private async Task<IReadOnlyList<TileFinding>> Check(string body, ITileGlitchReviewAgent reviewAgent) =>
        (await CheckFull(body, reviewAgent)).Findings;

    [Fact]
    public async Task NoCards_ReturnsEmptyFindings()
    {
        var findings = await Check("<p>No cards here at all.</p>", new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task ButtonWithNoVisibleEffect_IsAFunctionalFinding_WithOrdinalAndKind()
    {
        var html = "<section class='card'><h3>Dead Button</h3><button onclick=\"void(0)\">Click me</button></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        var finding = Assert.Single(findings, f => f.Severity == TileFindingSeverity.Functional);
        Assert.Equal("Dead Button", finding.CardTitle);
        Assert.Equal(0, finding.CardOrdinal);
        Assert.Equal("button", finding.ElementKind);
        Assert.Equal(string.Empty, finding.BeforeScreenshotPath); // nothing written in prod mode
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
    public async Task SvgWithTitle_IsHovered_OwnTileChangeIsNotAFinding()
    {
        var html = "<section class='card'><h3>Chart</h3>" +
            "<svg width='60' height='60' onmouseover=\"this.querySelector('rect').setAttribute('fill','red')\">" +
            "<title>Bar value: 42</title><rect width='60' height='60' fill='blue'/></svg></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    /// <summary>[CHANGED 2026-10-09] A hover on a mark that shows nothing (native title tooltips never
    /// render in screenshots) used to be a Functional finding no patch could fix - 0 of 6 real runs
    /// converged. It is no longer a finding of any kind.</summary>
    [Fact]
    public async Task SvgHoverWithNoVisibleChange_IsNeverAFinding()
    {
        var html = "<section class='card'><h3>Chart</h3>" +
            "<svg width='60' height='60'><title>Bar value: 42</title><rect width='60' height='60' fill='blue'/></svg></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    /// <summary>[ADDED 2026-10-09] One element per kind per card: three dead buttons produce ONE finding.</summary>
    [Fact]
    public async Task ThreeDeadButtonsInOneCard_ProduceOneFinding()
    {
        var html = "<section class='card'><h3>Buttons</h3>" +
            "<button onclick=\"void(0)\">A</button><button onclick=\"void(0)\">B</button><button onclick=\"void(0)\">C</button></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Single(findings);
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

    /// <summary>[ADDED 2026-10-09] The hidden 1x1 checkbox behind a CSS toggle is never the thing to click.</summary>
    [Fact]
    public async Task HiddenCheckboxBehindAToggle_IsNotTagged_VisibleLabelIs()
    {
        var html = "<style>.hr-panel{display:none}.hr-toggle:checked ~ .hr-panel{display:block;background:#333;color:#fff;padding:10px}</style>" +
            "<section class='card'><h3>Help</h3><div class='hr'>" +
            "<input type='checkbox' class='hr-toggle' id='hr-x' style='position:absolute;width:1px;height:1px;opacity:0'>" +
            "<label class='hr-i' for='hr-x' style='display:inline-block;width:20px;height:20px;background:#09c'>i</label>" +
            "<aside class='hr-panel'>How to read this chart, in several words.</aside></div></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings); // the label click opens the panel: real change, no finding, no exception
    }

    [Fact]
    public async Task ScrollableRegion_RealScrollIsNotAFunctionalFalsePositive()
    {
        var rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<div style='height:24px'>Row {i}</div>"));
        var html = $"<section class='card'><h3>Register</h3><div style='height:100px;overflow-y:auto'>{rows}</div></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.DoesNotContain(findings, f => f.Severity == TileFindingSeverity.Functional && f.Interaction.Contains("scroll"));
    }

    [Fact]
    public async Task ToggleInsideACardBelowTheFold_AfterRealScroll_IsNotAFalsePositive()
    {
        var spacer = "<div style='height:1800px'>Spacer so the real card sits below the fold.</div>";
        var html = spacer + "<section class='card'><h3>Info</h3>" +
            "<button class='hr-toggle' onclick=\"" +
            "var p=document.getElementById('panel'); p.style.display='block'; " +
            "var r=this.getBoundingClientRect(); p.style.top=(r.bottom+5)+'px'; p.style.left=r.left+'px';\">i</button></section>" +
            "<div id='panel' style='display:none;position:fixed;background:#333;color:#fff;padding:10px;width:200px'>Formula detail</div>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.DoesNotContain(findings, f => f.Severity == TileFindingSeverity.Functional);
    }

    [Fact]
    public async Task DecorativeContentInsideAHelpPanel_IsNeverTaggedAsInteractive()
    {
        var html = "<section class='card'><h3>Info</h3><div class='hr'>" +
            "<button class='hr-toggle' onclick=\"" +
            "var p=document.getElementById('panel'); p.style.visibility='visible'; p.style.opacity='1'; " +
            "var r=this.getBoundingClientRect(); p.style.top=(r.bottom+5)+'px'; p.style.left=r.left+'px';\">i</button>" +
            "<aside id='panel' class='hr-panel' style='visibility:hidden;opacity:0;position:fixed;background:#333;color:#fff;padding:10px;width:200px'>" +
            "<p>How to read this chart</p>" +
            "<svg width='30' height='30'><circle cx='15' cy='15' r='10' fill='red'/></svg>" +
            "<button onclick=\"void(0)\">Decorative, not real</button>" +
            "</aside></div></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(findings);
    }

    /// <summary>[ADDED 2026-10-09] Uncaught page errors are reported, not swallowed - the orchestrator
    /// reverts a patch that introduces one.</summary>
    [Fact]
    public async Task UncaughtPageError_IsCaptured()
    {
        var html = "<section class='card'><h3>Timeline</h3></section><script>document.getElementById('timeline').firstChild;</script>";
        var result = await CheckFull(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Contains(result.PageErrors, e => e.Contains("firstChild") || e.Contains("null"));
    }

    /// <summary>[ADDED 2026-10-09] OnlyCardOrdinals limits the pass to the named cards.</summary>
    [Fact]
    public async Task OnlyCardOrdinals_SkipsTheOtherCards()
    {
        var html = "<section class='card'><h3>First</h3><button onclick=\"void(0)\">dead</button></section>" +
                   "<section class='card'><h3>Second</h3><button onclick=\"void(0)\">dead</button></section>";
        var result = await CheckFull(html, new FakeGlitchReviewAgent(isBroken: false), new TileCheckRequest(OnlyCardOrdinals: [1]));
        var finding = Assert.Single(result.Findings);
        Assert.Equal(1, finding.CardOrdinal);
        Assert.Equal("Second", finding.CardTitle);
    }

    /// <summary>[ADDED 2026-10-09] A card inserted by script has no static ordinal and is not tested.</summary>
    [Fact]
    public async Task ScriptInsertedCard_IsNotTested()
    {
        var html = "<section class='card'><h3>Static</h3></section>" +
                   "<script>var s=document.createElement('section');s.className='card';s.innerHTML='<h3>Dynamic</h3><button onclick=\"void(0)\">dead</button>';document.body.appendChild(s);</script>";
        var result = await CheckFull(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.Empty(result.Findings);
    }

    /// <summary>[ADDED 2026-10-09] The pass budget returns partial results instead of running for an hour.</summary>
    [Fact]
    public async Task ExhaustedBudget_ReturnsTruncated_NotAnException()
    {
        var cards = string.Concat(Enumerable.Range(1, 30).Select(i =>
            $"<section class='card'><h3>Card {i}</h3><button onclick=\"void(0)\">dead</button></section>"));
        // BudgetSeconds is floored at 10 inside the checker; 30 cards x (2 screenshots + settle) comfortably exceeds it.
        var result = await CheckFull(cards, new FakeGlitchReviewAgent(isBroken: false), new TileCheckRequest(BudgetSeconds: 1));
        Assert.True(result.Truncated);
        Assert.True(result.Findings.Count < 30);
    }

    /// <summary>[ADDED 2026-10-09] Lab mode writes screenshots for findings only, under a per-call folder.</summary>
    [Fact]
    public async Task LabScreenshotDirectory_WritesOnlyForFindings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "insights-tile-qa-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var html = "<section class='card'><h3>Dead</h3><button onclick=\"void(0)\">dead</button></section>" +
                       "<section class='card'><h3>Fine</h3><button onclick=\"this.textContent='clicked and grown a lot wider'\">ok</button></section>";
            var result = await CheckFull(html, new FakeGlitchReviewAgent(isBroken: false), new TileCheckRequest(ScreenshotDirectory: dir));
            var finding = Assert.Single(result.Findings);
            Assert.True(File.Exists(finding.BeforeScreenshotPath));
            Assert.True(File.Exists(finding.AfterScreenshotPath));
            Assert.Equal(2, Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ChartLoadAnimationSettledBeforeFirstInteraction_IsNotAIssue()
    {
        var html = "<section class='card'><h3 id='label'>Chart</h3>" +
            "<script>setTimeout(() => document.getElementById('label').style.opacity = '1', 50);</script>" +
            "<button onclick=\"void(0)\">Refresh</button></section>";
        var findings = await Check(html, new FakeGlitchReviewAgent(isBroken: false));
        Assert.DoesNotContain(findings, f => f.CardTitle.Contains("Chart") && f.Severity == TileFindingSeverity.Cosmetic);
    }
}

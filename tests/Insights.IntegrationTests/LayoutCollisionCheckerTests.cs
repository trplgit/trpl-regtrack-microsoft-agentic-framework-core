using Insights.Presentation;
using Microsoft.Playwright;

namespace Insights.IntegrationTests;

/// <summary>
/// [ADDED 2026-09-28] LayoutCollisionChecker in real Chromium against hand-made pages: each kind of
/// problem it must catch, and the false alarms it must not raise (found while calibrating it on real
/// reports: a hidden screen-reader label, rows parked behind a scroll edge).
/// </summary>
public sealed class LayoutCollisionCheckerTests : IAsyncLifetime
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

    private Task<IReadOnlyList<string>> Check(string body) =>
        new LayoutCollisionChecker(browser!).FindIssuesAsync(
            $"<!DOCTYPE html><html><head><style>body{{font:14px sans-serif;margin:20px}}</style></head><body>{body}</body></html>");

    [Fact]
    public async Task CleanPage_HasNoIssues()
    {
        var issues = await Check("<div style='background:#eee;padding:8px;width:300px'>Short label</div><p>Another line</p>");
        Assert.Empty(issues);
    }

    [Fact]
    public async Task TwoLabelsOnTopOfEachOther_AreCaught()
    {
        var issues = await Check("<div style='position:relative;height:40px'>" +
            "<span style='position:absolute;left:0;top:0'>Factories Act, 1948</span>" +
            "<span style='position:absolute;left:10px;top:2px'>Industries Act, 1951</span></div>");
        Assert.Contains(issues, i => i.Contains("overlaps"));
    }

    [Fact]
    public async Task TextSpillingOutOfItsTile_IsCaught()
    {
        var issues = await Check("<div style='background:#e0a106;width:80px;height:40px;white-space:nowrap'>Electricity Act, 2003 and Central Electricity Rules</div>");
        Assert.Contains(issues, i => i.Contains("spills outside"));
    }

    [Fact]
    public async Task TextCutOffAtAChartEdge_IsCaught()
    {
        var issues = await Check("<div style='overflow:hidden;width:90px;height:30px;white-space:nowrap'>Sexual Harassment of Women at Workplace Act</div>");
        Assert.Contains(issues, i => i.Contains("cut off"));
    }

    [Fact]
    public async Task TextUnderAnIcon_IsCaught()
    {
        var issues = await Check("<div style='position:relative;width:300px;height:30px'>" +
            "<span>Industries (Development) Act</span>" +
            "<svg style='position:absolute;left:40px;top:0' width='18' height='18'><rect width='18' height='18'/></svg></div>");
        Assert.Contains(issues, i => i.Contains("icon"));
    }

    [Fact]
    public async Task HiddenScreenReaderLabel_IsNotAnIssue()
    {
        var issues = await Check("<label style='position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0)'>Search departments</label>" +
            "<input placeholder='Search'> <button>Show</button> <button>All</button>");
        Assert.Empty(issues);
    }

    [Fact]
    public async Task RowsBehindAScrollEdge_AreNotIssues()
    {
        var rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<div style='height:24px'>Licence type {i}</div>"));
        var issues = await Check($"<div style='height:120px;overflow:auto'>{rows}</div><div style='background:#eee;padding:6px'>Worth checking: something</div>");
        Assert.Empty(issues);
    }
}

using System.Text;
using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-28] Needle-in-a-haystack micro tests for Vision QA (Llm:VisionQa:Model, now
/// gpt-5.6-terra). Haystack = a real, clean rendered report. Needle = ONE small layout defect
/// planted by a script in the MIDDLE of the page (or inside a tab that is not open by default),
/// after the report's own scripts have run. Each case goes through the exact production path:
/// PlaywrightReportQa screenshots (full page + one per .di-tab) -> MafVisionQaAgent with the real
/// prompts/06_vision_qa.md. A case passes when the verdict is right: a planted defect is found,
/// a clean report is NOT flagged (false alarms matter as much - the gate re-renders on a "yes").
///
/// Before the model is called, the needle is proved present in a real page (data-needle marker,
/// and the target's text is recorded) - a miss can never be an injection that silently did nothing.
///
/// NOT part of the automated suite - real model calls cost money. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~VisionQaNeedleLabTest
/// Reads D:\trpl-reginsights-dev\appsettings.uat.json. Screenshots + scorecard go to
/// D:\trpl-reginsights-dev\vision-needle-tests\.
/// </summary>
public sealed class VisionQaNeedleLabTest(ITestOutputHelper output) : IAsyncLifetime
{
    private const string HaystackDir = @"D:\trpl-reginsights-dev\7dim-60day";
    private const string OutDir = @"D:\trpl-reginsights-dev\vision-needle-tests";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private IPlaywright? playwright;
    private IBrowser? browser;

    public async Task InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
        Directory.CreateDirectory(OutDir);
    }

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
    }

    // The haystacks: real reports from the 7-dimension run (tenant 1285). Entity has 6 CSS tabs.
    public static readonly string[] Haystacks = ["licence", "users", "departments", "backlogaging", "entity"];

    /// <summary>
    /// Each needle is a JS body run with `t` = the element it damages. Kept small and local - the
    /// point is a single defect in an otherwise correct, long page.
    /// </summary>
    private static readonly Dictionary<string, (string Js, string Meaning)> Needles = new()
    {
        ["heading-overlap"] = ("var h=t.querySelector('h2,h3,h4,.di-pane__title,.card-title')||t; h.setAttribute('data-needle','1'); h.style.position='relative'; h.style.top='46px'; h.style.zIndex='5';",
            "a section heading pushed down so it is drawn on top of the content under it"),
        ["block-overlap"] = ("t.setAttribute('data-needle','1'); t.style.position='relative'; t.style.marginTop='-170px'; t.style.zIndex='4';",
            "a whole card slid up so it covers the bottom of the card above it"),
        ["text-spill"] = ("var p=[].slice.call(t.querySelectorAll('p,li,span,div')).filter(function(e){return e.children.length===0&&e.textContent.trim().length>45;})[0]||t; p.setAttribute('data-needle','1'); p.style.whiteSpace='nowrap'; p.style.overflow='visible'; p.style.maxWidth='none'; p.style.width='260px'; var a=p; while(a&&a!==document.body){a.style.overflow='visible'; a=a.parentElement;}",
            "a long sentence forced onto one line so it runs far outside its card"),
        ["clipped"] = ("t.setAttribute('data-needle','1'); t.style.maxHeight='190px'; t.style.overflow='hidden';",
            "a card cut off part-way through its chart/table"),
        ["chart-blank"] = ("t.setAttribute('data-needle','1'); [].slice.call(t.querySelectorAll('svg,canvas,table,[class*=chart],[class*=plot],[class*=bar],[class*=grid],[class*=dot],[class*=tile]')).forEach(function(e){e.style.visibility='hidden';});",
            "a section whose chart area rendered completely empty under its heading"),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var h in Haystacks)
        {
            yield return [h, "clean", "middle"];
            foreach (var n in Needles.Keys)
                yield return [h, n, "middle"];
        }
        // Needles hidden in a tab that is NOT open by default (Entity, tab 4 "Operations").
        yield return ["entity", "heading-overlap", "tab4"];
        yield return ["entity", "block-overlap", "tab4"];
        yield return ["entity", "clipped", "tab4"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VisionQa_FindsThePlantedDefect_AndDoesNotFlagCleanReports(string haystack, string needle, string where)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(HaystackDir, haystack + ".html"));
        var planted = needle == "clean" ? html : Plant(html, needle, where);

        // Prove the needle really is in the page before asking the model anything.
        var target = needle == "clean" ? "(none)" : await NeedleTargetAsync(planted, where);
        Assert.False(target is null, $"Needle '{needle}' did not attach to anything in {haystack} ({where}) - fix the injection, not the model.");

        var qa = await new PlaywrightReportQa(browser!).RunAsync(planted);
        var agent = new MafVisionQaAgent(MafAgentFactory.CreateJsonAgent(
            Require("Llm:VisionQa:Endpoint"), Require("Llm:VisionQa:Model"), Require("Llm:VisionQa:ApiKey"),
            "VisionQaAgent", "Checks a real screenshot of the rendered report for overlap or broken layout only.",
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prompts", "06_vision_qa.md"))));
        var result = await agent.ReviewAsync(qa.Screenshots);

        var expectDefect = needle != "clean";
        // A "yes" only counts for a needle when the issue points at the needle's own section - a real
        // pre-existing defect elsewhere on the page must not be scored as finding the needle
        // (found in the first run: 10 of 27 "passes" named an unrelated, pre-existing defect).
        var located = expectDefect && result.Value.HasVisualDefect && PointsAtNeedle(result.Value.Issue, target!, where);
        var correct = expectDefect ? located : !result.Value.HasVisualDefect;
        var caseName = $"{haystack}__{needle}__{where}";
        for (var i = 0; i < qa.Screenshots.Count; i++)
            await File.WriteAllBytesAsync(Path.Combine(OutDir, $"{caseName}__shot{i + 1}.png"), qa.Screenshots[i]);

        var line = $"| {(correct ? "PASS" : "FAIL")} | {haystack} | {needle} | {where} | {qa.Screenshots.Count} | {(result.Value.HasVisualDefect ? "defect" : "clean")} | {Clean(target)} | {Clean(result.Value.Issue)} | {result.TotalTokens} | {Require("Llm:VisionQa:Model")} |";
        await AppendScorecardAsync(line);
        output.WriteLine(line);

        Assert.True(correct, expectDefect
            ? $"MISSED{(result.Value.HasVisualDefect ? " (named a different problem)" : "")}: {Needles[needle].Meaning} (target: {target}) in {haystack} ({where}). Model said: {result.Value.Issue ?? "no defect"}"
            : $"FALSE ALARM on the clean {haystack} report: {result.Value.Issue}");
    }

    private static string Plant(string html, string needle, string where)
    {
        // Middle of the page = the middle section.card; tab4 = the first card-like block in pane 4.
        var pick = where == "tab4"
            ? "var s=document.querySelector('#di-pane-4'); var t=s&&(s.querySelector('.di-comp,.card,section,article')||s);"
            : "var cs=document.querySelectorAll('section.card,section.di-card,section'); var t=cs.length?cs[Math.floor(cs.length/2)]:null;";
        var script = $"<script>(function(){{function go(){{ if(document.querySelector('[data-needle]'))return; {pick} if(!t)return; {Needles[needle].Js} }} go(); window.addEventListener('load', go);}})();</script>";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + script : html.Insert(at, script);
    }

    private async Task<string?> NeedleTargetAsync(string html, string where)
    {
        var page = await browser!.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1200, Height = 800 } });
        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.NetworkIdle });
            return await page.EvaluateAsync<string?>(
                "() => { const n = document.querySelector('[data-needle]'); if (!n) return null; " +
                "const sec = n.closest('.di-comp,.di-pane,section.card') || n; const h = n.matches('h2,h3,h4,.di-pane__title') ? n : sec.querySelector('h2,h3,h4,.di-pane__title'); " +
                "return (h ? h.textContent : n.textContent).replace(/\\s+/g,' ').trim().slice(0, 70); }");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task AppendScorecardAsync(string line)
    {
        var path = Path.Combine(OutDir, "scorecard.md");
        if (!File.Exists(path))
            await File.WriteAllTextAsync(path,
                "| Result | Haystack | Needle | Where | Shots | Verdict | Needle placed in section | Model's issue | Tokens | Model |\n" +
                "|---|---|---|---|---|---|---|---|---|---|\n", Encoding.UTF8);
        await File.AppendAllTextAsync(path, line + "\n", Encoding.UTF8);
    }

    /// <summary>
    /// The issue sentence must name the needle's section: at least two of the section heading's
    /// longer words, or (for a tab needle) the tab's own name.
    /// </summary>
    internal static bool PointsAtNeedle(string? issue, string target, string where)
    {
        if (string.IsNullOrWhiteSpace(issue)) return false;
        var text = issue.ToLowerInvariant();
        if (where == "tab4" && text.Contains("operations")) return true;
        var words = target.ToLowerInvariant().Split([' ', ',', '.', '-', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 5).Distinct().ToList();
        return words.Count(text.Contains) >= Math.Min(2, words.Count);
    }

    private static string Clean(string? s) => (s ?? "").Replace("|", "/").Replace("\n", " ");

    private static string Require(string key) =>
        Config[key] ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json before running this lab test.");
}

using Insights.Domain;
using Microsoft.Playwright;

namespace Insights.Presentation;

public interface IReportQaRunner
{
    /// <summary>
    /// Renders the (normalizer-approved, DOMPurify-sanitized) report in a real headless Chromium
    /// page and checks it actually looks right - console errors, horizontal overflow (the prompt
    /// explicitly forbids fixed positioning and wants print-friendly layout; overflow is the
    /// cheapest automatable proxy for "this document breaks its own layout rules"), and one
    /// full-page screenshot per tab/state for review. Cosmetic only - see
    /// <see cref="Insights.Domain.ReportQaResult"/>.
    /// </summary>
    Task<ReportQaResult> RunAsync(string html, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReportQaRunner"/>
public sealed class PlaywrightReportQa(IBrowser browser) : IReportQaRunner
{
    public async Task<ReportQaResult> RunAsync(string html, CancellationToken cancellationToken = default)
    {
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1200, Height = 800 } });
        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
                consoleErrors.Add(message.Text);
        };

        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var hasOverflow = await page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");

            var screenshots = new List<byte[]>
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }),
            };

            /*  [ADDED 2026-09-14] One more real screenshot per tab, so a vision-model review sees
                every state the document actually has, not just whatever renders by default. Every
                freehand render prompt's own god-rails name `.di-tab` as the required tab-trigger
                class (CSS-only, radio-driven) - clicking each one is the same interaction a real
                user's click performs, so this captures exactly what they would see. Zero tabs
                (a single long scroll - explicitly allowed) means this loop simply does not run,
                leaving the one default screenshot above as the only one.                        */
            var tabs = await page.QuerySelectorAllAsync(".di-tab");
            foreach (var tab in tabs)
            {
                await tab.ClickAsync();
                screenshots.Add(await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }));
            }

            return new ReportQaResult(
                HasIssues: consoleErrors.Count > 0 || hasOverflow,
                ConsoleErrors: consoleErrors,
                HasHorizontalOverflow: hasOverflow,
                Screenshots: screenshots);
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

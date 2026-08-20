using Insights.Domain;
using Microsoft.Playwright;

namespace Insights.Presentation;

public interface IReportQaRunner
{
    /// <summary>
    /// Renders the (normalizer-approved, DOMPurify-sanitized) report in a real headless Chromium
    /// page and checks it actually looks right - console errors, horizontal overflow (the prompt
    /// explicitly forbids fixed positioning and wants print-friendly layout; overflow is the
    /// cheapest automatable proxy for "this document breaks its own layout rules"), and a
    /// full-page screenshot for human review. Cosmetic only - see
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

            var screenshot = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });

            return new ReportQaResult(
                HasIssues: consoleErrors.Count > 0 || hasOverflow,
                ConsoleErrors: consoleErrors,
                HasHorizontalOverflow: hasOverflow,
                Screenshot: screenshot);
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

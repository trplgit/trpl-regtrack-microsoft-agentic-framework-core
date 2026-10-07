using System.Drawing;
using Insights.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SkiaSharp;

namespace Insights.Presentation;

public enum TileFindingSeverity { Cosmetic, Functional }

/// <summary>
/// [ADDED 2026-10-07] One real problem InteractiveTileChecker found while exercising a report's
/// interactive elements tile by tile. CardSelector/Interaction are stable enough to re-locate the
/// same element after a patch (see docs/superpowers/specs/2026-10-07-interactive-tile-qa-design.md
/// Section 1). Severity drives the orchestrator's patch-loop exhaustion behaviour (Section 4):
/// Functional means the control itself did not appear to do anything when triggered (own tile
/// showed no visible change); Cosmetic means triggering it visibly broke a DIFFERENT part of the
/// page (cross-tile bleed), confirmed by a real vision-model review of the before/after crop.
/// </summary>
public sealed record TileFinding(
    string CardSelector,
    string CardTitle,
    string Interaction,
    TileFindingSeverity Severity,
    string TechnicalDescription,
    string BeforeScreenshotPath,
    string AfterScreenshotPath);

public interface IInteractiveTileChecker
{
    Task<IReadOnlyList<TileFinding>> FindIssuesAsync(string html, CancellationToken cancellationToken = default);
}

public sealed class InteractiveTileChecker(
    IBrowser browser, ITileGlitchReviewAgent glitchReviewAgent, string screenshotDirectory, ILogger<InteractiveTileChecker> logger)
    : IInteractiveTileChecker
{
    public const int ViewportWidth = 1280;
    private const int SettleMs = 400;
    private const int TileMargin = 24;
    // [CALIBRATED LIVE] A real one-line text reveal inside a 1240px-wide card measured a 0.3%
    // own-tile change ratio (most of a wide card stays visually empty even when it does something
    // real) - 0.01 (1%) missed it. Set below that real measurement with margin, still comfortably
    // above pure floating/anti-aliasing noise (measured ~0.0 on a genuinely inert control).
    private const double OwnTileChangeThreshold = 0.001;
    private const int CrossTileBlockSize = 40;
    // [CALIBRATED LIVE] A real cross-tile text-colour change (black -> blue heading) measured
    // 2.875% of its own 40x40 block - most of a text glyph's bounding block is still background.
    // 0.15 (15%) missed it entirely. Set with margin below that real measurement.
    private const double CrossTileBlockThreshold = 0.01;
    private const int CropMargin = 16;

    public async Task<IReadOnlyList<TileFinding>> FindIssuesAsync(string html, CancellationToken cancellationToken = default)
    {
        var findings = new List<TileFinding>();
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = ViewportWidth, Height = 900 } });
        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.EvaluateAsync("() => document.fonts ? document.fonts.ready : null");
            await page.WaitForTimeoutAsync(SettleMs);

            // Stable per-run selectors, independent of DOM structure/nth-of-type ambiguity.
            await page.EvaluateAsync("() => document.querySelectorAll('section.card').forEach((el, i) => el.setAttribute('data-tile-qa-index', String(i)))");

            var cardCount = await page.EvalOnSelectorAllAsync<int>("section.card", "els => els.length");
            for (var cardIndex = 0; cardIndex < cardCount; cardIndex++)
            {
                var cardSelector = $"section.card[data-tile-qa-index=\"{cardIndex}\"]";
                var card = await page.QuerySelectorAsync(cardSelector);
                if (card is null) continue;

                var cardTitle = await card.EvaluateAsync<string>(
                    "el => (el.querySelector('h1,h2,h3,h4,.card-title')?.textContent || el.textContent || '').trim().slice(0,80)");
                await card.ScrollIntoViewIfNeededAsync();
                var box = await card.BoundingBoxAsync();
                if (box is null) continue;
                var ownTileRegion = Rectangle.Inflate(new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height), TileMargin, TileMargin);

                // Tags every element this checker treats as "interactive": the four explicit
                // selectors from the design spec's Section 2 table, PLUS any element whose
                // COMPUTED overflow-y is auto/scroll (a scrollable register/list) - that last one
                // cannot be expressed as a plain CSS selector, so it needs its own JS-side filter
                // pass rather than being folded into the querySelectorAll string above.
                await page.EvaluateAsync(
                    """
                    (sel) => {
                      const card = document.querySelector(sel);
                      if (!card) return;
                      const matches = new Set(card.querySelectorAll('.hr-toggle, .hr-i, .pf, button, svg'));
                      card.querySelectorAll('*').forEach(el => {
                        const s = getComputedStyle(el);
                        if (s.overflowY === 'auto' || s.overflowY === 'scroll') matches.add(el);
                      });
                      let i = 0;
                      matches.forEach(el => el.setAttribute('data-tile-qa-el', String(i++)));
                    }
                    """,
                    cardSelector);

                var elementCount = await page.EvalOnSelectorAllAsync<int>($"{cardSelector} [data-tile-qa-el]", "els => els.length");
                for (var elIndex = 0; elIndex < elementCount; elIndex++)
                {
                    var elSelector = $"{cardSelector} [data-tile-qa-el=\"{elIndex}\"]";
                    try
                    {
                        var finding = await CheckOneElementAsync(page, cardSelector, cardTitle, elSelector, ownTileRegion, cancellationToken);
                        if (finding is not null) findings.Add(finding);
                    }
                    catch (Exception ex)
                    {
                        // Fails soft (design spec Section 1, closing paragraph): tooling flakiness on
                        // one element never aborts the whole report's QA pass.
                        logger.LogWarning(ex, "InteractiveTileChecker: skipped element {Selector} after an error.", elSelector);
                    }
                }
            }
        }
        finally
        {
            await page.CloseAsync();
        }
        return findings;
    }

    private async Task<TileFinding?> CheckOneElementAsync(
        IPage page, string cardSelector, string cardTitle, string elSelector, Rectangle ownTileRegion, CancellationToken cancellationToken)
    {
        var element = await page.QuerySelectorAsync(elSelector);
        if (element is null) return null;

        // Class check FIRST: a <button class="hr-toggle"> must classify as 'toggle' (so it gets
        // the Escape-key reset below), not 'button' (which has none) - tagName alone would pick
        // 'button' and leave a real toggle open for every element checked after it.
        var kind = await element.EvaluateAsync<string>(
            "el => (el.classList.contains('hr-toggle') || el.classList.contains('hr-i') || el.classList.contains('pf')) ? 'toggle' : " +
            "el.tagName === 'SVG' ? 'svg' : el.tagName === 'BUTTON' ? 'button' : " +
            "(getComputedStyle(el).overflowY === 'auto' || getComputedStyle(el).overflowY === 'scroll') ? 'scroll' : 'toggle'");
        var interaction = kind switch
        {
            "svg" => "hover on chart mark",
            "button" => "click on button",
            "scroll" => "scroll register",
            _ => "hover/click on toggle",
        };

        var before = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });

        switch (kind)
        {
            case "svg":
                await element.HoverAsync();
                break;
            case "button":
            case "toggle":
                await element.HoverAsync();
                await element.ClickAsync();
                break;
            case "scroll":
                await element.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                break;
        }
        await page.WaitForTimeoutAsync(SettleMs);
        var after = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });

        try
        {
            return await ClassifyAsync(cardSelector, cardTitle, interaction, before, after, ownTileRegion, cancellationToken);
        }
        finally
        {
            switch (kind)
            {
                case "svg":
                    await page.Mouse.MoveAsync(0, 0);
                    break;
                case "toggle":
                    await page.Keyboard.PressAsync("Escape");
                    break;
                case "scroll":
                    await element.EvaluateAsync("el => el.scrollTop = 0");
                    break;
                // "button": no reset defined by the design spec's selector table - a plain button
                // click has no standard undo.
            }
        }
    }

    private async Task<TileFinding?> ClassifyAsync(
        string cardSelector, string cardTitle, string interaction, byte[] before, byte[] after, Rectangle ownTileRegion, CancellationToken cancellationToken)
    {
        var ownTileChange = PixelDiff.RegionChangeRatio(before, after, ownTileRegion);
        var beforePath = SaveScreenshot(before);
        var afterPath = SaveScreenshot(after);

        // [FOUND LIVE] Always check for a cross-tile change BEFORE deciding "nothing happened" -
        // a real own-card reveal inside a wide card can legitimately stay under OwnTileChangeThreshold
        // (a one-line text reveal inside a 1240px-wide card measured 0.3% of the card's own region,
        // confirmed live), so this alone is not reliable evidence nothing happened. More importantly:
        // if the trigger's own tile shows no change but something ELSE on the page visibly changed,
        // that is proof the interaction DID do something - just not where expected - which is a
        // cross-tile bleed (Cosmetic), not "the control does nothing" (Functional). Only when NEITHER
        // the own tile NOR anything else changed is this truly inert.
        var flagged = PixelDiff.FindChangedBlocksOutside(before, after, ownTileRegion, CrossTileBlockSize, CrossTileBlockThreshold);

        if (ownTileChange < OwnTileChangeThreshold && flagged.Count == 0)
        {
            return new TileFinding(
                cardSelector, cardTitle, interaction, TileFindingSeverity.Functional,
                $"Triggering \"{interaction}\" on \"{cardTitle}\" produced no visible change at all - the control does not appear to do anything.",
                beforePath, afterPath);
        }

        if (flagged.Count == 0) return null;

        var region = Rectangle.Inflate(flagged[0], CropMargin, CropMargin);
        var beforeCrop = CropPng(before, region);
        var afterCrop = CropPng(after, region);
        var review = await glitchReviewAgent.ReviewAsync(beforeCrop, afterCrop, cancellationToken);
        if (!review.Value.IsBroken) return null;

        return new TileFinding(
            cardSelector, cardTitle, interaction, TileFindingSeverity.Cosmetic,
            review.Value.Explanation ?? $"Triggering \"{interaction}\" on \"{cardTitle}\" visibly changed an unrelated part of the page.",
            beforePath, afterPath);
    }

    private static byte[] CropPng(byte[] png, Rectangle region)
    {
        using var image = SKBitmap.Decode(png);
        var clamped = Rectangle.Intersect(region, new Rectangle(0, 0, image.Width, image.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0) clamped = new Rectangle(0, 0, image.Width, image.Height);
        using var cropped = new SKBitmap(clamped.Width, clamped.Height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(image, new SKRect(clamped.X, clamped.Y, clamped.Right, clamped.Bottom),
                new SKRect(0, 0, clamped.Width, clamped.Height), new SKSamplingOptions());
        }
        using var skImage = SKImage.FromBitmap(cropped);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string SaveScreenshot(byte[] png)
    {
        Directory.CreateDirectory(screenshotDirectory);
        var path = Path.Combine(screenshotDirectory, $"{Guid.NewGuid()}.png");
        File.WriteAllBytes(path, png);
        return path;
    }
}

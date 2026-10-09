using System.Drawing;
using Insights.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SkiaSharp;

namespace Insights.Presentation;

public enum TileFindingSeverity { Cosmetic, Functional }

/// <summary>
/// One defect found by real browser interaction with one card.
/// [CHANGED 2026-10-09] <paramref name="CardOrdinal"/> / <paramref name="ElementKind"/> are trailing
/// optional: the ordinal is the card's static position (TileCards) so the scoped patcher can find
/// it in the stored HTML; findings recorded before this field existed carry null and fall back to
/// the legacy index in <paramref name="CardSelector"/>. The screenshot paths are empty in prod
/// (nothing is written unless TileQaOptions.ScreenshotDirectory is set) and kept for compatibility.
/// </summary>
public sealed record TileFinding(
    string CardSelector,
    string CardTitle,
    string Interaction,
    TileFindingSeverity Severity,
    string TechnicalDescription,
    string BeforeScreenshotPath,
    string AfterScreenshotPath,
    int? CardOrdinal = null,
    string? ElementKind = null);

/// <summary>Per-pass knobs - a snapshot of TileQaOptions plus the optional re-verify scope.</summary>
public sealed record TileCheckRequest(
    IReadOnlyList<int>? OnlyCardOrdinals = null,
    int BudgetSeconds = 180,
    int PerElementSeconds = 20,
    int MaxGlitchReviews = 4,
    int MaxCards = 40,
    string? ScreenshotDirectory = null);

/// <summary>
/// <paramref name="PageErrors"/>: uncaught page errors raised while loading and exercising the page
/// (a patch that introduces one is reverted by the orchestrator). <paramref name="Truncated"/>:
/// the overall budget ran out and the findings are partial.
/// </summary>
public sealed record TileCheckResult(
    IReadOnlyList<TileFinding> Findings,
    long TotalTokens,
    IReadOnlyList<string> PageErrors,
    bool Truncated);

public interface IInteractiveTileChecker
{
    Task<TileCheckResult> FindIssuesAsync(string html, TileCheckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interactive tile QA: exercises a sample of interactive elements in each card of a rendered
/// report in real Chromium and reports what did not behave.
///
/// [REWORKED 2026-10-09, FOUND LIVE] The first version (2026-10-07) tested EVERY svg, button,
/// toggle and scrollable element in every card, with two full-page screenshots each and no
/// Playwright timeout; on a real Act report (thousands of chart marks) one pass took 57 minutes,
/// and a hover on a mark with no tooltip was reported as a Functional defect that no patch could
/// ever fix - so no run ever converged. Now:
///  - at most ONE element per kind per card (toggle, button, scroll, svg), only visible ones,
///    never the hidden 1x1 input behind a toggle, only scrollables that actually overflow, only
///    svg marks that carry a title or sit in a card with a tooltip element;
///  - an svg hover that changes nothing is NOT a finding (native title tooltips never appear in
///    screenshots); Functional findings come only from toggles and buttons;
///  - 3 s Playwright default timeout, a per-element budget and an overall pass budget on a real
///    CancellationToken threaded into Playwright and the vision call; partial results come back
///    with Truncated = true rather than hanging a pod for an hour;
///  - screenshots stay full-page (a position:fixed help panel opens far from its card in document
///    coordinates, so a clip around the card produced false "does nothing" findings) but are
///    decoded once and compared over pixel spans, and there are far fewer of them;
///  - cards are keyed by static ordinal + heading fingerprint (TileCards) so a finding can be
///    located in the stored HTML without the browser; cards built by scripts do not match and are
///    not tested;
///  - nothing is written to disk unless a lab screenshot directory is configured;
///  - uncaught page errors are collected and returned.
/// </summary>
public sealed class InteractiveTileChecker(
    IBrowser browser, ITileGlitchReviewAgent glitchReviewAgent, ILogger<InteractiveTileChecker> logger)
    : IInteractiveTileChecker
{
    public const int ViewportWidth = 1280;
    private const int SettleMs = 400;
    private const int TileMargin = 24;
    private const int DefaultActionTimeoutMs = 3000;
    // [CALIBRATED LIVE] A real one-line text reveal inside a 1240px-wide card measured a 0.3%
    // own-tile change ratio - 0.01 (1%) missed it. Set below that with margin, above anti-aliasing noise.
    private const double OwnTileChangeThreshold = 0.001;
    private const int CrossTileBlockSize = 40;
    // [CALIBRATED LIVE] A real cross-tile heading colour change measured 2.875% of its 40x40 block.
    private const double CrossTileBlockThreshold = 0.01;
    private const int CropMargin = 16;

    private const string TagSampleElementsJs =
        """
        (sel) => {
          const card = document.querySelector(sel);
          if (!card) return 0;
          const inPanel = el => el.closest('.hr-panel, .pf-panel') !== null;
          const visible = el => {
            const r = el.getBoundingClientRect();
            const s = getComputedStyle(el);
            return r.width >= 4 && r.height >= 4 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
          };
          const pick = list => Array.from(list).find(el => !inPanel(el) && visible(el));
          const chosen = [];
          const toggle = pick(Array.from(card.querySelectorAll('.hr-i, .pf, label.hr-toggle, button.hr-toggle')).filter(el => el.tagName !== 'INPUT'));
          if (toggle) chosen.push(['toggle', toggle]);
          const button = pick(Array.from(card.querySelectorAll('button:not([disabled])')).filter(el => !el.classList.contains('hr-toggle') && !el.classList.contains('hr-i') && !el.classList.contains('pf')));
          if (button) chosen.push(['button', button]);
          const scroll = pick(Array.from(card.querySelectorAll('*')).filter(el => {
            const s = getComputedStyle(el);
            return (s.overflowY === 'auto' || s.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 4;
          }));
          if (scroll) chosen.push(['scroll', scroll]);
          const hasTip = card.querySelector('.tip, [role="tooltip"]') !== null;
          const svg = pick(Array.from(card.querySelectorAll('svg')).filter(el => hasTip || el.querySelector('title') !== null));
          if (svg) chosen.push(['svg', svg]);
          chosen.forEach(([kind, el], i) => { el.setAttribute('data-tile-qa-el', String(i)); el.setAttribute('data-tile-qa-kind', kind); });
          return chosen.length;
        }
        """;

    public async Task<TileCheckResult> FindIssuesAsync(string html, TileCheckRequest request, CancellationToken cancellationToken = default)
    {
        var findings = new List<TileFinding>();
        var pageErrors = new List<string>();
        var truncated = false;
        var totalTokens = 0L;
        var glitchReviews = 0;

        CleanLabScreenshots(request.ScreenshotDirectory);
        var callDirectory = request.ScreenshotDirectory is null ? null : Path.Combine(request.ScreenshotDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, request.BudgetSeconds)));
        var ct = budget.Token;

        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = ViewportWidth, Height = 900 } });
        page.SetDefaultTimeout(DefaultActionTimeoutMs);
        page.PageError += (_, e) => { lock (pageErrors) pageErrors.Add(e); };
        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
            await page.EvaluateAsync("() => document.fonts ? document.fonts.ready : null");
            await page.WaitForTimeoutAsync(SettleMs);

            // Static ordinals (what the patcher can find later) matched to browser cards in order.
            var staticCards = TileCards.Fingerprints(html);
            var browserFingerprints = await page.EvaluateAsync<string[]>(
                $"() => Array.from(document.querySelectorAll('{TileCards.CardSelector}')).map({TileCards.JsFingerprint})") ?? [];
            await page.EvaluateAsync($"() => document.querySelectorAll('{TileCards.CardSelector}').forEach((el, i) => el.setAttribute('data-tile-qa-index', String(i)))");

            var matches = MatchInOrder(staticCards, browserFingerprints);
            var only = request.OnlyCardOrdinals is null ? null : new HashSet<int>(request.OnlyCardOrdinals);
            var examined = 0;

            foreach (var (ordinal, browserIndex, fingerprint) in matches)
            {
                ct.ThrowIfCancellationRequested();
                if (only is not null && !only.Contains(ordinal)) continue;
                if (examined++ >= Math.Max(1, request.MaxCards)) break;

                var cardSelector = $"{TileCards.CardSelector}[data-tile-qa-index=\"{browserIndex}\"]";
                var card = await page.QuerySelectorAsync(cardSelector);
                if (card is null) continue;
                await card.ScrollIntoViewIfNeededAsync();
                // DOCUMENT coordinates, to match the full-page screenshots below. BoundingBoxAsync is
                // viewport-relative and was the 2026-10-08 below-the-fold false-positive bug.
                var rect = await card.EvaluateAsync<double[]>("el => { const r = el.getBoundingClientRect(); return [r.x + window.scrollX, r.y + window.scrollY, r.width, r.height]; }");
                if (rect is not { Length: 4 }) continue;
                var cardBox = new Rectangle((int)rect[0], (int)rect[1], (int)rect[2], (int)rect[3]);

                var elementCount = await page.EvaluateAsync<int>(TagSampleElementsJs, cardSelector);
                for (var elIndex = 0; elIndex < elementCount; elIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var elSelector = $"{cardSelector} [data-tile-qa-el=\"{elIndex}\"]";
                    using var perElement = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    perElement.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, request.PerElementSeconds)));
                    try
                    {
                        var canReview = glitchReviews < request.MaxGlitchReviews;
                        var (finding, tokens, reviewed) = await CheckOneElementAsync(
                            page, cardSelector, ordinal, fingerprint, elSelector, cardBox, canReview, callDirectory, perElement.Token);
                        totalTokens += tokens;
                        if (reviewed) glitchReviews++;
                        if (finding is not null) findings.Add(finding);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("InteractiveTileChecker: element {Selector} exceeded its {Seconds}s budget - skipped.", elSelector, request.PerElementSeconds);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Fails soft (design spec Section 1): tooling flakiness on one element never aborts the pass.
                        logger.LogWarning(ex, "InteractiveTileChecker: skipped element {Selector} after an error.", elSelector);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            truncated = true;
            logger.LogWarning("InteractiveTileChecker: pass budget of {Seconds}s exhausted - returning {Count} partial finding(s).", request.BudgetSeconds, findings.Count);
        }
        finally
        {
            try { await page.CloseAsync(); } catch { /* closing a page that already died is not worth a response */ }
        }

        List<string> errorsSnapshot;
        lock (pageErrors) errorsSnapshot = pageErrors.Distinct(StringComparer.Ordinal).ToList();
        return new TileCheckResult(findings, totalTokens, errorsSnapshot, truncated);
    }

    /// <summary>Walks both lists in order; a static card whose fingerprint never appears next in the browser list is skipped.</summary>
    internal static List<(int Ordinal, int BrowserIndex, string Fingerprint)> MatchInOrder(
        IReadOnlyList<(int Ordinal, string Fingerprint)> staticCards, string[] browserFingerprints)
    {
        var result = new List<(int, int, string)>();
        var j = 0;
        foreach (var (ordinal, fingerprint) in staticCards)
        {
            var k = j;
            while (k < browserFingerprints.Length && browserFingerprints[k] != fingerprint) k++;
            if (k >= browserFingerprints.Length) continue;
            result.Add((ordinal, k, fingerprint));
            j = k + 1;
        }
        return result;
    }

    private async Task<(TileFinding? Finding, long Tokens, bool Reviewed)> CheckOneElementAsync(
        IPage page, string cardSelector, int ordinal, string cardTitle, string elSelector, Rectangle cardBox,
        bool canReview, string? callDirectory, CancellationToken cancellationToken)
    {
        var element = await page.QuerySelectorAsync(elSelector);
        if (element is null) return (null, 0, false);
        var kind = await element.GetAttributeAsync("data-tile-qa-kind") ?? "toggle";
        var interaction = kind switch
        {
            "svg" => "hover on chart mark",
            "button" => "click on button",
            "scroll" => "scroll register",
            _ => "hover/click on toggle",
        };

        // Full-page captures, deliberately NOT clipped to the card: a help panel that opens as a
        // position:fixed element lands wherever the viewport is, often far from its card in document
        // coordinates, and a clip around the card turned every such real reveal into a false
        // "control does nothing" finding (caught by the below-the-fold integration test). With one
        // element per kind per card the capture count is already small; the decode/compare is span-based.
        var ownTileRegion = Rectangle.Inflate(cardBox, TileMargin, TileMargin);

        var before = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
        cancellationToken.ThrowIfCancellationRequested();
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
            return await ClassifyAsync(cardSelector, ordinal, cardTitle, kind, interaction, before, after, ownTileRegion, canReview, callDirectory, cancellationToken);
        }
        finally
        {
            try
            {
                switch (kind)
                {
                    case "toggle":
                        await page.Keyboard.PressAsync("Escape");
                        break;
                    case "scroll":
                        await element.EvaluateAsync("el => el.scrollTop = 0");
                        break;
                }
                await page.Mouse.MoveAsync(0, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "InteractiveTileChecker: reset after {Selector} failed.", elSelector);
            }
        }
    }

    private async Task<(TileFinding? Finding, long Tokens, bool Reviewed)> ClassifyAsync(
        string cardSelector, int ordinal, string cardTitle, string kind, string interaction, byte[] before, byte[] after,
        Rectangle ownTileRegion, bool canReview, string? callDirectory, CancellationToken cancellationToken)
    {
        var ownTileChange = PixelDiff.RegionChangeRatio(before, after, ownTileRegion);
        var flagged = PixelDiff.FindChangedBlocksOutside(before, after, ownTileRegion, CrossTileBlockSize, CrossTileBlockThreshold);

        if (ownTileChange < OwnTileChangeThreshold && flagged.Count == 0)
        {
            // A hover on a chart mark with no visible tooltip is NOT a defect (native <title>
            // tooltips never appear in screenshots) - only a toggle/button that does nothing is.
            if (kind is "svg" or "scroll") return (null, 0, false);
            var (b1, a1) = SaveForFinding(before, after, callDirectory);
            return (new TileFinding(
                cardSelector, cardTitle, interaction, TileFindingSeverity.Functional,
                $"Triggering \"{interaction}\" on \"{cardTitle}\" produced no visible change at all - the control does not appear to do anything.",
                b1, a1, ordinal, kind), 0, false);
        }
        if (flagged.Count == 0) return (null, 0, false);
        if (!canReview)
        {
            logger.LogInformation("InteractiveTileChecker: cross-tile change on card {Ordinal} not reviewed - glitch-review cap reached.", ordinal);
            return (null, 0, false);
        }

        var region = Rectangle.Inflate(flagged[0], CropMargin, CropMargin);
        var beforeCrop = CropPng(before, region);
        var afterCrop = CropPng(after, region);
        var review = await glitchReviewAgent.ReviewAsync(beforeCrop, afterCrop, cancellationToken);
        if (!review.Value.IsBroken) return (null, review.TotalTokens, true);
        var (b2, a2) = SaveForFinding(before, after, callDirectory);
        return (new TileFinding(
            cardSelector, cardTitle, interaction, TileFindingSeverity.Cosmetic,
            review.Value.Explanation ?? $"Triggering \"{interaction}\" on \"{cardTitle}\" visibly changed an unrelated part of the page.",
            b2, a2, ordinal, kind), review.TotalTokens, true);
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

    private (string Before, string After) SaveForFinding(byte[] before, byte[] after, string? callDirectory)
    {
        if (callDirectory is null) return (string.Empty, string.Empty);
        try
        {
            Directory.CreateDirectory(callDirectory);
            var stem = Guid.NewGuid().ToString("N")[..12];
            var b = Path.Combine(callDirectory, stem + "-before.png");
            var a = Path.Combine(callDirectory, stem + "-after.png");
            File.WriteAllBytes(b, before);
            File.WriteAllBytes(a, after);
            return (b, a);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "InteractiveTileChecker: could not write lab screenshots.");
            return (string.Empty, string.Empty);
        }
    }

    private void CleanLabScreenshots(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var sub in Directory.EnumerateDirectories(directory))
                if (Directory.GetLastWriteTimeUtc(sub) < cutoff) Directory.Delete(sub, recursive: true);
            foreach (var file in Directory.EnumerateFiles(directory))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "InteractiveTileChecker: lab screenshot cleanup failed.");
        }
    }
}

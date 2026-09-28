using Microsoft.Playwright;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-28] Finds text that a reader cannot read cleanly on the FINAL page - found live on
/// real reports: law names spilling out of their treemap tile into the next one and under the tile's
/// lock icon, location names colliding above a bar chart, rotated names cut off at a chart edge.
/// Deterministic (a real Chromium layout, no model), run on the finished HTML (after the font and
/// sanitising, so measurements match what the reader sees). Three kinds of issue:
/// <list type="bullet">
/// <item>two visible text boxes overlapping each other,</item>
/// <item>text overlapping a small icon (svg / img),</item>
/// <item>text spilling outside its own tile/bar/card, or cut off by a chart edge.</item>
/// </list>
/// Content hidden inside a scrolling area (a register or tile grid you scroll) is judged only by the
/// part actually visible, so rows parked behind a scroll edge are not reported. Closed panels,
/// tooltips and anything with display:none / visibility:hidden / opacity 0 are ignored.
/// </summary>
public interface ILayoutChecker
{
    Task<IReadOnlyList<string>> FindIssuesAsync(string html, CancellationToken cancellationToken = default);
}

public sealed class LayoutCollisionChecker(IBrowser browser) : ILayoutChecker
{
    public const int ViewportWidth = 1280;
    public const int MaxIssues = 12;

    public async Task<IReadOnlyList<string>> FindIssuesAsync(string html, CancellationToken cancellationToken = default)
    {
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = ViewportWidth, Height = 900 } });
        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.NetworkIdle });
            // Let chart scripts (which draw after load) and web fonts settle.
            await page.EvaluateAsync("() => document.fonts ? document.fonts.ready : null");
            await page.WaitForTimeoutAsync(400);
            return await page.EvaluateAsync<string[]>(Script, MaxIssues);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>The in-page detector. Public so a test can run it against a hand-made page.</summary>
    public const string Script = """
        (maxIssues) => {
          const hiddenStyle = s => s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity) === 0;
          const isVisible = el => { for (let p = el; p && p !== document.documentElement; p = p.parentElement) { if (hiddenStyle(getComputedStyle(p))) return false; } return true; };
          const inter = (a, b) => ({ l: Math.max(a.l, b.l), t: Math.max(a.t, b.t), r: Math.min(a.r, b.r), b: Math.min(a.b, b.b) });
          const area = r => Math.max(0, r.r - r.l) * Math.max(0, r.b - r.t);
          const box = r => ({ l: r.left, t: r.top, r: r.right, b: r.bottom });
          const label = s => { s = s.replace(/\s+/g, ' ').trim(); return s.length > 45 ? s.slice(0, 44) + '…' : s; };

          // Clip rectangle from every ancestor that hides overflow, and whether one of them scrolls.
          const clipInfo = el => {
            let clip = { l: -1e9, t: -1e9, r: 1e9, b: 1e9 }, scrolls = false;
            for (let p = el; p && p !== document.documentElement; p = p.parentElement) {
              if (p instanceof SVGElement && !(p instanceof SVGSVGElement)) continue;
              const s = getComputedStyle(p);
              const hides = s.overflowX !== 'visible' || s.overflowY !== 'visible' || p instanceof SVGSVGElement;
              if (!hides) continue;
              if (/(auto|scroll)/.test(s.overflowX + s.overflowY)) scrolls = true;
              clip = inter(clip, box(p.getBoundingClientRect()));
            }
            return { clip, scrolls };
          };

          // Nearest ancestor that paints a box (background or border) - the tile / bar / card the text sits in.
          const paintedBox = el => {
            for (let p = el; p && p !== document.body; p = p.parentElement) {
              if (p instanceof SVGElement) return null;
              const s = getComputedStyle(p);
              const bg = s.backgroundColor, img = s.backgroundImage;
              const painted = (bg && bg !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(bg)) || (img && img !== 'none') ||
                              parseFloat(s.borderTopWidth) > 0 || parseFloat(s.borderLeftWidth) > 0;
              if (painted) return box(p.getBoundingClientRect());
            }
            return null;
          };

          // Every piece of visible text, measured by the glyphs themselves (a Range), not the element box.
          const texts = [];
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let n = walker.nextNode(); n; n = walker.nextNode()) {
            const t = n.textContent.trim();
            if (t.length === 0) continue;
            const el = n.parentElement;
            if (!el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'OPTION', 'TITLE'].includes(el.tagName) || !isVisible(el)) continue;
            let r;
            if (el instanceof SVGElement) r = box(el.getBoundingClientRect());
            else { const range = document.createRange(); range.selectNodeContents(n); r = box(range.getBoundingClientRect()); }
            if (area(r) < 4) continue;
            const { clip, scrolls } = clipInfo(el);
            const visible = inter(r, clip);
            if (area(visible) < 4) continue; // parked behind a scroll edge / fully clipped - not seen
            texts.push({ el, t, r, visible, scrolls, clip });
          }

          const icons = [...document.querySelectorAll('svg, img')].filter(i => {
            if (!isVisible(i)) return false;
            const b = i.getBoundingClientRect();
            if (b.width < 6 || b.height < 6 || b.width > 40 || b.height > 40) return false;
            return !i.querySelector('text');
          }).map(i => ({ el: i, r: box(i.getBoundingClientRect()) }));

          const issues = [], seen = new Set();
          const add = s => { if (!seen.has(s)) { seen.add(s); issues.push(s); } };

          for (const a of texts) {
            // Cut off at a chart edge (not a deliberate scroll area).
            if (!a.scrolls && area(a.visible) < 0.8 * area(a.r)) add(`"${label(a.t)}" is cut off at the edge of its chart or box`);
            // Spilling out of its own tile / bar / card.
            // Only a clear spill counts: more than 6px AND more than 30% of the text's own size in that direction.
            const p = paintedBox(a.el);
            if (p && area(inter(a.r, p)) > 0) {
              const w = a.r.r - a.r.l, h = a.r.b - a.r.t;
              const outX = Math.max(a.r.r - p.r, p.l - a.r.l), outY = Math.max(a.r.b - p.b, p.t - a.r.t);
              if ((outX > 6 && outX > 0.3 * w) || (outY > 6 && outY > 0.3 * h)) add(`"${label(a.t)}" spills outside its tile/box`);
            }
          }
          for (let i = 0; i < texts.length; i++) {
            for (let j = i + 1; j < texts.length; j++) {
              const a = texts[i], b = texts[j];
              if (a.el === b.el || a.el.contains(b.el) || b.el.contains(a.el)) continue;
              const o = area(inter(a.visible, b.visible));
              if (o > 0.35 * Math.min(area(a.visible), area(b.visible)) && o > 10) add(`"${label(a.t)}" overlaps "${label(b.t)}"`);
            }
            for (const ic of icons) {
              if (ic.el.contains(texts[i].el) || texts[i].el.contains(ic.el)) continue;
              const o = area(inter(texts[i].visible, ic.r));
              if (o > 0.25 * area(ic.r)) add(`"${label(texts[i].t)}" runs under an icon`);
            }
            if (issues.length >= maxIssues * 3) break;
          }
          return issues.slice(0, maxIssues);
        }
        """;
}

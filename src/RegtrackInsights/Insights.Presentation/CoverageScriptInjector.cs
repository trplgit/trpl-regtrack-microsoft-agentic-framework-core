using System.Linq;
using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Deterministically embeds the REAL, tested Coverage-tile driving script (chip-filter + click-to-
/// select + detail-panel fill) into generated report HTML, the same treatment PoppinsFontInjector
/// already gives the self-hosted font - see that class's own doc comment for the general
/// reasoning.
///
/// [BUG FOUND LIVE, 2026-09-02] This script was previously something the render agent had to
/// author fresh every run - unreliable three separate ways: DOMPurify's default profile stripping
/// all &lt;script&gt; tags, DOMPurify defensively stripping a &lt;script&gt; whose content
/// contained an HTML-tag-shaped substring (the reference's own innerHTML='&lt;span...&gt;' pattern
/// - see ReportEmitNormalizer.CheckNoHtmlTagShapedTextInsideScripts's own note), and simply the LLM
/// omitting it on a given attempt. The script's LOGIC is identical for every tenant - it only ever
/// reads data-* attributes off whatever di-covtile buttons the render agent wrote, which it does
/// reliably. There is no reason to gamble on re-authoring known-correct JS every render: the render
/// agent's job for Coverage becomes only the grid markup with correct data attributes; this class
/// supplies the interactivity that makes it clickable, post-generation, always identical.
///
/// Uses createElement/className/textContent/appendChild throughout, never innerHTML with a literal
/// tag - the exact pattern confirmed live to survive DOMPurify's ADD_TAGS: ['script'] profile (see
/// DomPurifySanitizer's own [BUG FOUND LIVE] note).
/// </summary>
public static partial class CoverageScriptInjector
{
    // [BUG FOUND LIVE, 2026-09-02] Rewritten to match the REAL reference taxonomy exactly -
    // detailed-insights.data.ts's CovStatus type / COVERAGE_TEMPLATES, detailed-insights.
    // component.ts's covStatusLabel/covCoverPctLabel/covPeerGapLabel, and this repo's own
    // docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4 (status_counts: healthy/under_configured/
    // has_ownerless/unmapped - same 4 names, same distribution the Angular mock's numbers were
    // themselves copied from). The earlier version invented a 3-state model (healthy/"dark" no-
    // obligations/has_ownerless) that silently dropped the RED "unmapped" state entirely and
    // renamed under_configured - not a simplification, a different taxonomy. Never invent
    // business meaning: this class's job is to reproduce the reference's real 4-state model,
    // never a new one.
    //
    // "Coverage %" and "Peer gap" (both m/n-derived, n = obligations-mapped-count peer norm)
    // degrade to the reference's OWN designed '-' fallback (covCoverPctLabel/covPeerGapLabel
    // already treat "no peer norm" this way for n=0) because no per-branch peer-obligation-COUNT
    // norm is computed anywhere in this codebase yet (confirmed: sql/05_dimension_location.sql's
    // only real peer comparison, vs_peer_state_norm_pp, is an OVERDUE-RATE peer gap, a different
    // concept from "fewer obligations mapped than peers" - using it here would be exactly the
    // taxonomy reinterpretation this note warns against). Every tile's data-peer-norm attribute
    // is therefore expected to be absent until that real SQL work exists - this script's own
    // fallback is what makes that an honest '-', never a fabricated number.
    private const string Script = """
        <script>
        (function () {
          var map = document.querySelector('.di-covmap');
          var aside = document.querySelector('.di-covdetail--side');
          if (!map || !aside) return;
          var chips = Array.prototype.slice.call(document.querySelectorAll('.di-covchip[data-filter]'));
          chips.forEach(function (chip) {
            chip.addEventListener('click', function () {
              var key = chip.getAttribute('data-filter');
              var cur = map.getAttribute('data-filter');
              var next = key === 'all' || cur === key ? null : key;
              if (next) map.setAttribute('data-filter', next); else map.removeAttribute('data-filter');
              chips.forEach(function (c) {
                c.classList.toggle('di-covchip--active', next !== null && c.getAttribute('data-filter') === next);
              });
            });
          });
          var LBL = { healthy: 'Mapped', under_configured: 'Under-configured', has_ownerless: 'Has ownerless', unmapped: 'Unmapped' };
          var TITLE_SUFFIX = ' in ';
          var SUMMARY = {
            healthy: function (d) { return 'Fully mapped: ' + d.m + ' obligations' + (d.n ? ', at or near the regional norm of ' + d.n + '.' : '.'); },
            under_configured: function (d) { return 'Carries ' + d.m + ' obligations' + (d.n ? ', materially fewer than the ~' + d.n + ' its regional peers carry.' : ', likely under-configured relative to its regional peers.'); },
            has_ownerless: function (d) { return d.o + ' of ' + d.m + ' obligations have no performer assigned.'; },
            unmapped: function () { return 'No compliance mapped, so this location is invisible to every overdue report. Verify whether it is an operating store or a structural/incomplete record.'; }
          };
          var RECO = {
            healthy: 'No coverage action needed.',
            under_configured: 'Review applicability and complete configuration from the regional template.',
            has_ownerless: 'Assign a performer at this store.',
            unmapped: 'If operating, configure from the regional template; if structural, exclude from the operating estate.'
          };
          function pctLabel(m, n) { return n ? Math.round((m / n) * 100) + '%' : '—'; }
          function gapLabel(m, n) { var gap = n ? Math.max(0, n - m) : 0; return n && gap > 0 ? '−' + gap : '—'; }
          function fill(tile) {
            var st = tile.getAttribute('data-st');
            var branchName = tile.getAttribute('data-branch-name');
            var region = tile.getAttribute('data-state');
            var m = parseInt(tile.getAttribute('data-instances'), 10) || 0;
            var nRaw = tile.getAttribute('data-peer-norm');
            var n = nRaw ? parseInt(nRaw, 10) : null;
            var o = parseInt(tile.getAttribute('data-ownerless'), 10) || 0;
            var od = parseInt(tile.getAttribute('data-overdue'), 10) || 0;
            var performerAssigned = tile.getAttribute('data-performer') === 'true';

            var pill = aside.querySelector('.di-covdetail__pill');
            if (pill) {
              pill.className = 'di-covdetail__pill di-covdetail__pill--' + st;
              pill.textContent = '';
              var dot = document.createElement('span');
              dot.className = 'di-covdetail__dot';
              pill.appendChild(dot);
              pill.appendChild(document.createTextNode(LBL[st]));
            }
            var refEl = aside.querySelector('.di-covdetail__ref');
            if (refEl) refEl.textContent = tile.getAttribute('data-branch-id') + ' · ' + region;
            var titleEl = aside.querySelector('.di-covdetail__title');
            if (titleEl) titleEl.textContent = branchName + TITLE_SUFFIX + region;
            var summaryEl = aside.querySelector('.di-covdetail__summary');
            if (summaryEl) summaryEl.textContent = SUMMARY[st]({ m: m, n: n, o: o });

            var mv = aside.querySelectorAll('.di-covdetail__mv');
            if (mv[0]) {
              mv[0].textContent = '';
              mv[0].appendChild(document.createTextNode(String(m)));
              if (n) {
                var small = document.createElement('small');
                small.textContent = ' / ' + n + ' norm';
                mv[0].appendChild(small);
              }
            }
            if (mv[1]) mv[1].textContent = pctLabel(m, n);
            if (mv[2]) mv[2].textContent = String(o);
            if (mv[3]) mv[3].textContent = String(od);
            if (mv[4]) {
              mv[4].textContent = performerAssigned ? 'Assigned' : 'Unassigned';
              mv[4].classList.toggle('di-covdetail__mv--bad', !performerAssigned);
            }
            if (mv[5]) mv[5].textContent = gapLabel(m, n);

            var recoEl = aside.querySelector('.di-covdetail__action p');
            if (recoEl) recoEl.textContent = RECO[st];
          }
          map.querySelectorAll('.di-covtile').forEach(function (tile) {
            tile.addEventListener('click', function () {
              map.querySelectorAll('.di-covtile--selected').forEach(function (x) { x.classList.remove('di-covtile--selected'); });
              tile.classList.add('di-covtile--selected');
              fill(tile);
            });
          });
          var first = map.querySelector('.di-covtile');
          if (first) { first.classList.add('di-covtile--selected'); fill(first); }
        })();
        </script>
        """;

    /// <summary>
    /// Inserts the driving script immediately before &lt;/body&gt; - after every di-covtile the
    /// render agent wrote, so querySelectorAll('.di-covtile') at script-run time sees all of them.
    /// No-op if the document never rendered a Coverage grid at all (nothing to drive), or already
    /// has a script that references it (idempotent - never double-bind every click handler if the
    /// render agent still wrote its own, against updated instructions).
    /// </summary>
    public static string Inject(string html)
    {
        if (!HasCoverageGrid(html))
            return html;

        if (HasDrivingScriptAlready(html))
            return html;

        var match = BodyCloseTag().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("CoverageScriptInjector: no </body> tag found in generated HTML - cannot inject the coverage-tile driving script.");

        return html.Insert(match.Index, Script);
    }

    private static bool HasCoverageGrid(string html) =>
        html.Contains("di-covgrid", StringComparison.Ordinal) && html.Contains("di-covtile", StringComparison.Ordinal);

    private static bool HasDrivingScriptAlready(string html) =>
        ScriptContentToken().Matches(html).Any(m =>
            m.Groups["content"].Value.Contains(".di-covmap", StringComparison.Ordinal)
            || m.Groups["content"].Value.Contains(".di-covtile", StringComparison.Ordinal));

    [GeneratedRegex(@"</body\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyCloseTag();

    [GeneratedRegex(@"<script\b[^>]*>(?<content>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptContentToken();
}

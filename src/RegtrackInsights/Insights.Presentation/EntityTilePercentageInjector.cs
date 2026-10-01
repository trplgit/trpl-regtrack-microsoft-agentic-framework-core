using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-10-01] Deterministically wraps the headline percentage of known, stable
/// fixed_holistic tiles/cards (Tab1 Snapshot, Tab2 Risk &amp; licences, Tab4 Operations) as an
/// inline hover-link to a real fraction formula - same `.hr`/`.pf` mechanism as
/// <see cref="EntityScoreFormulaInjector"/>, reusing its shared style/script. Follow-up to that
/// class: "every tile has a score" extended to "every tile's percentage has a formula" (real user
/// ask, 2026-10-01).
///
/// Scope, this pass: headline tile/card percentages only (Licence - Expired, Backlog - overdue,
/// Timeliness current/previous, Evidence - review trail, Critical overdue, Liability overlap).
/// NOT yet covered: percentage/percentage-point figures embedded inside free prose sentences
/// (a card's own narrative paragraph, a snaptile's desc line, Tab6 Priority Actions), per-licence-
/// type rows (the single highlighted "highest Expired share" row), and the Users "busiest users"
/// stat - these sit in render-agent-authored text with no single stable anchor the way a tile's
/// own labelled value does, same class of problem `NumberFormulaInjector`'s own doc comment
/// describes for freehand prose. A real follow-up, not silently dropped.
///
/// [FIX - found live across two real renders, 2026-10-01] Tab2's `.di-kpi__pair-lbl` and Tab4's
/// `.di-fybar` label text are NOT a fixed catalog the way Tab1's Snapshot tile labels are - they
/// are free prose the render agent writes fresh each run ("Critical overdue" became "Overdue
/// rate"; "Liability overlap" became "Personal-liability overlap"; "Previous comparable period"
/// became "Comparator period" - same concept, different wording, second real run). Matching by
/// label text for these silently stops working the moment the wording drifts. `.di-kpi__pair` is
/// ALSO reused across unrelated cards/tabs (Risk, Licence's per-type highlight, Tab3 Coverage) -
/// so a document-wide positional count is not safe either, a run that drops or adds one of those
/// cards would shift every index after it. The fix: position WITHIN ITS OWN TAB PANE
/// (`id="di-pane-N"`, a real DOM boundary never relabelled) is stable, because each pane's set of
/// cards is dictated by a fixed render-order rule, not written fresh - see InjectPositional.
/// </summary>
public static class EntityTilePercentageInjector
{
    public sealed record FractionFigure(
        string Label, decimal Numerator, decimal Denominator, string NumeratorLabel, string DenominatorLabel);

    public static string Inject(string html, IReadOnlyDictionary<string, FractionFigure> figuresByLabel)
    {
        if (string.IsNullOrEmpty(html) || figuresByLabel.Count == 0)
            return html;

        var nextId = 0;
        var wrapped = false;

        // Tab1 Snapshot tiles: <div class="di-snaptile__label">{Label}</div> ... <div class="di-snaptile__num tnum">{value}%</div>
        // Label text here IS a fixed catalog (confirmed stable across two real renders) - exact
        // label matching is safe, unlike the positional-only tiles below.
        html = SnaptilePattern.Replace(html, m => WrapIfKnown(m, 1, 2, figuresByLabel, ref nextId, ref wrapped));

        if (!wrapped)
            return html;

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose < 0 ? html : html.Insert(bodyClose, NumberFormulaInjector.SharedStyle + NumberFormulaInjector.SharedScript);
    }

    /// <summary>
    /// Pane-scoped positional variant for tiles whose own label text is NOT a stable catalog (Tab2
    /// di-kpi__pair, Tab4 di-fybar, Tab4 di-stacklegend) - wraps the Nth matching value WITHIN ONE
    /// named pane, by position, never by label text. `figuresInOrder[i] == null` leaves that
    /// occurrence bare (no figure for it this run - never guessed). Safe to call multiple times
    /// (once per pane/pattern combination); each call only appends the shared style/script if it
    /// is not already present.
    /// </summary>
    public static string InjectPositional(string html, string paneId, TilePattern pattern, IReadOnlyList<FractionFigure?> figuresInOrder)
    {
        if (string.IsNullOrEmpty(html) || figuresInOrder.Count == 0)
            return html;

        var paneStart = html.IndexOf($"id=\"{paneId}\"", StringComparison.Ordinal);
        if (paneStart < 0)
            return html; // This pane did not render this run (a degraded dimension) - nothing to wrap.

        var nextPaneStart = html.IndexOf("id=\"di-pane-", paneStart + 1, StringComparison.Ordinal);
        var paneEnd = nextPaneStart < 0 ? html.Length : nextPaneStart;
        var paneHtml = html[paneStart..paneEnd];

        var regex = pattern switch
        {
            TilePattern.KpiPair => KpiPairPattern,
            TilePattern.Fybar => FybarPattern,
            TilePattern.StackLegend => StackLegendPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };

        var nextId = 0;
        var i = 0;
        var wrapped = false;
        var replaced = regex.Replace(paneHtml, m =>
        {
            var figure = i < figuresInOrder.Count ? figuresInOrder[i] : null;
            i++;
            if (figure is null)
                return m.Value; // No real figure for this position this run - leave it bare.

            // [FIX - found live, two real renders] id must include the PATTERN too, not just pane
            // + counter - two different InjectPositional calls on the SAME pane (e.g. Fybar and
            // StackLegend, both real on Tab4) each started their own counter at 0, producing two
            // checkboxes sharing id="pf-tile-di-pane-4-0" - a real duplicate-id bug, same class as
            // the close-button id-collision bug fixed earlier today, confirmed live via a real
            // rendered report (Tab4's fybar-0 and stacklegend-0 both existed).
            var id = $"pf-tile-{paneId}-{pattern}-{nextId}";
            nextId++;
            wrapped = true;
            var displayText = m.Groups[1].Value;
            var wrappedValue = BuildFractionPanel(id, displayText, figure);
            return m.Value.Replace($"{displayText}%", $"{wrappedValue}%");
        });

        if (!wrapped)
            return html;

        html = html[..paneStart] + replaced + html[paneEnd..];

        if (html.Contains(".hr-just-closed", StringComparison.Ordinal))
            return html; // Shared style/script already present from an earlier call this pipeline.

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose < 0 ? html : html.Insert(bodyClose, NumberFormulaInjector.SharedStyle + NumberFormulaInjector.SharedScript);
    }

    public enum TilePattern { KpiPair, Fybar, StackLegend }

    private static readonly Regex SnaptilePattern = new(
        """<div class="di-snaptile__label">([^<]+)</div>\s*<div class="di-snaptile__num tnum">(\d+(?:\.\d+)?)%</div>""",
        RegexOptions.Compiled);

    // Positional variants capture ONLY the value (group 1) - label text is deliberately not relied
    // on. Each is scoped to its OWN container shape (not a bare "<b class=tnum>" match), so running
    // the Fybar and StackLegend passes over the SAME pane never cross-wraps the other's values -
    // Tab4 has both a di-fybar card and a di-stacklegend card, and both use <b class="tnum">.
    private static readonly Regex KpiPairPattern = new(
        """<div class="di-kpi__pair-val tnum">(\d+(?:\.\d+)?)%</div>""",
        RegexOptions.Compiled);

    private static readonly Regex FybarPattern = new(
        """<div class="di-fybar__lbl"><span>[^<]*</span><b class="tnum">(\d+(?:\.\d+)?)%</b></div>""",
        RegexOptions.Compiled);

    private static readonly Regex StackLegendPattern = new(
        """<span class="di-stacklegend__item">.*?<b class="tnum">(\d+(?:\.\d+)?)%</b></span>""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static string WrapIfKnown(
        Match m, int labelGroup, int valueGroup, IReadOnlyDictionary<string, FractionFigure> figuresByLabel,
        ref int nextId, ref bool wrapped)
    {
        // The captured label is raw HTML (e.g. "Licence &amp;middot; Expired") - decode before
        // lookup so callers can key figuresByLabel with the plain text a human would read.
        var label = System.Net.WebUtility.HtmlDecode(m.Groups[labelGroup].Value).Trim();
        if (!figuresByLabel.TryGetValue(label, out var figure))
            return m.Value; // A real label this pass does not have formula data for - leave it bare, never guess.

        var id = $"pf-tile-{nextId}";
        nextId++;
        var displayText = m.Groups[valueGroup].Value;
        wrapped = true;
        var wrappedValue = BuildFractionPanel(id, displayText, figure);
        return m.Value.Replace($"{displayText}%", $"{wrappedValue}%");
    }

    /// <summary>
    /// Builds the `.hr`/`.pf` fraction-formula panel HTML for one real percentage - exposed
    /// (internal) so a deterministic injector that ALREADY holds the real numerator/denominator at
    /// generation time (e.g. <c>ForwardLookInjector</c>) can wrap its own number inline, without
    /// needing a find-and-match pass over its own freshly-built HTML.
    /// </summary>
    internal static string BuildFractionPanel(string id, string displayText, FractionFigure figure)
    {
        return $"""
            <span class="hr">
              <input type="checkbox" class="hr-toggle" id="{id}" aria-label="How this percentage is worked out">
              <label for="{id}" class="hr-i pf">{displayText}</label>
              <span class="hr-panel pf-panel" role="dialog" aria-label="How this percentage is worked out">
                <label for="{id}" class="hr-close" aria-label="Close">&times;</label>
                <span class="hr-title pf-title">{System.Net.WebUtility.HtmlEncode(figure.Label)} - {displayText}%</span>
                <span class="pf-formula"><span class="pf-formula-label">HOW IT IS CALCULATED</span>
                  <span class="pf-frac">
                    <span class="pf-frac-stack">
                      <span class="pf-num"><span class="pf-num-value">{figure.Numerator}</span><span class="pf-num-label">{System.Net.WebUtility.HtmlEncode(figure.NumeratorLabel)}</span></span>
                      <span class="pf-den"><span class="pf-den-value">{figure.Denominator}</span><span class="pf-den-label">{System.Net.WebUtility.HtmlEncode(figure.DenominatorLabel)}</span></span>
                    </span>
                    <span class="pf-times">&times; 100</span>
                  </span>
                </span>
              </span>
            </span>
            """;
    }
}

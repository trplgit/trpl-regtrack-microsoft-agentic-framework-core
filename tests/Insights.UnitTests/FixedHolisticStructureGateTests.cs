using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// Two real, live-confirmed bugs (see 05_report_html_holistic.md / 05_report_html_fixed_holistic.md
/// [FIX] notes): a render shipped only 4 of the 7 required score-component cards, and shipped a
/// "6" tab badge on an Actions pane whose own body said "no real data source yet in this run".
/// Pure logic, no DB, no LLM, no browser - same posture as ReportEmitNormalizer's own tests: this
/// is not a full HTML/CSS parser, it exercises exactly the two violation classes found live.
/// </summary>
public sealed class FixedHolisticStructureGateTests
{
    private const string SevenRealComponents = """
        <div class="di-components">
          <div class="di-comp"><div class="di-comp__name">Timeliness</div></div>
          <div class="di-comp"><div class="di-comp__name">Coverage</div></div>
          <div class="di-comp"><div class="di-comp__name">Overdue / Backlog</div></div>
          <div class="di-comp"><div class="di-comp__name">Risk-weighted</div></div>
          <div class="di-comp"><div class="di-comp__name">Licence</div></div>
          <div class="di-comp"><div class="di-comp__name">People</div></div>
          <div class="di-comp"><div class="di-comp__name">Evidence</div></div>
        </div>
        """;

    private const string CleanBlockedTab = """
        <label for="di-tab-6" class="di-tab" role="tab">Actions<span class="di-tab__count tnum">0</span></label>
        <section class="di-pane" id="di-pane-6" aria-label="Priority actions" data-blocked="true"></section>
        """;

    [Fact]
    public void Evaluate_Approves_WhenNoScoreHeroSectionPresent()
    {
        // A report with no composite score (e.g. every dimension degraded) never renders
        // .di-components at all - nothing to count, this is a legitimate document.
        var result = FixedHolisticStructureGate.Evaluate("<html><body>No hero here.</body></html>");

        Assert.True(result.Approved, string.Join("; ", result.Violations));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Evaluate_Approves_WhenExactlySevenScoreComponentCardsPresent()
    {
        var result = FixedHolisticStructureGate.Evaluate(SevenRealComponents);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>
    /// [CHANGED 2026-09-10, tenant rule] A muted "not scored this run" component card is no
    /// longer a legitimate fill - a component with no score is omitted entirely. Any
    /// di-comp--muted is now itself a refusal.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_WhenAnyMutedComponentCardPresent()
    {
        var fourReal = string.Join("\n", Enumerable.Range(0, 4)
            .Select(_ => "<div class=\"di-comp\"><div class=\"di-comp__name\">X</div></div>"));
        var oneMuted = "<div class=\"di-comp di-comp--muted\"><div class=\"di-comp__name\">Evidence<small>Not scored this run</small></div></div>";
        var html = $"<div class=\"di-components\">{fourReal}\n{oneMuted}</div>";

        var result = FixedHolisticStructureGate.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("di-comp--muted", StringComparison.Ordinal));
    }

    /// <summary>
    /// [CHANGED 2026-09-10, tenant rule] Fewer than 7 REAL cards is now a legitimate silent-omit
    /// (a component with no score this run is dropped, not muted) - approved, as long as at least
    /// one real component backs the composite.
    /// </summary>
    [Fact]
    public void Evaluate_Approves_WhenFewerThanSevenRealComponentCardsPresent()
    {
        var fiveCards = string.Join("\n", Enumerable.Range(0, 5)
            .Select(_ => "<div class=\"di-comp\"><div class=\"di-comp__name\">X</div></div>"));
        var html = $"<div class=\"di-components\">{fiveCards}</div>";

        var result = FixedHolisticStructureGate.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>
    /// A score hero with zero component cards is still malformed - the composite is built from at
    /// least one component, so at least one must render.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_WhenHeroPresentButZeroComponentCards()
    {
        var result = FixedHolisticStructureGate.Evaluate("<div class=\"di-components\"><div class=\"di-components__head\"></div></div>");

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("0 .di-comp", StringComparison.Ordinal));
    }

    /// <summary>
    /// [ADDED 2026-09-10, tenant rule] No ghost/placeholder states may ship - a muted snapshot
    /// tile, a di-blocked-note section, or the literal "Not available yet" copy each refuse the
    /// whole document.
    /// </summary>
    [Theory]
    [InlineData("<article class=\"di-snaptile di-snaptile--muted\"><div class=\"di-snaptile__num\">Not available yet</div></article>")]
    [InlineData("<p class=\"di-blocked-note\">This section has no real data source yet in this run.</p>")]
    [InlineData("<div class=\"di-comp di-comp--muted\"><small>Not scored this run</small></div>")]
    public void Evaluate_Refuses_WhenAGhostOrPlaceholderStateIsPresent(string ghost)
    {
        var result = FixedHolisticStructureGate.Evaluate($"<section>{ghost}</section>");

        Assert.False(result.Approved);
    }

    [Fact]
    public void Evaluate_Approves_WhenBlockedTabBadgeIsZero()
    {
        var result = FixedHolisticStructureGate.Evaluate(CleanBlockedTab);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>
    /// [BUG FOUND LIVE] A real render badged Actions "6" while di-pane-6 itself said "This section
    /// has no real data source yet in this run" - a fabricated number next to its own admission
    /// nothing backs it. CLAUDE.md non-negotiable #2: never a fabricated number, fail closed.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_WhenBlockedTabBadgeIsNonZero()
    {
        var html = CleanBlockedTab.Replace(
            "<span class=\"di-tab__count tnum\">0</span>", "<span class=\"di-tab__count tnum\">6</span>");

        var result = FixedHolisticStructureGate.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("di-pane-6", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Approves_WhenNoBlockedPanesPresentAtAll()
    {
        // The dynamic (non-fixed-template) report shape has no data-blocked panes at all - this
        // check must be a no-op for it, never a false positive.
        var result = FixedHolisticStructureGate.Evaluate(
            "<label for=\"di-tab-2\" class=\"di-tab\">Risk<span class=\"di-tab__count tnum\">3</span></label>" +
            "<section class=\"di-pane\" id=\"di-pane-2\"></section>");

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    private const string ValidCoveragePane = """
        <section class="di-pane" id="di-pane-3" aria-label="Coverage">
          <div class="di-covwrap">
            <div class="di-covmap">
              <div class="di-covregion">
                <div class="di-covgrid">
                  <button type="button" class="di-covtile" data-st="healthy" data-branch-id="B1"></button>
                </div>
              </div>
            </div>
            <aside class="di-covdetail di-covdetail--side"></aside>
          </div>
          <script>var map = document.querySelector('.di-covmap');</script>
        </section>
        """;

    [Fact]
    public void Evaluate_Approves_WhenCoveragePaneHasRealGridAndScript()
    {
        var result = FixedHolisticStructureGate.Evaluate(ValidCoveragePane);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Evaluate_Approves_WhenNoCoveragePanePresentAtAll()
    {
        // Not a fixed-holistic-shaped document at all (or this specific fixture just does not
        // include it) - nothing for this check to verify, must never be a false positive.
        var result = FixedHolisticStructureGate.Evaluate("<html><body>No coverage pane here.</body></html>");

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] A real render substituted a plain di-kpi--span12 summary card
    /// for the entire interactive store grid - zero di-cov* classes anywhere, tab badge otherwise
    /// internally consistent so nothing else caught it. Same failure family as the score-card
    /// count invariant: a prose instruction, not always followed.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_WhenCoveragePaneHasNoGridAtAll()
    {
        const string plainSummaryInstead = """
            <section class="di-pane" id="di-pane-3" aria-label="Coverage">
              <div class="di-kpigrid">
                <article class="di-kpi di-kpi--span12"><div class="di-kpi__pairs"></div></article>
              </div>
            </section>
            """;

        var result = FixedHolisticStructureGate.Evaluate(plainSummaryInstead);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("di-pane-3", StringComparison.Ordinal) && v.Contains("di-covgrid", StringComparison.Ordinal));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The original coverage-tile bug: the grid rendered correctly
    /// but DOMPurify's default profile silently deleted the &lt;script&gt; that made it clickable
    /// (see DomPurifySanitizer's own [BUG FOUND LIVE] note - fixed there via
    /// ADD_TAGS: ['script']). This gate is the second, independent net against the same class of
    /// regression recurring.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_WhenCoveragePaneHasGridButNoScript()
    {
        var gridWithoutScript = ValidCoveragePane.Replace(
            "<script>var map = document.querySelector('.di-covmap');</script>", "");

        var result = FixedHolisticStructureGate.Evaluate(gridWithoutScript);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("di-pane-3", StringComparison.Ordinal) && v.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The gate above was written assuming the driving &lt;script&gt;
    /// sits INSIDE di-pane-3's own &lt;section&gt; - a real render placed it at the end of
    /// &lt;body&gt; instead (a completely normal, common placement), and the gate refused it as
    /// "no driving script" even though one was right there - a false refusal that exhausted every
    /// retry iteration and caused a raw, never-processed fallback (no font-face either) TWICE.
    /// The script only needs to exist somewhere in the document and reference the grid it drives -
    /// nesting inside the pane's own markup was never a real requirement.
    /// </summary>
    [Fact]
    public void Evaluate_Approves_WhenDrivingScriptSitsOutsideThePaneAtEndOfBody()
    {
        var gridWithScriptMovedToEndOfBody = ValidCoveragePane
            .Replace("<script>var map = document.querySelector('.di-covmap');</script>", "")
            .Replace("</section>", "</section>\n<script>var map = document.querySelector('.di-covmap');</script>\n</body>");

        var result = FixedHolisticStructureGate.Evaluate(gridWithScriptMovedToEndOfBody);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Evaluate_Approves_WhenCoveragePaneIsBlocked()
    {
        // Defensive only - Coverage (Location dimension) always has real data in practice and
        // should never legitimately be data-blocked, but this check must not fight
        // CheckBlockedTabBadges over the same pane if that ever changes.
        const string blockedCoverage = """
            <section class="di-pane" id="di-pane-3" aria-label="Coverage" data-blocked="true"></section>
            """;

        var result = FixedHolisticStructureGate.Evaluate(blockedCoverage);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }
}

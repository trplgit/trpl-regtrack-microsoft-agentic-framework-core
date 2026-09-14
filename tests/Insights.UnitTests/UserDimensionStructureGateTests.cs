using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-12] A real render for `dimension_selection:Users` ignored the rewritten
/// 4-tab/donut/role-strip template entirely and reverted to the old, superseded flat-section
/// shape (no tabs, no donut, no role strip - straight into a plain "all rows" table). No
/// structural gate existed for this template (only FixedHolisticComposition had one), so
/// nothing caught it before publish. Same posture as FixedHolisticStructureGateTests: pure
/// logic, no DB/LLM/browser, exercises exactly the violation class found live.
/// </summary>
public sealed class UserDimensionStructureGateTests
{
    private const string WellFormed = """
        <div class="di-tabsroot">
          <input type="radio" name="di-tab" id="di-tab-1" class="di-tab-input" checked>
          <input type="radio" name="di-tab" id="di-tab-2" class="di-tab-input">
          <input type="radio" name="di-tab" id="di-tab-3" class="di-tab-input">
          <input type="radio" name="di-tab" id="di-tab-4" class="di-tab-input">
          <div class="di-stickytabs">
            <nav class="di-tabnav">
              <label for="di-tab-1" class="di-tab">Overview</label>
              <label for="di-tab-2" class="di-tab">Priority load</label>
              <label for="di-tab-3" class="di-tab">Standouts</label>
              <label for="di-tab-4" class="di-tab">What this means</label>
            </nav>
          </div>
          <section class="di-pane" id="di-pane-1"></section>
          <section class="di-pane" id="di-pane-2"></section>
          <section class="di-pane" id="di-pane-3"></section>
          <section class="di-pane" id="di-pane-4"></section>
        </div>
        """;

    private const string RealDonut = """
        <div class="di-donut ur-donut" aria-hidden="true">
          <svg viewBox="0 0 120 120">
            <circle class="di-donut__track" cx="60" cy="60" r="52"></circle>
            <circle class="di-donut__arc" cx="60" cy="60" r="52"></circle>
          </svg>
        </div>
        """;

    private const string RealRoleStrip = """
        <div class="ur-rolesgrid">
          <div class="ur-role ur-role--perf">
            <div class="ur-role__name">Performer</div>
          </div>
        </div>
        """;

    // [ADDED 2026-09-13] Priority-load lens toggle (Overdue items / Tenant share) - previously
    // rendered as one static list with no toggle at all, see UserDimensionStructureGate's own
    // doc comment. Same CSS-only radio+:has() shape as the di-tab-N tabs above, so the same
    // "the model reverted to the old, simpler shape" failure mode applies here too.
    // [FIX 2026-09-15] Radios now live INSIDE .ur-lensroot, matching the real, corrected prompt
    // markup - a real render once placed them as siblings BEFORE .ur-lensroot opened, which
    // silently broke every :has() rule (:has() only sees descendants), so both lens buttons
    // rendered but neither actually switched anything.
    private const string RealLensToggle = """
        <div class="ur-lensroot">
          <input type="radio" name="ur-lens" id="ur-lens-1" class="ur-lens-radio" checked>
          <input type="radio" name="ur-lens" id="ur-lens-2" class="ur-lens-radio">
          <div class="ur-pulist ur-pulist-1"></div>
          <div class="ur-pulist ur-pulist-2"></div>
        </div>
        """;

    private static string WellFormedDocument() => WellFormed + RealDonut + RealRoleStrip + RealLensToggle;

    [Fact]
    public void Evaluate_Approves_WhenFourTabsDonutRoleStripAndLensToggleAllPresent()
    {
        var result = UserDimensionStructureGate.Evaluate(WellFormedDocument());

        Assert.True(result.Approved, string.Join("; ", result.Violations));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Evaluate_Refuses_WhenNoTabsPresentAtAll()
    {
        // The exact real failure: a flat "all rows" table, no di-tabsroot, no radios, no labels.
        var flatTableOnly = """
            <table class="di-table"><thead><tr><th>User</th></tr></thead><tbody><tr><td>A</td></tr></tbody></table>
            """ + RealDonut + RealRoleStrip + RealLensToggle;

        var result = UserDimensionStructureGate.Evaluate(flatTableOnly);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("tab", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_Refuses_WhenOnlyThreeOfFourTabsPresent()
    {
        var threeTabs = WellFormed.Replace(
            """<input type="radio" name="di-tab" id="di-tab-4" class="di-tab-input">""", "");

        var result = UserDimensionStructureGate.Evaluate(threeTabs + RealDonut + RealRoleStrip + RealLensToggle);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("di-tab-4", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_WhenNoDonutPresent()
    {
        var result = UserDimensionStructureGate.Evaluate(WellFormed + RealRoleStrip + RealLensToggle);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("donut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_Refuses_WhenNoRoleStripPresent()
    {
        var result = UserDimensionStructureGate.Evaluate(WellFormed + RealDonut + RealLensToggle);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_Refuses_WhenLensToggleMissing()
    {
        // The exact plausible failure this closes: the model renders one static ranking again,
        // dropping both ur-lens-N radios and the ur-pulist-2 (Tenant share) list entirely.
        var noLensToggle = WellFormed + RealDonut + RealRoleStrip;

        var result = UserDimensionStructureGate.Evaluate(noLensToggle);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("lens", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_Refuses_WhenOnlyOneLensListPresent()
    {
        // A partial toggle - the radios exist but the second ranked list (Tenant share) does
        // not, so switching lenses would show nothing.
        var oneLensOnly = WellFormed + RealDonut + RealRoleStrip + """
            <input type="radio" name="ur-lens" id="ur-lens-1" class="ur-lens-radio" checked>
            <input type="radio" name="ur-lens" id="ur-lens-2" class="ur-lens-radio">
            <div class="ur-pulist ur-pulist-1"></div>
            """;

        var result = UserDimensionStructureGate.Evaluate(oneLensOnly);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("ur-pulist-2", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_WhenLensRadiosAreSiblingsBeforeLensRootInsteadOfDescendants()
    {
        // The EXACT real failure: both ur-lens-N radios and both ur-pulist-N lists are present
        // (the two checks above both pass), but the radios sit BEFORE <div class="ur-lensroot">
        // opens instead of inside it - a real render did this and every :has() toggle rule
        // silently did nothing, because :has() only matches descendants, never preceding
        // siblings. Both lens buttons rendered but clicking either one changed nothing.
        var radiosOutsideLensRoot = WellFormed + RealDonut + RealRoleStrip + """
            <input type="radio" name="ur-lens" id="ur-lens-1" class="ur-lens-radio" checked>
            <input type="radio" name="ur-lens" id="ur-lens-2" class="ur-lens-radio">
            <div class="ur-lensroot">
              <div class="ur-pulist ur-pulist-1"></div>
              <div class="ur-pulist ur-pulist-2"></div>
            </div>
            """;

        var result = UserDimensionStructureGate.Evaluate(radiosOutsideLensRoot);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("ur-lensroot", StringComparison.Ordinal));
    }

    // [ADDED 2026-09-15] Completion-timing info line (ur-timingline, replaces the earlier
    // ur-timingbadge). Real failure, found live on all THREE real tenants run through the actual
    // orchestrator the same day: rows with real, non-null OnTimePct (100%, 99.2%, ... - genuine
    // completed-event history, so a real TimingSampleSize >= 5 almost certainly existed too) had
    // .ur-pu-mix close right after .ur-risklegend with no <p class="ur-timingline...."> at all -
    // not even a hidden one. The model dropped the whole sentence for every row in all three
    // documents, not a data gap ("they're performing well so nothing to show" was the user's own
    // hypothesis, disproven by the real on-time percentages sitting right there). Never enforced
    // unconditionally (a tenant where truly no user clears the >= 5 sample floor legitimately
    // shows zero lines) - only when the real per-user rows prove at least one qualifies.
    private const string RealTimingLine = """
        <p class="ur-timingline ur-timingline--late">Typically finishes <span class="tnum">10</span> days after the deadline, on average.</p>
        """;

    [Fact]
    public void Evaluate_Approves_WhenQualifyingTimingRowExistsAndTimingLinePresent()
    {
        var result = UserDimensionStructureGate.Evaluate(WellFormedDocument() + RealTimingLine, hasQualifyingTimingRow: true);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Evaluate_Refuses_WhenQualifyingTimingRowExistsButTimingLineMissing()
    {
        // The exact real failure: shell markup all correct, but the LLM dropped the info line
        // entirely across every row despite real, qualifying completed-event data being given.
        var result = UserDimensionStructureGate.Evaluate(WellFormedDocument(), hasQualifyingTimingRow: true);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("ur-timingline", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Approves_WhenNoQualifyingTimingRowAndTimingLineAbsent()
    {
        // The honest "nothing to show" case - no user in this tenant clears the real >= 5
        // sample-size floor, so zero lines is CORRECT, never a violation.
        var result = UserDimensionStructureGate.Evaluate(WellFormedDocument(), hasQualifyingTimingRow: false);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
        Assert.Empty(result.Violations);
    }
}

using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-12] Deterministic structural gate for `dimension_selection:Users`
/// (05_report_html_dimension_selection_user.md) - reproduces the real product's own 4-tab
/// (Overview/Priority load/Standouts/What this means) donut+role-strip hero, rewritten the
/// same day to replace a flatter, superseded single-section shape. Found live on the FIRST
/// real render: the model ignored the new template entirely and reverted to something
/// resembling the OLD flat shape - no tabs, no donut, no role strip, straight into a plain
/// "all rows" table. No gate existed for any dimension_selection template (only
/// FixedHolisticComposition had one, via FixedHolisticStructureGate) - this closes that gap
/// for the Users template specifically, same regex-based, auditable-not-a-full-parser posture.
///
/// [2026-09-13] A 5th tab ("Completion timing") was built, gated, and tested, then dropped the
/// same day in favour of folding the same data (MedianDaysEarlyLate) inline into each
/// Priority-load row instead - a per-row badge is data-conditional (a tenant where no user has
/// enough sample size legitimately shows zero badges), so it gets no structural "must be
/// present" check, unlike the tabs/donut/role-strip/lens toggle below which are always present
/// regardless of data. Back to 4 tabs.
///
/// Five structural invariants, all must hold:
/// 1. All four tab radios (`di-tab-1`..`di-tab-4`) present as `<input type="radio" ... id="di-tab-N">`.
/// 2. All four matching `<label for="di-tab-N">` present.
/// 3. A real donut (`.di-donut` wrapper containing a `.di-donut__arc`) present - the hero's
///    concentration ring, not the old shape's plain KPI-grid-only hero.
/// 4. A real role strip (`.ur-rolesgrid` containing at least one `.ur-role` card) present.
/// 5. [ADDED 2026-09-13] The Priority-load lens toggle - both `ur-lens-1`/`ur-lens-2` radios AND
///    both `.ur-pulist-1`/`.ur-pulist-2` ranked lists present. Same CSS-only radio+:has()
///    mechanism as the di-tab-N tabs, added the same day the toggle went from "one static list,
///    no switcher" to a real 2-lens toggle - closing the same class of gap check 1-2 already
///    close for the tab nav, before a render has the chance to silently drop it again.
/// 6. [ADDED 2026-09-15] Both lens radios sit INSIDE `.ur-lensroot`, not as siblings before it.
///    Found live: a real render placed the two radios before `<div class="ur-lensroot">`
///    opened - checks 5 above still passed (both radios and both lists were present SOMEWHERE
///    in the document), but the `:has()` rules driving the toggle silently did nothing, because
///    `:has()` only matches descendants, never preceding siblings. Both lens buttons rendered;
///    neither one switched anything. This is a position check (a regex position comparison, not
///    a real DOM check), same auditable-not-a-full-parser posture as every other check here.
/// 7. [ADDED 2026-09-15] The completion-timing info line (`.ur-timingline`) appears at least once
///    when the caller says a real qualifying row exists (`hasQualifyingTimingRow`). Found live on
///    ALL THREE real tenants run through the actual orchestrator the same day: rows with real,
///    non-null `OnTimePct` (100%, 99.2%, ...) - genuine completed-event history, so a real
///    `TimingSampleSize` >= 5 almost certainly existed - had `.ur-pu-mix` close right after
///    `.ur-risklegend` with no `<p class="ur-timingline...">` at all, not even a hidden one. Not
///    a data gap; the model dropped the whole sentence for every row in all three documents.
///    Unlike checks 1-6, this one is genuinely data-conditional (a tenant where truly no user
///    clears the real >= 5 sample floor legitimately shows zero lines), so the caller must supply
///    that fact from the real per-user rows - this gate never re-derives it from the HTML alone.
/// </summary>
public static partial class UserDimensionStructureGate
{
    private static readonly string[] RequiredTabIds = ["di-tab-1", "di-tab-2", "di-tab-3", "di-tab-4"];
    private static readonly string[] RequiredLensIds = ["ur-lens-1", "ur-lens-2"];
    private static readonly string[] RequiredLensLists = ["ur-pulist-1", "ur-pulist-2"];

    public static FixedHolisticStructureResult Evaluate(string html, bool hasQualifyingTimingRow = false)
    {
        var violations = new List<string>();

        CheckAllFourTabRadiosPresent(html, violations);
        CheckAllFourTabLabelsPresent(html, violations);
        CheckRealDonutPresent(html, violations);
        CheckRoleStripPresent(html, violations);
        CheckLensTogglePresent(html, violations);
        CheckLensRadiosInsideLensRoot(html, violations);
        CheckTimingLineWhenDataAvailable(html, hasQualifyingTimingRow, violations);

        return violations.Count == 0 ? FixedHolisticStructureResult.Approve : FixedHolisticStructureResult.Refuse(violations);
    }

    private static void CheckAllFourTabRadiosPresent(string html, List<string> violations)
    {
        foreach (var tabId in RequiredTabIds)
        {
            if (!TabRadioToken(tabId).IsMatch(html))
                violations.Add($"missing tab radio input#{tabId} - the real 4-tab (Overview/Priority load/Standouts/What this means) shape requires all four, this document has fewer");
        }
    }

    private static void CheckAllFourTabLabelsPresent(string html, List<string> violations)
    {
        foreach (var tabId in RequiredTabIds)
        {
            if (!TabLabelForToken(tabId).IsMatch(html))
                violations.Add($"missing <label for=\"{tabId}\"> - every tab radio needs its matching clickable label");
        }
    }

    private static void CheckRealDonutPresent(string html, List<string> violations)
    {
        if (!DiDonutWrapperToken().IsMatch(html) || !DiDonutArcToken().IsMatch(html))
            violations.Add("no real .di-donut/.di-donut__arc concentration ring in the hero - the old, superseded flat shape had a plain KPI-grid-only hero instead; this template requires the real donut");
    }

    private static void CheckRoleStripPresent(string html, List<string> violations)
    {
        if (!UrRolesGridToken().IsMatch(html) || !UrRoleCardToken().IsMatch(html))
            violations.Add("no .ur-rolesgrid / .ur-role role-strip card in the hero band - the real page always shows at least one real role card (Performer, Reviewer, or Other roles)");
    }

    private static void CheckLensTogglePresent(string html, List<string> violations)
    {
        foreach (var lensId in RequiredLensIds)
        {
            if (!LensRadioToken(lensId).IsMatch(html))
                violations.Add($"missing lens radio input#{lensId} - the Priority-load ranking toggle (Overdue items / Tenant share) requires both, this document has fewer");
        }

        foreach (var lensListClass in RequiredLensLists)
        {
            if (!LensListToken(lensListClass).IsMatch(html))
                violations.Add($"missing .{lensListClass} - each lens needs its own ranked row list, or switching lenses would show nothing");
        }
    }

    private static void CheckLensRadiosInsideLensRoot(string html, List<string> violations)
    {
        var lensRootMatch = LensRootOpenToken().Match(html);
        if (!lensRootMatch.Success)
            return; // no .ur-lensroot at all - already covered by CheckLensTogglePresent's own violations

        foreach (var lensId in RequiredLensIds)
        {
            var radioMatch = LensRadioToken(lensId).Match(html);
            if (radioMatch.Success && radioMatch.Index < lensRootMatch.Index)
                violations.Add($"lens radio input#{lensId} sits BEFORE .ur-lensroot opens (a sibling, not a descendant) - the :has() toggle rules can never see it, so switching lenses does nothing");
        }
    }

    private static void CheckTimingLineWhenDataAvailable(string html, bool hasQualifyingTimingRow, List<string> violations)
    {
        if (hasQualifyingTimingRow && !UrTimingLineToken().IsMatch(html))
            violations.Add("missing <p class=\"ur-timingline...\"> - a real per-user row has a qualifying (>= 5 sample) MedianDaysEarlyLate reading, so at least one completion-timing sentence must appear");
    }

    [GeneratedRegex(@"class=""[^""]*\bur-lensroot\b[^""]*""")]
    private static partial Regex LensRootOpenToken();

    [GeneratedRegex(@"<p\s+class=""[^""]*\bur-timingline\b[^""]*""")]
    private static partial Regex UrTimingLineToken();

    private static Regex TabRadioToken(string tabId) =>
        new($"<input\\s+type=\"radio\"[^>]*\\bid=\"{Regex.Escape(tabId)}\"", RegexOptions.IgnoreCase);

    private static Regex TabLabelForToken(string tabId) =>
        new($"<label\\s+for=\"{Regex.Escape(tabId)}\"", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"class=""[^""]*\bdi-donut\b[^""]*""")]
    private static partial Regex DiDonutWrapperToken();

    [GeneratedRegex(@"class=""[^""]*\bdi-donut__arc\b[^""]*""")]
    private static partial Regex DiDonutArcToken();

    [GeneratedRegex(@"class=""[^""]*\bur-rolesgrid\b[^""]*""")]
    private static partial Regex UrRolesGridToken();

    [GeneratedRegex(@"class=""[^""]*\bur-role\b[^""]*""")]
    private static partial Regex UrRoleCardToken();

    private static Regex LensRadioToken(string lensId) =>
        new($"<input\\s+type=\"radio\"[^>]*\\bid=\"{Regex.Escape(lensId)}\"", RegexOptions.IgnoreCase);

    private static Regex LensListToken(string lensListClass) =>
        new($@"class=""[^""]*\b{Regex.Escape(lensListClass)}\b[^""]*""", RegexOptions.IgnoreCase);
}

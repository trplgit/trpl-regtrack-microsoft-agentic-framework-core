namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-10-09] Kill switches and budgets for the interactive tile-QA stage and its patch
/// loop, bound from configuration section <c>Presentation:TileQa</c> and read on EVERY activity
/// execution (IOptionsMonitor), so prod can turn either half off without a redeploy.
///
/// Code defaults are BOTH OFF: a host with no key configured never opens a browser page for this
/// stage and never schedules a patch call. The orchestrator cannot read configuration
/// (CLAUDE.md Sec. 6), so these are consumed inside InteractiveTileQaActivity / PatchRenderActivity
/// and surfaced to the orchestrator through nullable, trailing output fields whose null value
/// reproduces the pre-2026-10-09 (orchestrator 4.8) behaviour exactly - no version bump.
///
/// Why this exists: on 2026-10-09 every one of six real tenant-1271 runs spent all three patch
/// attempts without converging (Act: 75 minutes inside InteractiveTileQaActivity, one call
/// 57 minutes), and the whole-page LLM patch call corrupted the page it was asked to fix.
/// </summary>
public sealed class TileQaOptions
{
    public const string SectionName = "Presentation:TileQa";

    /// <summary>Run the tile-by-tile check at all. Off: the activity returns no findings without opening a page.</summary>
    public bool Enabled { get; set; }

    /// <summary>Allow the patch loop. Off: findings are reported (logs + run output) and the page ships as rendered.</summary>
    public bool PatchEnabled { get; set; }

    /// <summary>Overall wall-clock budget for one checker pass. Partial findings are returned with Truncated = true.</summary>
    public int BudgetSeconds { get; set; } = 180;

    /// <summary>Budget for one element's hover/click, settle, screenshots and (if any) vision review.</summary>
    public int PerElementSeconds { get; set; } = 20;

    /// <summary>Cap on vision (glitch-review) calls per checker pass. Beyond it, cross-tile changes are not reviewed.</summary>
    public int MaxGlitchReviews { get; set; } = 4;

    /// <summary>Cap on cards examined per pass, in document order.</summary>
    public int MaxCardsPerPass { get; set; } = 40;

    /// <summary>Wall-clock cap on one patch LLM call. Past it the attempt is reported as failed, never thrown.</summary>
    public int PatchCallSeconds { get; set; } = 150;

    /// <summary>
    /// Lab only. When null (prod), nothing is written to disk. When set, before/after screenshots
    /// are written for FINDINGS only, into a per-call subfolder, and files older than 24 h are
    /// deleted when a pass starts.
    /// </summary>
    public string? ScreenshotDirectory { get; set; }
}

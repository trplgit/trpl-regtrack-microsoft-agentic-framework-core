namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-10-09] What the interactive tile-QA stage concluded for one report, carried on
/// PersistInput (so PersistActivity can log one structured warning with the ReportId) and on
/// PersistOutput (so it lands in the orchestration's stored Output in the task hub, queryable
/// without any schema change). Trailing-optional everywhere it appears; null means "stage did
/// not report" (runs recorded before this existed).
///
/// Outcome values: "disabled" (checker off), "clean" (no findings), "patched" (findings found and
/// all cleared), "shipped_with_open_findings" (findings remain on the shipped page),
/// "patching_disabled" (findings found, patch loop off by configuration).
/// </summary>
public sealed record TileQaSummary(
    string Outcome,
    int OpenFunctional,
    int OpenCosmetic,
    int PatchAttempts,
    int PatchesApplied,
    bool CheckerTruncated);

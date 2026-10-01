namespace Insights.Domain;

/// <summary>
/// The "Holistic Insights" report (the real, designer-approved product UI at
/// detailed-insights.component.html - confirmed against the live demo, not the illustrative
/// sample data) is a FIXED TEMPLATE: always a score hero, always these six tabs in this order,
/// never a per-tenant-shaped structure. This is a genuinely different report shape from the
/// dynamic `compliance_health` composition path (01_composition.md) - deliberately NOT built by
/// modifying that path, so nothing already working (e.g. the "coverage_map"/"risk_mix" hero
/// reports proven live today) regresses.
///
/// Zero LLM involvement in structure - more strictly than CLAUDE.md's non-negotiable #1 already
/// requires for the dynamic path, since here there is no judgement call to make at all. This also
/// sidesteps the exact reliability problem found live today: a composition LLM asked to always
/// decide on a `composite_score` block sometimes still skipped it. A fixed template cannot skip
/// anything, by construction.
/// </summary>
public static class FixedHolisticComposition
{
    /// <summary>
    /// The InsightsReportOrchestrationInput.ReportType value that selects this path - a caller
    /// (RegTrack API) requests it explicitly, same as "compliance_health" selects the existing
    /// dynamic path. Named after this class, not the product name ("Holistic Insights"), so the
    /// report-type string and the C# type that owns its shape can never drift apart by accident.
    /// </summary>
    public const string ReportType = "fixed_holistic";

    /// <summary>
    /// [CORRECTED 2026-09-23, was wrong since 2026-09-22] Used to scope tenant-memory read/write
    /// for this ReportType (docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md).
    ///
    /// The original list here (["Risk","Licence","Location","Users"]) was a GUESS about which
    /// tabs have real data backing, made without checking FetchDimensionsActivity's actual source.
    /// It was wrong on both ends: FetchDimensionsActivity attempts ALL FIFTEEN real dimensions for
    /// this ReportType (RequestedDimensions null -> every one, see that activity's own doc
    /// comment) - not a curated subset - and any of them can end up backing render content, not
    /// just the four guessed here. Confirmed live 2026-09-23: tenant 1285's real Location fetch
    /// failed (a genuine SQL bug, see the location-caveat-truncation-bug memory) and silently
    /// degraded into FetchDimensionsOutput.FailedDimensions rather than DimensionResults - the
    /// Coverage tab then had no real Location data to render from and the structure gate correctly
    /// refused the resulting placeholder-ish render. This list now matches that real fetch set
    /// exactly, in the same order FetchDimensionsActivity calls them.
    /// </summary>
    public static readonly IReadOnlyList<string> Dimensions =
    [
        "Location", "Entity", "Risk", "Nature", "Departments", "Act", "Users", "Internal",
        "Event", "Licence", "BacklogAging", "TimelinessFY", "ForwardPipeline", "EvidenceIntegrity", "ForwardRisk",
    ];

    public static CompositionPlan Build()
    {
        var hero = new CompositionHero("snapshot", "Fixed template - Snapshot is always the landing view, matching the real product UI.");

        var blocks = new List<CompositionBlockPlan>
        {
            new("snapshot", "high", []),
            new("risk_licences", "high", []),
            new("coverage", "medium", []),
            new("operations", "medium", []),
            new("forward_look", "low", []),
            new("actions", "medium", []),
        };

        // Nothing is ever omitted in a fixed template - every tab always renders, even if some
        // of its tiles have no real data source yet (declared as "not available yet" at render
        // time, never as an omitted block).
        return new CompositionPlan(hero, blocks, [], []);
    }
}

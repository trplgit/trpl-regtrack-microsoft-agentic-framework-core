namespace Insights.Domain;

/// <summary>One weighted component of the composite score, matching paid_tier_holistic.schema.json's overall_health.components[] shape.</summary>
public sealed record ScoreComponentResult(string DomainKpi, string Label, decimal? Score, decimal Weight);

/// <summary>Matches paid_tier_holistic.schema.json's overall_health object exactly.</summary>
public sealed record OverallHealth(decimal? Score, string Band, string Trend, string Method, IReadOnlyList<ScoreComponentResult> Components);

/// <summary>
/// PROVISIONAL FORMULA - CLAUDE.md non-negotiable #1: this is the one place "what is this number"
/// gets decided, deterministically. The composition/narrative agents may only CITE these results
/// (as Assertions, wired in by the caller) - never compute or restate a score themselves.
///
/// Every transform below is a single clear input, monotonic, and intentionally simple. Built to
/// give a real working number NOW; the business (Vinay/Sambram) is expected to review and retune
/// both the formulas and the weights - nothing here should be read as final.
///
/// A component whose input isn't available yet (Evidence - no real data source exists at all, see
/// DIMENSION_SPECS.md's evidence-integrity gap) returns a null Score, not a fabricated zero.
/// Timeliness was in this category too until sql/05_dimension_location.sql grew a real tenant-wide
/// on-time-closure query - ComputeScoreActivity now passes it through. The composite renormalizes over whichever
/// components ARE available, generalizing the "exclude Evidence, redistribute its weight" idea to
/// however many components are actually missing right now - never silently treats a missing input
/// as a zero score, which would be a worse number than not answering at all.
/// </summary>
public static class CompositeScoreCalculator
{
    private const decimal RiskWeight = 0.20m;
    private const decimal LicenceWeight = 0.15m;
    private const decimal CoverageWeight = 0.15m;
    private const decimal PeopleWeight = 0.15m;
    private const decimal TimelinessWeight = 0.15m;
    private const decimal OverdueBacklogWeight = 0.10m;
    private const decimal EvidenceWeight = 0.10m;

    /// <summary>
    /// Each dimension parameter is nullable: a dimension that FetchDimensionsActivity had to
    /// degrade (design doc Sec.11.4, partial generation) means the components depending on it
    /// score null - same treatment as a missing sub-metric (Timeliness/Evidence below), not a
    /// reason to fail the whole composite. Only when EVERY component ends up null does this
    /// refuse to publish (see ComputeBand).
    /// </summary>
    public static OverallHealth Compute(
        DimensionResult<RiskControlTotals, RiskRow>? risk,
        DimensionResult<LocationControlTotals, LocationRow>? location,
        IReadOnlyList<UsersRow>? usersRows,
        LicenceControlTotals? licenceTotals,
        decimal? tenantOnTimePct)
    {
        var components = new List<ScoreComponentResult>
        {
            new("risk_weighted", "Risk-weighted exposure", risk is null ? null : ComputeRiskScore(risk.ControlTotals, risk.Rows), RiskWeight),
            new("licence", "Licence", licenceTotals is null ? null : Clamp(100m - licenceTotals.TenantLapsedPct), LicenceWeight),
            new("coverage", "Coverage", location is null ? null : ComputeCoverageScore(location.ControlTotals, location.Rows), CoverageWeight),
            new("overdue_backlog", "Overdue / Backlog health", location is null ? null : ComputeOverdueBacklogScore(location.Rows, location.ControlTotals), OverdueBacklogWeight),
            new("people_continuity", "People / continuity", usersRows is null ? null : ComputePeopleScore(usersRows), PeopleWeight),
            new("timeliness", "Timeliness", Clamp(tenantOnTimePct), TimelinessWeight),
            // [BLOCKED] no real source exists - DIMENSION_SPECS.md's evidence-integrity gap
            // (FileID/DocumentNo are 100% empty on real data). Mirrors
            // paid_tier_holistic.schema.json's own evidence_in_sql escape hatch: this is a known,
            // declared absence, not a defect to hide.
            new("evidence_integrity", "Evidence integrity", null, EvidenceWeight),
        };

        var composite = ComputeComposite(components);

        return new OverallHealth(
            composite,
            ComputeBand(composite),
            // No prior run to diff against yet (no snapshot/delta history - Phase 2, not built).
            // "flat" is a declared placeholder, not a claim of stability - see the data-quality
            // note the caller should attach alongside this.
            "flat",
            "PROVISIONAL - not yet reviewed with the business. Weighted composite of up to seven " +
            "KPI scores (0-100). Weights: risk 0.20, licence 0.15, coverage 0.15, people 0.15, " +
            "timeliness 0.15, overdue/backlog 0.10, evidence 0.10. A component with no real data " +
            "source yet is scored null rather than zero, and the remaining weights are " +
            "renormalized to sum to 1.0 across whatever IS available.",
            components);
    }

    /// <summary>
    /// 100 minus the CRITICAL risk tier's own overdue rate - the tier the dictionary resolves as
    /// Critical (RiskControlTotals.CriticalRiskType), never a name/label match, since risk labels
    /// are SQL-computed narrative text, not a raw column CLAUDE.md's duplicate-name trap applies to.
    /// Null (not zero) if the critical tier has no rankable rate - e.g. zero obligations in scope.
    /// </summary>
    private static decimal? ComputeRiskScore(RiskControlTotals totals, IReadOnlyList<RiskRow> rows)
    {
        var criticalRow = rows.FirstOrDefault(r => r.RiskType == totals.CriticalRiskType);
        return criticalRow?.OverduePct is { } pct ? Clamp(100m - pct) : null;
    }

    /// <summary>
    /// Ghost branches (no_obligations_configured) get zero credit; high-ownerless branches get
    /// half credit; everything else counts as healthy. [PROVISIONAL SIMPLIFICATION] The
    /// "under-configured vs. peer norm" bucket the frontend's coverage grid shows
    /// (obligations_mapped vs peer_norm ratio) is NOT the same signal sql/05's Flags carry today -
    /// sql/05 only flags overdue-RATE peer gaps (peer_coverage_gap), not obligation-COUNT peer
    /// gaps. Distinguishing "under-configured" from "healthy" would need a new sql/05 detector
    /// (median Instances per StateID peer group, not median OverduePct) - out of scope for this
    /// pass. Flagged here rather than silently invented from data that does not exist yet.
    /// </summary>
    private static decimal? ComputeCoverageScore(LocationControlTotals totals, IReadOnlyList<LocationRow> rows)
    {
        if (totals.BranchesReported == 0)
            return null;

        var ghost = rows.Count(r => (r.Flags ?? "").Contains("no_obligations_configured"));
        var highOwnerless = rows.Count(r => (r.Flags ?? "").Contains("high_ownerless") && !(r.Flags ?? "").Contains("no_obligations_configured"));
        var healthy = totals.BranchesReported - ghost - highOwnerless;

        return Clamp(100m * (healthy + 0.5m * highOwnerless) / totals.BranchesReported);
    }

    /// <summary>
    /// 100 minus the tenant-wide overdue rate. [DEVIATION FROM THE ORIGINAL PLAN, FOUND WHILE
    /// IMPLEMENTING] The plan's `closure_rate_pct * 2` formula assumed sql/05's ClosureRatio field
    /// (lifetime closure EVENTS per CURRENT instance) was a bounded 0-100% figure it could scale.
    /// It is not - CLAUDE.md's own reference tenant shows a median ratio of 7.90 (790% on that
    /// scale), because closure events accumulate over years against a point-in-time instance
    /// count. That is a genuinely different metric from paid_tier_holistic.schema.json's
    /// closure_rate_pct (occurrences ever closed / occurrences ever scheduled), which no existing
    /// dimension proc currently exposes as a clean tenant-wide percentage. Using TenantOverduePct
    /// instead - already real, already bounded 0-100, already on every dimension's control totals -
    /// rather than force-fitting a mismatched metric to hit a formula that looked right on paper.
    /// </summary>
    private static decimal? ComputeOverdueBacklogScore(IReadOnlyList<LocationRow> rows, LocationControlTotals totals) =>
        rows.Count == 0 ? null : Clamp(100m - totals.TenantOverduePct);

    /// <summary>
    /// 100 minus a 50/50 blend of top-3 performer concentration and top reviewer concentration -
    /// the two continuity risks DIMENSION_SPECS.md's people-dimension narrative calls out by name
    /// ("three performer accounts carry ~60%... one reviewer approves ~82%"). Degrades to
    /// whichever half is available if one side has no data, rather than failing the whole score.
    /// </summary>
    private static decimal? ComputePeopleScore(IReadOnlyList<UsersRow> rows)
    {
        var totalPerformer = rows.Sum(r => r.PerformerInstances);
        var totalReviewer = rows.Sum(r => r.ReviewerInstances);

        decimal? top3SharePct = totalPerformer == 0 ? null :
            100m * rows.OrderByDescending(r => r.PerformerInstances).Take(3).Sum(r => r.PerformerInstances) / totalPerformer;

        decimal? reviewerConcentrationPct = totalReviewer == 0 ? null :
            100m * (rows.Count == 0 ? 0 : rows.Max(r => r.ReviewerInstances)) / totalReviewer;

        return (top3SharePct, reviewerConcentrationPct) switch
        {
            (null, null) => null,
            ({ } t3, null) => Clamp(100m - t3),
            (null, { } rc) => Clamp(100m - rc),
            ({ } t3, { } rc) => Clamp(100m - (0.5m * t3 + 0.5m * rc)),
        };
    }

    /// <summary>Weighted average over whichever components have a non-null score; the rest is redistributed automatically by construction (dividing by the sum of only the weights actually used).</summary>
    private static decimal? ComputeComposite(IReadOnlyList<ScoreComponentResult> components)
    {
        var available = components.Where(c => c.Score is not null).ToList();
        if (available.Count == 0)
            return null;

        var weightSum = available.Sum(c => c.Weight);
        var weightedSum = available.Sum(c => c.Score!.Value * c.Weight);
        return Math.Round(weightedSum / weightSum, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Matches paid_tier_holistic.schema.json's band enum exactly. A null composite (nothing scoreable at all) is a fail-closed condition the caller must handle before this runs - never silently mapped to a band here.</summary>
    private static string ComputeBand(decimal? composite) => composite switch
    {
        null => throw new InvalidOperationException("No component produced a score - cannot assign a health band. Refusing to publish rather than infer a verdict from nothing."),
        >= 80 => "Strong",
        >= 65 => "Stable",
        >= 45 => "Needs Attention",
        >= 25 => "At Risk",
        _ => "Critical",
    };

    /// <summary>
    /// [BUG FOUND LIVE] decimal division does not auto-round - an unrounded ratio produced
    /// "people_continuity is 56.387042686695154582317886609" verbatim in real rendered prose
    /// (the narrator correctly copies assertion values exactly, so the malformed value was the
    /// bug, not the narration). Every score is a percentage; one decimal place is what every
    /// other percentage field in this codebase already uses (see e.g. LocationRow.OverduePct).
    /// </summary>
    private static decimal? Clamp(decimal? value) => value is { } v ? Math.Round(Math.Clamp(v, 0m, 100m), 1, MidpointRounding.AwayFromZero) : null;
}

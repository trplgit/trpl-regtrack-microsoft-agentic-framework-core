namespace Insights.Domain;

/// <summary>The 4 real Coverage-pane statuses - see docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4, and the real Angular reference (detailed-insights.data.ts's CovStatus type). Not an invented taxonomy.</summary>
public static class CoverageStatus
{
    public const string Healthy = "healthy";
    public const string UnderConfigured = "under_configured";
    public const string HasOwnerless = "has_ownerless";
    public const string Unmapped = "unmapped";
}

public sealed record CoverageStatusCounts(int Total, int Healthy, int UnderConfigured, int HasOwnerless, int Unmapped);

/// <summary>
/// [BUG FOUND LIVE, 2026-09-02] Classifies a leaf LocationRow into the real 4-state Coverage
/// taxonomy by reading sql/05_dimension_location.sql's own already-computed Flags field, not by
/// re-deriving thresholds independently - an earlier draft used "Ownerless &gt; 0" for
/// has_ownerless, which is looser than the real detector's own 10% OwnerlessPct threshold
/// (`high_ownerless`). Reading Flags directly means this can never drift from the real logic.
///
/// `under_configured` is never assigned - it needs a real per-branch obligation-COUNT peer norm
/// (how many obligations comparable branches carry) that no procedure computes yet. The only real
/// peer field on LocationRow, VsPeerStateNormPP (`peer_coverage_gap` flag), is an OVERDUE-RATE
/// peer gap - a different concept - using it here would be exactly the taxonomy reinterpretation
/// CLAUDE.md non-negotiable #2 exists to prevent. The chip/legend/CSS for this status still exist
/// (the taxonomy is real and complete); no real branch qualifies for it today.
/// </summary>
public static class LocationCoverageClassifier
{
    /// <summary>no_obligations_configured takes precedence over high_ownerless - most severe wins, matches PAID_TIER_SAMPLE_REFERENCE.md's own "status_precedence... first match wins" rule.</summary>
    public static string Classify(LocationRow row)
    {
        var flags = row.Flags ?? "";
        if (flags.Contains("no_obligations_configured", StringComparison.Ordinal))
            return CoverageStatus.Unmapped;
        if (flags.Contains("high_ownerless", StringComparison.Ordinal))
            return CoverageStatus.HasOwnerless;
        return CoverageStatus.Healthy;
    }

    public static CoverageStatusCounts ComputeCounts(IReadOnlyList<LocationRow> rows)
    {
        int healthy = 0, hasOwnerless = 0, unmapped = 0;
        foreach (var row in rows)
        {
            switch (Classify(row))
            {
                case CoverageStatus.Unmapped: unmapped++; break;
                case CoverageStatus.HasOwnerless: hasOwnerless++; break;
                default: healthy++; break;
            }
        }
        return new CoverageStatusCounts(rows.Count, healthy, UnderConfigured: 0, hasOwnerless, unmapped);
    }
}

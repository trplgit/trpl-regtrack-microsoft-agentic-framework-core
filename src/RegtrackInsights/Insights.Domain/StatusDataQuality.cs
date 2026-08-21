namespace Insights.Domain;

/// <summary>
/// How usp_Insights_StatusDataQuality judged this tenant's unknown-status volume.
///
/// The rule is deliberately PROPORTIONATE rather than absolute. Production contains
/// schedules whose ComplianceStatusID is NULL - ten system-wide at the time of writing, nine of
/// them past due and active. The canonical overdue function INNER JOINs the dictionary, so those
/// rows produce no row and are excluded from overdue: safe, because it cannot over-count, but
/// SILENT, which violates "fail closed AND loudly". Refusing an entire report over one unknown
/// row in 650,000 would be over-strict and would train people to bypass the gate.
/// </summary>
public enum StatusDataQualityVerdict
{
    /// <summary>No unknown statuses at all.</summary>
    Clean,

    /// <summary>Below threshold - surface it as a data_quality entry, do not block the run.</summary>
    DeclareInDataQuality,

    /// <summary>Above threshold - the proc THROWs rather than returning this verdict quietly.</summary>
    Raise,
}

/// <summary>
/// Result of usp_Insights_StatusDataQuality (sql/01) for one tenant.
///
/// ALWAYS surface <see cref="DataQualityNote"/> on the report when it is non-null. The whole
/// point of this probe is that excluded rows are declared rather than silently dropped - a
/// caller that reads the verdict and discards the note reintroduces exactly the silence it
/// exists to remove.
/// </summary>
public sealed record StatusDataQualityProbe(
    int CustomerId,
    int PastDueSchedules,
    int NullStatusRows,
    int UnmappedStatusRows,
    int TotalUnknown,
    decimal UnknownPct,
    StatusDataQualityVerdict Verdict,
    string? DataQualityNote);

/// <summary>
/// Thrown when a ComplianceStatus exists in the system but is absent from the dictionary -
/// SQL error 51001 from usp_Insights_AssertStatusCoverage, or 51003 from
/// usp_Insights_StatusDataQuality.
///
/// This ALWAYS blocks, at any volume: it means the dictionary is out of date with the system,
/// so every status-derived number is computed against an incomplete map. Add the missing values
/// to InsightsStatusClassification before computing anything.
/// </summary>
public sealed class DictionaryCoverageException(string detail, StatusDataQualityProbe? probe, Exception inner)
    : Exception($"Insights dictionary gap - {detail} Refusing to compute.", inner)
{
    /// <summary>The probe row, when the gap came from usp_Insights_StatusDataQuality. Null otherwise.</summary>
    public StatusDataQualityProbe? Probe { get; } = probe;
}

/// <summary>
/// Thrown when the volume of NULL-status past-due schedules exceeds the proportionate threshold -
/// SQL error 51004.
///
/// Unlike <see cref="DictionaryCoverageException"/> this is a SOURCE DATA problem, not a
/// dictionary one: the statuses are not misclassified, they are missing. <see cref="Probe"/>
/// carries the counts the proc selected before it threw, so the failure is diagnosable without
/// a second round trip.
/// </summary>
public sealed class StatusDataQualityThresholdException(StatusDataQualityProbe probe, Exception inner)
    : Exception(
        $"Tenant {probe.CustomerId}: {probe.NullStatusRows} of {probe.PastDueSchedules} past-due schedule(s) " +
        $"({probe.UnknownPct}%) have a NULL status - too many to report reliably. Refusing to compute.", inner)
{
    public StatusDataQualityProbe Probe { get; } = probe;
}

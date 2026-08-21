using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps the classification dictionary helpers (sql/01) - the single versioned source of truth
/// for all status and enum semantics. Every other proc reads from it; nothing outside it may
/// carry a status or enum literal.
///
/// Both methods are PRE-FLIGHT checks. Run them once before a batch, not once per tenant report:
/// a dictionary gap is a property of the deployment, so discovering it 600 times during a weekly
/// digest run is 599 wasted failures.
/// </summary>
public interface IDictionaryRepository
{
    /// <summary>
    /// Asserts that every ComplianceStatus in the system is mapped in the dictionary.
    ///
    /// This is the same check every dimension proc runs internally, exposed so it can be run
    /// ONCE up front - in CI on any dictionary change, and as a batch pre-flight. Returns true
    /// on success; throws <see cref="DictionaryCoverageException"/> (SQL 51001) if any status is
    /// unmapped. There is no partial-success outcome by design.
    /// </summary>
    Task<bool> AssertStatusCoverageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes one tenant for past-due schedules whose status is NULL or unmapped.
    ///
    /// Returns a probe whose <see cref="StatusDataQualityProbe.DataQualityNote"/> MUST be
    /// surfaced on the report when non-null - excluded rows are declared, never dropped quietly.
    ///
    /// Throws <see cref="DictionaryCoverageException"/> (SQL 51003) if any past-due schedule
    /// references a status absent from the dictionary - that always blocks, at any volume.
    /// Throws <see cref="StatusDataQualityThresholdException"/> (SQL 51004) if NULL-status volume
    /// exceeds the thresholds. Both carry the probe counts, so the failure is diagnosable
    /// without a second call.
    ///
    /// The defaults match the proc's own: raise above 100 unknown rows, or above 0.10% of
    /// past-due. Override only with a reason.
    /// </summary>
    Task<StatusDataQualityProbe> GetStatusDataQualityAsync(
        int customerId,
        int maxAbsoluteGap = 100,
        decimal maxProportionPct = 0.10m,
        CancellationToken cancellationToken = default);
}

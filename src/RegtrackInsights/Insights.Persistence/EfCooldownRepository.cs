using Insights.Data;
using Insights.Domain;
using Microsoft.EntityFrameworkCore;

namespace Insights.Persistence;

/// <inheritdoc cref="ICooldownRepository"/>
/// <param name="cooldownEnabled">
/// [ADDED 2026-09-25] Reports:CooldownEnabled - the testing/demo toggle. False makes every check
/// unconditionally open with no DB read, so a tester can regenerate the same dimension repeatedly;
/// flip back to true (the real appsettings default) for a demo or production, no code change or
/// redeploy needed if sourced from a config layer that supports runtime override.
/// </param>
public sealed class EfCooldownRepository(InsightsReportsDbContext db, int cooldownDays, bool cooldownEnabled = true) : ICooldownRepository
{
    private const string CompleteStatus = "complete";

    public async Task<CooldownResult> CheckAsync(
        int customerId, string reportType, string scopeDescriptor, string? dimension,
        CancellationToken cancellationToken = default)
    {
        if (!cooldownEnabled)
            return new CooldownResult(true, null);

        // [REDESIGNED 2026-09-25] Matches on the real dimension identity, not the free-text period
        // string. dimension is still only recoverable from GeneratedReport.Period's interim
        // "::dim=<name>" suffix (see ReportDimensionKey's own doc comment - the real dedicated
        // column is still deferred) - normalise and match by suffix, the same normalisation
        // ReportDimensionKey.ForCooldownAndRunId already applies when writing it. A null dimension
        // (every report type except dimension_selection) matches on scope+reportType alone - there
        // is only ever one unit for those types, so no further narrowing is needed or possible.
        var query = db.GeneratedReports
            .Where(r => r.CustomerId == customerId
                     && r.ReportType == reportType
                     && r.ScopeDescriptor == scopeDescriptor
                     && r.Status == CompleteStatus);

        if (dimension is { Length: > 0 })
        {
            var suffix = "::dim=" + dimension.Trim().ToLowerInvariant();
            query = query.Where(r => r.Period.EndsWith(suffix));
        }

        var lastSuccess = await query
            .OrderByDescending(r => r.GeneratedAtUtc)
            .Select(r => (DateTime?)r.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastSuccess is not { } generatedAtUtc)
            return new CooldownResult(true, null);

        var nextAvailableUtc = generatedAtUtc.AddDays(cooldownDays);
        var now = DateTime.UtcNow;
        if (nextAvailableUtc <= now)
            return new CooldownResult(true, null);

        var daysRemaining = (int)Math.Ceiling((nextAvailableUtc - now).TotalDays);
        return new CooldownResult(false, nextAvailableUtc, daysRemaining);
    }
}

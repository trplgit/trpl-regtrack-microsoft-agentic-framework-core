using Insights.Data;
using Insights.Domain;
using Microsoft.EntityFrameworkCore;

namespace Insights.Persistence;

/// <inheritdoc cref="ICooldownRepository"/>
public sealed class EfCooldownRepository(InsightsReportsDbContext db, int cooldownDays) : ICooldownRepository
{
    private const string CompleteStatus = "complete";

    public async Task<CooldownResult> CheckAsync(
        int customerId, string reportType, string scopeDescriptor, string period,
        CancellationToken cancellationToken = default)
    {
        var lastSuccess = await db.GeneratedReports
            .Where(r => r.CustomerId == customerId
                     && r.ReportType == reportType
                     && r.ScopeDescriptor == scopeDescriptor
                     && r.Period == period
                     && r.Status == CompleteStatus)
            .OrderByDescending(r => r.GeneratedAtUtc)
            .Select(r => (DateTime?)r.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastSuccess is not { } generatedAtUtc)
            return new CooldownResult(true, null);

        var nextAvailableUtc = generatedAtUtc.AddDays(cooldownDays);
        return nextAvailableUtc <= DateTime.UtcNow
            ? new CooldownResult(true, null)
            : new CooldownResult(false, nextAvailableUtc);
    }
}

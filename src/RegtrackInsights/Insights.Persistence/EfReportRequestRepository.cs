using Insights.Data;
using Insights.Domain;
using Microsoft.EntityFrameworkCore;

namespace Insights.Persistence;

/// <inheritdoc cref="IReportRequestRepository"/>
public sealed class EfReportRequestRepository(InsightsReportsDbContext db) : IReportRequestRepository
{
    public async Task SaveAsync(Guid reqId, IReadOnlyList<string> runIds, CancellationToken cancellationToken = default)
    {
        var createdAtUtc = DateTime.UtcNow;
        db.ReportRequestUnits.AddRange(runIds.Select(runId => new ReportRequestUnit
        {
            ReqId = reqId,
            RunId = runId,
            CreatedAtUtc = createdAtUtc,
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRunIdsAsync(Guid reqId, CancellationToken cancellationToken = default) =>
        await db.ReportRequestUnits
            .Where(u => u.ReqId == reqId)
            .Select(u => u.RunId)
            .ToListAsync(cancellationToken);
}

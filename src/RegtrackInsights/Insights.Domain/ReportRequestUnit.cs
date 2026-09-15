namespace Insights.Domain;

/// <summary>
/// One (reqId, runId) pair - sql/30_report_request.sql. A fan-out POST creates one row per
/// dimension it enqueued, all sharing the same ReqId, so IReportRequestRepository.GetRunIdsAsync
/// can find every runId a given reqId groups.
/// </summary>
public sealed class ReportRequestUnit
{
    public required Guid ReqId { get; init; }
    public required string RunId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

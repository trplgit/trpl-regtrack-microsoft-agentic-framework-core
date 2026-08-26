using Insights.Domain;

namespace Insights.Worker.Orchestration;

/// <summary>
/// The orchestration's public input shape - matches API_CONTRACTS.md 3's POST body plus UserId
/// (which the real API takes from the authenticated caller, not the request body; the CLI trigger
/// in InsightsRunOnceWorker supplies it directly since there is no auth context there).
///
/// Priority defaults to Interactive so every existing positional construction (tests, the CLI
/// trigger) keeps compiling unchanged and keeps its actual pre-existing meaning - only
/// DurableTaskRunEnqueuer passes something other than the default, and only when
/// PaidKeepWarmScheduler asked for Batch.
/// </summary>
public sealed record InsightsReportOrchestrationInput(
    int TenantId, string ReportType, InsightsScopeRequest Scope, string Period, int UserId,
    LlmCallPriority Priority = LlmCallPriority.Interactive);

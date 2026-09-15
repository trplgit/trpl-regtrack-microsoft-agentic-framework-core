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
/// <summary>
/// RequestedDimensions [ADDED 2026-09-08] - only meaningful when ReportType ==
/// DimensionSelectionComposition.ReportType ("dimension_selection"). Null/empty for every other
/// ReportType, which fetches all fourteen dimensions unconditionally, exactly as before this field
/// existed - trailing optional default, same reasoning as Priority above, so every existing
/// positional construction keeps compiling and keeps its pre-existing meaning.
/// </summary>
/// <summary>
/// ReqId [ADDED 2026-09-14] - the API-level fan-out request id (RunEndpoints.cs), one per POST
/// regardless of how many dimensions it fans out to. Used ONLY for LangFuse session grouping
/// (LangfuseSessionContext/LangfuseSessionTaggingChatClient) - never a scope/auth/identity
/// concern. Null for callers with no such concept (manual tests, the CLI trigger,
/// PaidKeepWarmScheduler); every activity falls back to the DTFx run id itself when this is null.
/// </summary>
/// <summary>
/// RunVisionQa [ADDED 2026-09-15] - defaults to true, real production behavior unchanged for
/// every existing caller. When false, the render-retry loop's real vision-model gate
/// (VisionQaActivity) is skipped entirely - NOTE: PlaywrightQaActivity is already advisory-only
/// (its result is discarded, never gates anything - see InsightsReportOrchestrator.cs's own
/// comment), so with this false, NOTHING checks the rendered HTML for a real visual defect before
/// it ships. This is a deliberate, explicit opt-out for fast local iteration (real vision defects
/// on large-row-count tenants were the main cause of the 3-retry/60+ minute runs found live
/// tonight) - not meant to stay on for anything that will actually be persisted/shown to a user.
/// Lives on the orchestration INPUT, not read from live config inside the orchestrator body,
/// because the orchestrator must stay deterministic across replay (CLAUDE.md Sec.6) - this way the
/// value is captured once at enqueue time and DTFx replay sees the exact same value every time.
/// </summary>
public sealed record InsightsReportOrchestrationInput(
    int TenantId, string ReportType, InsightsScopeRequest Scope, string Period, int UserId,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    IReadOnlyList<string>? RequestedDimensions = null,
    string? ReqId = null,
    bool RunVisionQa = true);

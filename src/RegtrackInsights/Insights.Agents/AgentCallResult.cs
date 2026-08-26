namespace Insights.Agents;

/// <summary>
/// Wraps every paid agent's parsed result together with the tokens THAT ONE CALL billed. Exists
/// for item 17's per-run token budget (design doc Sec.12.3): the orchestrator sums TotalTokens
/// after every LLM-calling activity and refuses the run if the running total exceeds
/// Budget:PerRunTokenCeiling, closing the "single report exceeding its expected envelope aborts to
/// the gate-refusal path rather than running away" requirement.
///
/// [TRAP] This is a SEPARATE channel from ILlmUsageRecorder/MeteredChatClient, deliberately not
/// unified with it. MeteredChatClient's recorder is a PROCESS-WIDE SINGLETON shared across every
/// concurrent run (LlmConcurrencyGate allows several tenants' calls in flight at once) - any shared
/// mutable "current run" state there would let one run's tokens leak into another's count. Returning
/// usage through the normal call chain instead (agent -> activity -> orchestrator) has no shared
/// state to race on, so it stays correct under concurrency by construction.
/// </summary>
public sealed record AgentCallResult<T>(T Value, long TotalTokens);

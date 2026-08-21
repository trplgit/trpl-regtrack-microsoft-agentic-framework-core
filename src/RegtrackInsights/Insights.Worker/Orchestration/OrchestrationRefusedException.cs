namespace Insights.Worker.Orchestration;

/// <summary>
/// Thrown by any activity that must stop the run - secure-deny, gate refusal, or a normalizer/
/// sanitizer violation. ReasonCode is a fixed, small vocabulary (SCOPE_DENIED, NOT_ENTITLED,
/// GATE_REFUSED, NOT_NORMALIZABLE, POST_SANITIZE_VIOLATION) - item 16 (failure UX, not this
/// slice) switches on these to pick the right user-safe message per CLAUDE.md/API_CONTRACTS.md's
/// four failure classes. Never put diagnostic detail in the message that reaches a user - that is
/// what InternalDiagnostics is for elsewhere (see PublishGateResult).
/// </summary>
public sealed class OrchestrationRefusedException(string reasonCode, string message, IReadOnlyList<string>? internalDiagnostics = null)
    : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
    public IReadOnlyList<string> InternalDiagnostics { get; } = internalDiagnostics ?? [];
}

namespace Insights.Domain;

/// <summary>
/// The deterministic arbiter's verdict (design doc 3.5, 11.3) - the non-negotiable layer that
/// sits ON TOP OF reflection, not in place of it. Reflection catches judgement errors
/// (wrong emphasis, an overclaiming story); this catches what reflection cannot: a hallucinated
/// number, an out-of-scope row.
///
/// <see cref="UserFacingRefusal"/> is the ONLY thing that may ever reach the user - the fixed
/// generic message from 11.3. <see cref="InternalDiagnostics"/> carries the real reason and
/// goes to LangFuse/Grafana with an alert; NEVER surface it to a caller outside the worker -
/// "reconciliation variance of 3 on branch X" means nothing to a CCO and exposes internals.
/// </summary>
public sealed record PublishGateResult(bool Approved, string? UserFacingRefusal, IReadOnlyList<string> InternalDiagnostics)
{
    public static readonly PublishGateResult Approve = new(true, null, []);

    public static PublishGateResult Refuse(IReadOnlyList<string> diagnostics) => new(
        false,
        "We couldn't generate this report to our accuracy standard. Our team has been notified.",
        diagnostics);
}

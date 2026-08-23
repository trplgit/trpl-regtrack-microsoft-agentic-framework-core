namespace Insights.Domain;

/// <summary>
/// One progress snapshot for API_CONTRACTS.md §4. Terminal states are
/// <c>complete</c> and <c>failed</c>.
/// </summary>
/// <param name="RunId">The Durable Task instance id (see <see cref="InsightsRunId"/>).</param>
/// <param name="Status"><c>queued</c> | <c>running</c> | <c>complete</c> | <c>failed</c>.</param>
/// <param name="Stage">One of the seven contract stage names, or null before the first stage lands.</param>
/// <param name="Message">
/// [TRAP] USER-SAFE TEXT ONLY, and only on failure. The gate's real diagnostics - "reconciliation
/// variance of 3 on branch X", a scope audit violation, a dictionary gap - are exactly the kind of
/// internal detail spec §11.3 forbids putting in front of a customer. They go to the logs and the
/// alert, never here. Whoever populates this field is the last line of that defence.
/// </param>
public sealed record InsightsRunStatus(
    string RunId,
    string Status,
    string? Stage,
    int StagesComplete,
    int StagesTotal,
    string? Message)
{
    public bool IsTerminal => Status is "complete" or "failed";
}

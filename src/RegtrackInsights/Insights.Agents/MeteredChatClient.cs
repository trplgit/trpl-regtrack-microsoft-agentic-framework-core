using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// Reports token usage for every LLM call, and enforces a per-call ceiling.
///
/// WHY IT WRAPS THE CHAT CLIENT rather than the four agent classes: MafAgentFactory builds every
/// agent from one <see cref="IChatClient"/>, so wrapping there covers composition, both
/// reflection agents and report HTML at a single point - and covers any agent added later without
/// anyone remembering to instrument it. Instrumentation that has to be repeated per agent is
/// instrumentation that eventually gets missed.
///
/// [TRAP] The cap is checked AFTER the call, not before, and that is deliberate: the tokens are
/// already billed by then. It exists to stop a runaway from continuing through the remaining
/// stages of a report (4 calls plus up to 2 reflection loops), not to prevent the first
/// overspend - which nothing on this side of the wire can do. Same shape as FreeDigestWriter's
/// tokenCap, except the paid path refuses instead of falling back.
/// </summary>
public sealed class MeteredChatClient(
    IChatClient inner,
    string stage,
    string model,
    ILlmUsageRecorder recorder,
    int? maxTokensPerCall) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        /*  Usage is nullable on the M.E.AI contract and some providers omit it. A missing count is
            reported as zero rather than skipped: a stage that silently stops appearing in the
            metrics looks identical to a stage that stopped running, and the second is the one you
            need to notice.                                                                       */
        var usage = new LlmUsage(
            stage,
            model,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0);

        /*  Guarded HERE as well as inside the recorder, and not redundantly: this method is
            holding a response the tenant has ALREADY been billed for. Whatever an implementation
            promises, letting its failure propagate from this line would throw away paid-for work
            because a metrics backend was down. The cap check below is a deliberate refusal; this
            is not.                                                                              */
        try
        {
            recorder.Record(usage);
        }
        catch
        {
            // Metrics are not worth a response.
        }

        if (maxTokensPerCall is int cap && cap > 0 && usage.TotalTokens > cap)
            throw new LlmBudgetExceededException(usage, cap);

        return response;
    }
}

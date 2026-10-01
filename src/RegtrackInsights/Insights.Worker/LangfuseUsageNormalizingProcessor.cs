using System.Diagnostics;
using OpenTelemetry;

namespace Insights.Worker;

/// <summary>
/// [ADDED 2026-09-28] Runs on every LLM span just before it is exported to LangFuse.
///
/// Microsoft.Extensions.AI's OpenTelemetryChatClient writes thinking tokens as
/// <c>gen_ai.usage.reasoning.output_tokens</c>. LangFuse turns that into a usage key with a DOT in
/// it ("reasoning.output_tokens") and subtracts it from <c>output</c> - and LangFuse's model
/// definition form rejects a price key containing a dot ("Invalid price entries", found live), so
/// thinking tokens (~25% of real cost on gpt-5.6-sol) were being priced at zero.
///
/// Thinking tokens are billed at the output rate and OpenAI's own output count already includes
/// them, so the fix is to stop LangFuse splitting them out: the usage attribute is removed and the
/// value kept under a non-usage name for visibility. LangFuse then prices the whole output count at
/// the output price with a plain input / input_cached_tokens / output model definition.
/// Must be registered BEFORE the OTLP exporter so it runs first.
/// </summary>
public sealed class LangfuseUsageNormalizingProcessor : BaseProcessor<Activity>
{
    public const string ReasoningUsageTag = "gen_ai.usage.reasoning.output_tokens";
    public const string ReasoningInfoTag = "insights.reasoning_output_tokens";

    public override void OnEnd(Activity data)
    {
        if (data.GetTagItem(ReasoningUsageTag) is { } reasoningTokens)
        {
            data.SetTag(ReasoningInfoTag, reasoningTokens);
            data.SetTag(ReasoningUsageTag, null); // null removes the tag
        }
    }
}

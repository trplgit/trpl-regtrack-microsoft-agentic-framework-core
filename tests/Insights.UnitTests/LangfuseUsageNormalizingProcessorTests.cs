using System.Diagnostics;
using Insights.Worker;

namespace Insights.UnitTests;

public sealed class LangfuseUsageNormalizingProcessorTests
{
    [Fact]
    public void OnEnd_MovesReasoningTokensOffTheUsageAttribute_AndKeepsOutputWhole()
    {
        using var activity = new Activity("chat gpt-5.6-sol");
        activity.SetTag("gen_ai.usage.output_tokens", 18588);
        activity.SetTag("gen_ai.usage.reasoning.output_tokens", 2568);
        activity.SetTag("gen_ai.usage.input_tokens", 8409);

        new LangfuseUsageNormalizingProcessor().OnEnd(activity);

        Assert.Null(activity.GetTagItem("gen_ai.usage.reasoning.output_tokens"));
        Assert.Equal(2568, activity.GetTagItem("insights.reasoning_output_tokens"));
        Assert.Equal(18588, activity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(8409, activity.GetTagItem("gen_ai.usage.input_tokens"));
    }

    [Fact]
    public void OnEnd_LeavesASpanWithoutReasoningTokensUntouched()
    {
        using var activity = new Activity("chat gpt-5.6-sol");
        activity.SetTag("gen_ai.usage.output_tokens", 100);

        new LangfuseUsageNormalizingProcessor().OnEnd(activity);

        Assert.Null(activity.GetTagItem("insights.reasoning_output_tokens"));
        Assert.Equal(100, activity.GetTagItem("gen_ai.usage.output_tokens"));
    }
}

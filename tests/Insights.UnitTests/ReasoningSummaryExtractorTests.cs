using Insights.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace Insights.UnitTests;

public class ReasoningSummaryExtractorTests
{
    [Fact]
    public void Extract_MessageHasReasoningContent_ReturnsReasoningText()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent("weigh option A vs B, pick A because..."),
                new TextContent("final answer text"),
            ]),
        };

        var result = ReasoningSummaryExtractor.Extract(messages);

        Assert.Equal("weigh option A vs B, pick A because...", result);
    }

    [Fact]
    public void Extract_NoReasoningContentAnywhere_ReturnsNull()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [new TextContent("final answer text")]),
        };

        var result = ReasoningSummaryExtractor.Extract(messages);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_MultipleReasoningPartsAcrossMessages_JoinsThemInOrder()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("first part")]),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("second part")]),
        };

        var result = ReasoningSummaryExtractor.Extract(messages);

        Assert.Equal("first part\n\nsecond part", result);
    }

    [Fact]
    public void Extract_ReasoningContentIsWhitespaceOnly_TreatedAsAbsent()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("   "), new TextContent("final answer")]),
        };

        var result = ReasoningSummaryExtractor.Extract(messages);

        Assert.Null(result);
    }
}

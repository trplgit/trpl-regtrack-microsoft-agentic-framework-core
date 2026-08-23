using Insights.Agents;
using Microsoft.Extensions.AI;

namespace Insights.UnitTests;

/// <summary>
/// Item 17. A paid report is 4 LLM calls plus up to 2 reflection loops and nothing bounded any of
/// them - these pin that every call is now counted, and that a runaway one stops the run.
/// </summary>
public sealed class MeteredChatClientTests
{
    private static MeteredChatClient Build(
        long inputTokens, long outputTokens, ILlmUsageRecorder recorder, int? cap = null, bool omitUsage = false) =>
        new(new StubChatClient(inputTokens, outputTokens, omitUsage), "CompositionAgent", "gpt-5.2", recorder, cap);

    [Fact]
    public async Task Records_TokensStageAndModel_ForEveryCall()
    {
        var recorder = new RecordingUsageRecorder();
        var client = Build(1200, 340, recorder);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var usage = Assert.Single(recorder.Recorded);
        Assert.Equal("CompositionAgent", usage.Stage);
        Assert.Equal("gpt-5.2", usage.Model);
        Assert.Equal(1200, usage.InputTokens);
        Assert.Equal(340, usage.OutputTokens);
        Assert.Equal(1540, usage.TotalTokens);
    }

    /// <summary>
    /// A provider that omits usage must still produce a row. A stage that silently stops
    /// appearing in the metrics is indistinguishable from a stage that stopped running, and the
    /// second is the one worth noticing.
    /// </summary>
    [Fact]
    public async Task Records_ZeroRatherThanNothing_WhenTheProviderOmitsUsage()
    {
        var recorder = new RecordingUsageRecorder();
        var client = Build(0, 0, recorder, omitUsage: true);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var usage = Assert.Single(recorder.Recorded);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public async Task Throws_WhenOneCallExceedsTheCap()
    {
        var recorder = new RecordingUsageRecorder();
        var client = Build(9000, 2000, recorder, cap: 10_000);

        var ex = await Assert.ThrowsAsync<LlmBudgetExceededException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(10_000, ex.CapTokens);
        Assert.Equal(11_000, ex.Usage.TotalTokens);

        // Still recorded before refusing - the tokens were billed whether we continue or not,
        // and a refused run that vanishes from the spend metrics understates real cost.
        Assert.Single(recorder.Recorded);
    }

    [Fact]
    public async Task DoesNotThrow_WhenExactlyAtTheCap()
    {
        var client = Build(6000, 4000, ILlmUsageRecorder.Null, cap: 10_000);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.NotNull(response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task DoesNotThrow_WhenNoCapIsConfigured(int? cap)
    {
        var client = Build(500_000, 500_000, ILlmUsageRecorder.Null, cap);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.NotNull(response);
    }

    /// <summary>
    /// A metrics failure must never discard a response the tenant has already paid for.
    /// </summary>
    [Fact]
    public async Task SwallowsRecorderFailures()
    {
        var client = Build(100, 100, new ThrowingUsageRecorder());

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.NotNull(response);
    }

    private sealed class RecordingUsageRecorder : ILlmUsageRecorder
    {
        public List<LlmUsage> Recorded { get; } = [];
        public void Record(LlmUsage usage) => Recorded.Add(usage);
    }

    /// <summary>Stands in for InsightsCostMetrics, which swallows internally. Here the swallow is the client's job.</summary>
    private sealed class ThrowingUsageRecorder : ILlmUsageRecorder
    {
        public void Record(LlmUsage usage) => throw new InvalidOperationException("metrics backend down");
    }

    private sealed class StubChatClient(long inputTokens, long outputTokens, bool omitUsage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));

            if (!omitUsage)
                response.Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

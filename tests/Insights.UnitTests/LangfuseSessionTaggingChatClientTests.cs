using System.Diagnostics;
using Insights.Agents;
using Microsoft.Extensions.AI;

namespace Insights.UnitTests;

/// <summary>
/// Confirms the real mechanism LangFuse needs: `langfuse.session.id` must land on the actual chat
/// span (Activity.Current) at the moment the call happens - not merely be passed around as a
/// string. Uses a real ActivitySource + ActivityListener (not a mock) since Activity.Current only
/// becomes non-null when something is actually listening, exactly the condition
/// OpenTelemetryChatClient creates in production.
/// </summary>
public sealed class LangfuseSessionTaggingChatClientTests : IDisposable
{
    private static readonly ActivitySource Source = new("Insights.UnitTests.Langfuse");
    private readonly ActivityListener _listener;

    public LangfuseSessionTaggingChatClientTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task GetResponseAsync_SessionPushed_TagsTheActiveSpan()
    {
        var client = new LangfuseSessionTaggingChatClient(new StubChatClient());

        using var _session = LangfuseSessionContext.Push("run-abc-123");
        using var activity = Source.StartActivity("chat gpt-5.6-sol")!;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("run-abc-123", activity.GetTagItem("langfuse.session.id"));
    }

    [Fact]
    public async Task GetResponseAsync_NoSessionPushed_DoesNotTagTheSpan()
    {
        var client = new LangfuseSessionTaggingChatClient(new StubChatClient());

        using var activity = Source.StartActivity("chat gpt-5.6-sol")!;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Null(activity.GetTagItem("langfuse.session.id"));
    }

    [Fact]
    public async Task GetResponseAsync_NoActiveSpan_DoesNotThrow()
    {
        var client = new LangfuseSessionTaggingChatClient(new StubChatClient());

        using var _session = LangfuseSessionContext.Push("run-abc-123");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.NotNull(response);
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

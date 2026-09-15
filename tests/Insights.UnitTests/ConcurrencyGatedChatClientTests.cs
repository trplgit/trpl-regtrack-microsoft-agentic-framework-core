using Insights.Agents;
using Microsoft.Extensions.AI;

namespace Insights.UnitTests;

/// <summary>
/// Item 17. LlmConcurrencyGateTests pins the gate itself; these pin that the IChatClient decorator
/// actually goes THROUGH it - a real call blocks on a full gate and a slot frees on release, same
/// as a bare gate, but exercised through the shape MafAgentFactory actually wires (a chat client
/// wrapping another chat client), and confirmed the slot is freed even when the wrapped call throws.
/// </summary>
public sealed class ConcurrencyGatedChatClientTests
{
    [Fact]
    public async Task BlocksUntilASlotIsFree_ThenDelegatesToInner()
    {
        var gate = new LlmConcurrencyGate(1);
        using var held = await gate.AcquireAsync(CancellationToken.None);

        var inner = new BlockingStubChatClient();
        var client = new ConcurrencyGatedChatClient(inner, gate);

        var callTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        await Task.Delay(100);
        Assert.False(inner.WasCalled); // still queued behind the held slot

        held.Dispose();
        var response = await callTask;

        Assert.True(inner.WasCalled);
        Assert.NotNull(response);
    }

    /// <summary>[TRAP] A failed inner call must still free the slot, or one bad call starves every run after it.</summary>
    [Fact]
    public async Task ReleasesTheSlot_WhenTheInnerCallThrows()
    {
        var gate = new LlmConcurrencyGate(1);
        var client = new ConcurrencyGatedChatClient(new ThrowingStubChatClient(), gate);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        // If the slot leaked, this would hang.
        using var _token = await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingStubChatClient : IChatClient
    {
        public bool WasCalled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingStubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated LLM call failure");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

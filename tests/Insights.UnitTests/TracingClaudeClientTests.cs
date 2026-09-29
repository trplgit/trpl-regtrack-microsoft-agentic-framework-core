using System.Diagnostics;
using Insights.Agents;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// TracingClaudeClient (2026-09-29): every free-digest model call becomes one LangFuse generation
/// carrying tokens and the tenant / user / month context - and tracing never changes the call.
/// </summary>
public class TracingClaudeClientTests
{
    private sealed class FakeClient(ClaudeCompletionResult result) : IClaudeClient
    {
        public Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingClient : IClaudeClient
    {
        public Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("boom");
    }

    private static (ActivityListener Listener, List<Activity> Stopped) Listen()
    {
        var stopped = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == FreeDigestTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, stopped);
    }

    [Fact]
    public async Task Card_call_is_traced_with_tokens_and_the_tenant_user_month_context()
    {
        var (listener, stopped) = Listen();
        using (listener)
        {
            var client = new TracingClaudeClient(new FakeClient(new ClaudeCompletionResult("ok", 1200, 340, false)), "azure_openai", "gpt-5.6-luna");

            using (FreeDigestTelemetry.Push(new FreeDigestTraceContext(23, 357, "card", "location", new DateOnly(2026, 9, 20))))
                await client.CompleteAsync("sys", "user", 900);

            var span = Assert.Single(stopped, a => (string?)a.GetTagItem("langfuse.session.id") == "basic-23-2026-09-20");
            Assert.Equal("gpt-5.6-luna", span.GetTagItem("gen_ai.request.model"));
            Assert.Equal(1200, span.GetTagItem("gen_ai.usage.input_tokens"));
            Assert.Equal(340, span.GetTagItem("gen_ai.usage.output_tokens"));
            Assert.Equal("card:location", span.GetTagItem("langfuse.trace.name"));
            Assert.Equal("357", span.GetTagItem("langfuse.user.id"));
            Assert.Equal(23, span.GetTagItem("langfuse.trace.metadata.tenant_id"));
            Assert.Equal("2026-09", span.GetTagItem("langfuse.trace.metadata.month"));
            Assert.Contains("month-2026-09", (string[])span.GetTagItem("langfuse.trace.tags")!);
            Assert.Contains("tenant-23", (string[])span.GetTagItem("langfuse.trace.tags")!);
            Assert.Null(span.GetTagItem("langfuse.observation.input"));   // content capture is off by default
        }
    }

    [Fact]
    public async Task Result_is_passed_through_unchanged_with_no_listener()
    {
        var expected = new ClaudeCompletionResult("body", 10, 20, true);
        var client = new TracingClaudeClient(new FakeClient(expected), "azure_openai", "gpt-5.6-luna");

        Assert.Same(expected, await client.CompleteAsync("sys", "user", 100));
    }

    [Fact]
    public async Task A_failing_call_is_rethrown_and_marked_as_an_error()
    {
        var (listener, stopped) = Listen();
        using (listener)
        {
            var client = new TracingClaudeClient(new ThrowingClient(), "azure_openai", "gpt-5.6-luna");

            using (FreeDigestTelemetry.Push(new FreeDigestTraceContext(1285, 11384, "email", "overview", new DateOnly(2026, 9, 6))))
                await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("sys", "user", 100));

            var span = Assert.Single(stopped, a => (string?)a.GetTagItem("langfuse.session.id") == "basic-1285-2026-09-06");
            Assert.Equal(ActivityStatusCode.Error, span.Status);
        }
    }
}

using System.Net;
using Insights.Agents;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-20] Real bug found live: every free-tier digest composition today 404'd with
/// "Response status code does not indicate success: 404 (Resource Not Found)" - traced to
/// Llm:AzureOpenAi:Endpoint being configured in the newer unified-API style
/// (".../openai.azure.com/openai/v1", same shape Llm:Maf:Endpoint correctly uses for the modern
/// OpenAI SDK client) while this class's hand-rolled URL builder assumes a bare resource root and
/// appends its own "/openai/deployments/..." path - doubling to ".../openai/v1/openai/deployments/..."
/// against a real Azure endpoint, which 404s. Confirmed via dt.History on the real UAT task hub
/// (freedigest-gen-1283-2026-09-20, 3 retries, identical 404 every time).
/// </summary>
public sealed class AzureOpenAiChatClientTests
{
    private const string SuccessBody = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}
        """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SuccessBody) });
        }
    }

    [Theory]
    [InlineData("https://trpl-uat-saas-ai-2-resource.openai.azure.com")]
    [InlineData("https://trpl-uat-saas-ai-2-resource.openai.azure.com/")]
    [InlineData("https://trpl-uat-saas-ai-2-resource.openai.azure.com/openai/v1")]
    [InlineData("https://trpl-uat-saas-ai-2-resource.openai.azure.com/openai/v1/")]
    public async Task CompleteAsync_BuildsCorrectDeploymentUrl_RegardlessOfEndpointStyle(string configuredEndpoint)
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new AzureOpenAiChatClient(httpClient, configuredEndpoint, "gpt-4o-mini", "fake-key");

        await client.CompleteAsync("system prompt", "user message", 100);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            "https://trpl-uat-saas-ai-2-resource.openai.azure.com/openai/deployments/gpt-4o-mini/chat/completions",
            handler.LastRequestUri!.GetLeftPart(UriPartial.Path));
    }
}

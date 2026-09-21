using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Insights.Agents;

/// <summary>
/// Azure OpenAI client, same <see cref="IClaudeClient"/> shape as <see cref="AnthropicClaudeClient"/>.
/// One-off smoke-test swap (user decision, 2026-08-19) - CLAUDE.md Sections 3.2/7 still
/// document Claude as the provider. Deployment-based routing, api-key header, api-version
/// query param - different shape from OpenAiChatClient's public-API call.
/// </summary>
public sealed class AzureOpenAiChatClient(HttpClient httpClient, string endpoint, string deployment, string apiKey, string apiVersion = "2024-02-15-preview") : IClaudeClient
{
    // [FIX 2026-09-20, FOUND LIVE] This class expects a BARE resource root and appends its own
    // "/openai/deployments/{deployment}/chat/completions" path - but Llm:AzureOpenAi:Endpoint has
    // been set to the same "/openai/v1"-suffixed style Llm:Maf:Endpoint correctly uses for the
    // modern OpenAI SDK client (which appends its own path differently). Given the suffixed style
    // unmodified, this doubled to ".../openai/v1/openai/deployments/..." against the real Azure
    // endpoint - confirmed live via dt.History on the real UAT task hub: every free-tier digest
    // composition 404'd today (freedigest-gen-1283-2026-09-20, identical 404 on all 3 retries).
    // Stripping a trailing "/openai/v1" here makes this class correct regardless of which style
    // the endpoint config uses, rather than depending on every caller getting the format right.
    private static readonly string[] UnifiedApiSuffixes = ["/openai/v1", "/v1"];

    private static string NormalizeResourceRoot(string endpoint)
    {
        var root = endpoint.TrimEnd('/');
        foreach (var suffix in UnifiedApiSuffixes)
        {
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return root[..^suffix.Length];
        }
        return root;
    }

    public async Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        var url = $"{NormalizeResourceRoot(endpoint)}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent.Create(new AzureOpenAiRequest(
            maxTokens,
            [new AzureOpenAiMessage("system", systemPrompt), new AzureOpenAiMessage("user", userMessage)]));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AzureOpenAiResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Azure OpenAI API returned an empty body.");

        var choice = body.Choices[0];
        return new ClaudeCompletionResult(choice.Message.Content, body.Usage.PromptTokens, body.Usage.CompletionTokens, choice.FinishReason == "length");
    }

    private sealed record AzureOpenAiRequest([property: JsonPropertyName("max_tokens")] int MaxTokens, AzureOpenAiMessage[] Messages);
    private sealed record AzureOpenAiMessage(string Role, string Content);
    private sealed record AzureOpenAiResponse(AzureOpenAiChoice[] Choices, AzureOpenAiUsage Usage);
    private sealed record AzureOpenAiChoice(AzureOpenAiMessage Message, [property: JsonPropertyName("finish_reason")] string FinishReason);
    private sealed record AzureOpenAiUsage([property: JsonPropertyName("prompt_tokens")] int PromptTokens, [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}

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
    public async Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
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

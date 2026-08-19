using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Insights.Agents;

/// <summary>
/// GPT-4o-mini client, same <see cref="IClaudeClient"/> shape as <see cref="AnthropicClaudeClient"/>.
/// CLAUDE.md Section 7 documents Claude as the provider - this exists for a one-off smoke
/// test only (user decision, 2026-08-19), not a permanent switch. Do not wire this into
/// default DI without updating CLAUDE.md Sections 3.2/7 and appsettings Llm:Provider first.
/// </summary>
public sealed class OpenAiChatClient(HttpClient httpClient, string apiKey, string model = "gpt-4o-mini") : IClaudeClient
{
    public async Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new OpenAiRequest(
            model,
            maxTokens,
            [new OpenAiMessage("system", systemPrompt), new OpenAiMessage("user", userMessage)]));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken)
            ?? throw new InvalidOperationException("OpenAI API returned an empty body.");

        var choice = body.Choices[0];
        return new ClaudeCompletionResult(choice.Message.Content, body.Usage.PromptTokens, body.Usage.CompletionTokens, choice.FinishReason == "length");
    }

    private sealed record OpenAiRequest(string Model, [property: JsonPropertyName("max_tokens")] int MaxTokens, OpenAiMessage[] Messages);
    private sealed record OpenAiMessage(string Role, string Content);
    private sealed record OpenAiResponse(OpenAiChoice[] Choices, OpenAiUsage Usage);
    private sealed record OpenAiChoice(OpenAiMessage Message, [property: JsonPropertyName("finish_reason")] string FinishReason);
    private sealed record OpenAiUsage([property: JsonPropertyName("prompt_tokens")] int PromptTokens, [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}

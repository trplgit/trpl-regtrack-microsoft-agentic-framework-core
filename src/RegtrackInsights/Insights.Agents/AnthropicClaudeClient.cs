using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Insights.Agents;

/// <inheritdoc cref="IClaudeClient"/>
public sealed class AnthropicClaudeClient(HttpClient httpClient, string apiKey, string model) : IClaudeClient
{
    private const string ApiVersion = "2023-06-01";

    public async Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Content = JsonContent.Create(new AnthropicRequest(
            model,
            maxTokens,
            systemPrompt,
            [new AnthropicMessage("user", userMessage)]));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Anthropic API returned an empty body.");

        var text = string.Concat(body.Content.Where(b => b.Type == "text").Select(b => b.Text));
        return new ClaudeCompletionResult(text, body.Usage.InputTokens, body.Usage.OutputTokens, body.StopReason == "max_tokens");
    }

    private sealed record AnthropicRequest(string Model, [property: JsonPropertyName("max_tokens")] int MaxTokens, string System, AnthropicMessage[] Messages);
    private sealed record AnthropicMessage(string Role, string Content);
    private sealed record AnthropicResponse([property: JsonPropertyName("stop_reason")] string? StopReason, AnthropicContentBlock[] Content, AnthropicUsage Usage);
    private sealed record AnthropicContentBlock(string Type, string Text);
    private sealed record AnthropicUsage([property: JsonPropertyName("input_tokens")] int InputTokens, [property: JsonPropertyName("output_tokens")] int OutputTokens);
}

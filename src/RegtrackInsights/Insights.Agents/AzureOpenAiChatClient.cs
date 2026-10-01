using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Insights.Agents;

/// <summary>
/// Azure OpenAI client, same <see cref="IClaudeClient"/> shape as <see cref="AnthropicClaudeClient"/>.
/// One-off smoke-test swap (user decision, 2026-08-19) - CLAUDE.md Sections 3.2/7 still
/// document Claude as the provider. Deployment-based routing, api-key header, api-version
/// query param - different shape from OpenAiChatClient's public-API call.
/// </summary>
/// <param name="temperature">
/// <c>Llm:AzureOpenAi:Temperature</c>, or null to send none and let the deployment use its own
/// default. Null is not the same as 0: the field is omitted from the request body entirely, which
/// matters because some deployments reject any explicit temperature. Lower values make the digest
/// wording more repeatable between runs; the numbers never come from the model either way.
/// </param>
/// <param name="reasoningEffort">
/// <c>Llm:AzureOpenAi:ReasoningEffort</c>, or null for a non-reasoning deployment. Setting it also
/// switches the output budget to <c>max_completion_tokens</c>: a reasoning model rejects
/// <c>max_tokens</c> outright, and rejects <c>temperature</c> as well, so the two must move together.
/// </param>
/// <param name="verbosity"><c>Llm:AzureOpenAi:Verbosity</c> - low gives tighter prose and fewer output tokens.</param>
/// <param name="maxOutputTokens">
/// <c>Llm:AzureOpenAi:MaxOutputTokens</c>, used in place of the caller's per-reply estimate when a
/// reasoning effort is set. REASONING TOKENS COME OUT OF THIS BUDGET, so a reply-sized number lets
/// the model think itself out of an answer and return an empty body.
/// </param>
public sealed class AzureOpenAiChatClient(
    HttpClient httpClient,
    string endpoint,
    string deployment,
    string apiKey,
    double? temperature = null,
    string? reasoningEffort = null,
    string? verbosity = null,
    int? maxOutputTokens = null,
    string apiVersion = "2024-02-15-preview") : IClaudeClient
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

        var reasoning = reasoningEffort is { Length: > 0 };
        var budget = reasoning ? maxOutputTokens ?? maxTokens : maxTokens;

        request.Content = JsonContent.Create(new AzureOpenAiRequest(
            reasoning ? null : maxTokens,
            reasoning ? budget : null,
            [new AzureOpenAiMessage("system", systemPrompt), new AzureOpenAiMessage("user", userMessage)],
            reasoning ? null : temperature,
            reasoningEffort,
            verbosity));

        using var response = await httpClient.SendAsync(request, cancellationToken);

        /*  The body names WHICH parameter a deployment rejected - "Unsupported parameter:
            'max_tokens'", "'temperature' is not supported with this model". EnsureSuccessStatusCode
            discards it and leaves only "400 (Bad Request)", which is unactionable.              */
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Azure OpenAI returned {(int)response.StatusCode} for deployment '{deployment}': "
                + await response.Content.ReadAsStringAsync(cancellationToken));

        var body = await response.Content.ReadFromJsonAsync<AzureOpenAiResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Azure OpenAI API returned an empty body.");

        var choice = body.Choices[0];
        return new ClaudeCompletionResult(choice.Message.Content, body.Usage.PromptTokens, body.Usage.CompletionTokens, choice.FinishReason == "length");
    }

    // Every optional field is omitted when null: a reasoning deployment rejects max_tokens and
    // temperature, and a non-reasoning one rejects reasoning_effort. Only one set is ever sent.
    private sealed record AzureOpenAiRequest(
        [property: JsonPropertyName("max_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens,
        [property: JsonPropertyName("max_completion_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxCompletionTokens,
        AzureOpenAiMessage[] Messages,
        [property: JsonPropertyName("temperature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort,
        [property: JsonPropertyName("verbosity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Verbosity);
    private sealed record AzureOpenAiMessage(string Role, string Content);
    private sealed record AzureOpenAiResponse(AzureOpenAiChoice[] Choices, AzureOpenAiUsage Usage);
    private sealed record AzureOpenAiChoice(AzureOpenAiMessage Message, [property: JsonPropertyName("finish_reason")] string FinishReason);
    private sealed record AzureOpenAiUsage([property: JsonPropertyName("prompt_tokens")] int PromptTokens, [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}

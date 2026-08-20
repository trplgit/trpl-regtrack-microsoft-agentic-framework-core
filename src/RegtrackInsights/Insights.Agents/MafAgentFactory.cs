#pragma warning disable OPENAI001 // GetResponsesClient()/AsIChatClient(ResponsesClient,...) - experimental in this SDK version, confirmed working via a live call (2026-08-20)

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Insights.Agents;

/// <summary>
/// Builds a MAF <see cref="AIAgent"/> - shared by every agent that talks to the LLM directly
/// (composition, composition reflection, narrative, narrative reflection, report HTML), so the
/// Responses-API wiring exists in exactly one place. Provider-agnostic by construction - MAF's
/// whole point - the specific wiring here (Responses API, GPT-5.2 via Azure AI Foundry) is a
/// temporary dev provider, not the documented default (CLAUDE.md 3.2/7 still name Claude).
/// Swapping providers later means a different factory body, not a different caller.
/// </summary>
public static class MafAgentFactory
{
    /// <summary>For agents whose contract is a JSON object (composition, reflection, narrative).</summary>
    public static AIAgent CreateJsonAgent(string endpoint, string model, string apiKey, string name, string description, string instructions) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Json);

    /// <summary>
    /// For agents whose output is NOT JSON - report HTML (05_report_html.md) produces a raw HTML
    /// document, and forcing ResponseFormat=Json here would be actively wrong, not just unhelpful.
    /// </summary>
    public static AIAgent CreateTextAgent(string endpoint, string model, string apiKey, string name, string description, string instructions) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Text);

    private static AIAgent Create(string endpoint, string model, string apiKey, string name, string description, string instructions, ChatResponseFormat responseFormat)
    {
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(model);

        var options = new ChatClientAgentOptions
        {
            Name = name,
            Description = description,
            ChatOptions = new ChatOptions
            {
                // Instructions lives on the INNER ChatOptions, not ChatClientAgentOptions itself -
                // confirmed via reflection, not guessed (2026-08-20).
                Instructions = instructions,
                ResponseFormat = responseFormat,
            },
        };

        return new ChatClientAgent(chatClient, options);
    }
}

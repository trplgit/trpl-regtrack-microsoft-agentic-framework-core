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
    /// <summary>
    /// The ActivitySource name Microsoft.Extensions.AI's built-in chat-client instrumentation
    /// actually uses, in the pinned 10.7.0 build - confirmed by inspecting the compiled
    /// Microsoft.Extensions.AI.dll directly (2026-08-25: OpenTelemetryConsts.DefaultSourceName is
    /// the literal string "Microsoft.Extensions.AI", not the "Experimental.Microsoft.Extensions.AI"
    /// name older preview versions used and some public examples still show). Insights.Worker.
    /// ObservabilityRegistration's AddSource(...) call must use this exact same constant, or it
    /// silently matches nothing and every LLM span is dropped before export - no error, just an
    /// empty LangFuse project.
    /// </summary>
    public const string ChatClientActivitySourceName = "Microsoft.Extensions.AI";

    /// <summary>For agents whose contract is a JSON object (composition, reflection, narrative).</summary>
    public static AIAgent CreateJsonAgent(string endpoint, string model, string apiKey, string name, string description, string instructions, ILlmUsageRecorder? usage = null, int? maxTokensPerCall = null, bool enableSensitiveTelemetry = false, LlmConcurrencyGate? concurrencyGate = null) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Json, usage, maxTokensPerCall, enableSensitiveTelemetry, concurrencyGate);

    /// <summary>
    /// For agents whose output is NOT JSON - report HTML (05_report_html_fixed_holistic.md) produces a raw HTML
    /// document, and forcing ResponseFormat=Json here would be actively wrong, not just unhelpful.
    /// </summary>
    public static AIAgent CreateTextAgent(string endpoint, string model, string apiKey, string name, string description, string instructions, ILlmUsageRecorder? usage = null, int? maxTokensPerCall = null, bool enableSensitiveTelemetry = false, LlmConcurrencyGate? concurrencyGate = null) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Text, usage, maxTokensPerCall, enableSensitiveTelemetry, concurrencyGate);

    private static AIAgent Create(string endpoint, string model, string apiKey, string name, string description, string instructions, ChatResponseFormat responseFormat, ILlmUsageRecorder? usage, int? maxTokensPerCall, bool enableSensitiveTelemetry, LlmConcurrencyGate? concurrencyGate)
    {
        /*  [BUG FOUND LIVE, 2026-09-01] The SDK's own default NetworkTimeout is 100 seconds
            (ClientPipelineOptions.NetworkTimeout - confirmed via the SDK's own
            TaskCanceledException message, which names this exact property). A real render call
            for a large tenant (a fixed-holistic report's location_rows can carry 600+ branches,
            plus up to Agents:MaxTokensPerCall of reasoning+output) can legitimately take longer
            than that to complete - confirmed live: a direct curl to this exact endpoint with a
            trivial prompt returned in ~2s, so the endpoint itself was never the problem, but two
            consecutive full-size render attempts both hit the 100s wall and were reported as
            "network failure" when the real cause was an undersized client timeout for this
            workload's size, not a transient connectivity issue. A RETRY does not fix this - the
            same oversized call hits the same 100s ceiling every time. Every agent this factory
            builds (Composition/Reflection/Narrative/Reflection/ReportHtml) can carry a
            comparably large payload, so this is set here, once, for all of them - not per-caller.  */
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromMinutes(5) });
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(model);

        /*  OTel wraps the RAW client, innermost, so its span timing measures the actual network
            call rather than anything the layers above add. EnableSensitiveData gates whether the
            span carries full prompt/response text (Otel:EnableSensitiveData, design doc Sec.3.2's
            two-projection audit: full content internally, never shown to a customer) - off by
            default, since tokens/cost/latency alone are useful without it and turning it on is a
            deliberate choice, not a default.                                                     */
        chatClient = new OpenTelemetryChatClient(chatClient, sourceName: ChatClientActivitySourceName)
        {
            EnableSensitiveData = enableSensitiveTelemetry,
        };

        /*  Metering wraps the CHAT CLIENT, so every agent this factory builds is instrumented at
            one point - including any added later, without anyone remembering to do it. The stage
            tag is the agent name, which is already a closed set of five values.                 */
        chatClient = new MeteredChatClient(chatClient, name, model, usage ?? ILlmUsageRecorder.Null, maxTokensPerCall);

        /*  OUTERMOST, deliberately - see ConcurrencyGatedChatClient's doc comment: queue-wait time
            must never be counted as part of the OTel span's latency or MeteredChatClient's timing,
            both of which should reflect the real network call only. Null when no concurrency cap
            is configured (Agents:MaxConcurrentLlmCalls unset) - same "optional, off until a real
            number is measured" stance as maxTokensPerCall above, not a guessed default.           */
        if (concurrencyGate is not null)
            chatClient = new ConcurrencyGatedChatClient(chatClient, concurrencyGate);

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

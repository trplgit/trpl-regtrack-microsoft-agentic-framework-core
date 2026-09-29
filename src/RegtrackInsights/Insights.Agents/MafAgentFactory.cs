#pragma warning disable OPENAI001 // GetResponsesClient()/AsIChatClient(ResponsesClient,...) - experimental in this SDK version, confirmed working via a live call (2026-08-20)

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

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

    /// <summary>
    /// For agents whose contract is a JSON object (composition, reflection, narrative).
    /// <paramref name="reasoningEffort"/> [ADDED 2026-09-14, DEFAULT CHANGED 2026-09-15] defaults
    /// to High, not null - necessary but NOT sufficient on its own (see the real finding below).
    /// </summary>
    /// <remarks>
    /// [REAL ROOT CAUSE, FOUND LIVE 2026-09-15] The empty-reasoning-table bug was never about
    /// effort level or about <c>ReasoningSummaryExtractor</c> reading the wrong field - both of
    /// those were red herrings chased earlier the same night. The actual cause: this factory's
    /// <c>ReasoningSummaryVerbosity</c> was hardcoded to <c>Auto</c>, and Auto silently returns
    /// ZERO summary parts on this Azure deployment regardless of effort level - confirmed live,
    /// repeatedly, both on a trivial prompt and a genuine multi-step reasoning prompt, at both
    /// effort=null and effort=High. Switching to <c>ReasoningSummaryVerbosity.Detailed</c> AND
    /// effort=High TOGETHER is what actually produces a real summary (verified live: SummaryParts
    /// count 1, real text, AND MEAI's own <c>TextReasoningContent.Text</c> populated too -
    /// <see cref="ReasoningSummaryExtractor"/> needed no change at all). Detailed+null effort was
    /// tried and still came back empty - both knobs are required together, neither alone is
    /// enough. An earlier code comment claimed gpt-5 "rejects Concise" as the reason Auto was
    /// chosen over an explicit verbosity - that comment never actually tried Detailed; it does
    /// work, no rejection.
    /// </remarks>
    public static AIAgent CreateJsonAgent(string endpoint, string model, string apiKey, string name, string description, string instructions, ILlmUsageRecorder? usage = null, int? maxTokensPerCall = null, bool enableSensitiveTelemetry = false, LlmConcurrencyGate? concurrencyGate = null, ResponseReasoningEffortLevel? reasoningEffort = null) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Json, usage, maxTokensPerCall, enableSensitiveTelemetry, concurrencyGate, reasoningEffort ?? ResponseReasoningEffortLevel.High, supportsReasoning: true);

    /// <summary>
    /// For agents whose output is NOT JSON - report HTML (05_report_html_fixed_holistic.md) produces a raw HTML
    /// document, and forcing ResponseFormat=Json here would be actively wrong, not just unhelpful.
    /// See <see cref="CreateJsonAgent"/>'s own remarks for the real reasoning-summary root cause.
    /// </summary>
    public static AIAgent CreateTextAgent(string endpoint, string model, string apiKey, string name, string description, string instructions, ILlmUsageRecorder? usage = null, int? maxTokensPerCall = null, bool enableSensitiveTelemetry = false, LlmConcurrencyGate? concurrencyGate = null, ResponseReasoningEffortLevel? reasoningEffort = null) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Text, usage, maxTokensPerCall, enableSensitiveTelemetry, concurrencyGate, reasoningEffort ?? ResponseReasoningEffortLevel.High, supportsReasoning: true);

    /// <summary>
    /// [ADDED 2026-09-26] For a genuinely non-reasoning model (gpt-4o-mini, the reasoning-trace
    /// explainer's own deployment) - CreateJsonAgent/CreateTextAgent both hardcode a
    /// RawRepresentationFactory that sets ReasoningOptions on EVERY call, defaulting effort to
    /// High. Found live: gpt-4o-mini rejects that outright with a real HTTP 400
    /// ("Unsupported parameter: 'reasoning.effort' is not supported with this model") - it has no
    /// reasoning/effort concept at all, unlike the o-series/gpt-5 family every other agent in this
    /// file targets. This method reuses the exact same client/pipeline wiring (OTel, metering,
    /// concurrency gate, network timeout) but never attaches ReasoningOptions - the one real
    /// difference a non-reasoning model needs.
    /// </summary>
    public static AIAgent CreateSimpleTextAgent(string endpoint, string model, string apiKey, string name, string description, string instructions, ILlmUsageRecorder? usage = null, int? maxTokensPerCall = null, bool enableSensitiveTelemetry = false, LlmConcurrencyGate? concurrencyGate = null) =>
        Create(endpoint, model, apiKey, name, description, instructions, ChatResponseFormat.Text, usage, maxTokensPerCall, enableSensitiveTelemetry, concurrencyGate, reasoningEffort: null, supportsReasoning: false);

    private static AIAgent Create(string endpoint, string model, string apiKey, string name, string description, string instructions, ChatResponseFormat responseFormat, ILlmUsageRecorder? usage, int? maxTokensPerCall, bool enableSensitiveTelemetry, LlmConcurrencyGate? concurrencyGate, ResponseReasoningEffortLevel? reasoningEffort, bool supportsReasoning)
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

        /*  [ADDED 2026-09-14] MUST be INNERMOST of all - i.e. wrapped by OpenTelemetryChatClient,
            not wrapping it - so it runs WHILE the real chat span (Activity.Current) is active, not
            before it starts or after it has already stopped. See its own doc comment. */
        chatClient = new LangfuseSessionTaggingChatClient(chatClient);

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
                // [MERGED 2026-09-15] Two features landed on this same option independently:
                // reasoningEffort (reginsights-staging, 2026-09-14) makes the effort level
                // per-caller-configurable (FreehandDimensions:ReasoningEffort) instead of
                // hardcoded, so every OTHER caller keeps the model's own default when it passes
                // null. ReasoningSummaryVerbosity (agent-reasoning-capture, this branch) requests
                // the vendor's own summary of its reasoning for EVERY call this factory makes,
                // regardless of effort level - it is the ONLY supported way to get any of the
                // model's reasoning back (OpenAI's terms forbid extracting raw chain-of-thought by
                // any other means). [CHANGED 2026-09-15, FROM Auto] Auto silently returned ZERO
                // summary parts on this deployment regardless of effort level, confirmed live -
                // Detailed does not have that problem (and, contrary to an earlier assumption
                // here, is NOT rejected by gpt-5 - only Concise ever was). Detailed alone is still
                // not enough on its own; it must be paired with effort=High (see CreateJsonAgent's
                // remarks) - both knobs are required together. See ReasoningSummaryExtractor for
                // how this is read back out of the response.
                //
                // [TRIED 2026-09-15, REVERTED SAME DAY] A requestReasoningSummary flag briefly let
                // one caller (render_html) skip this - ruled out as the cause of the real
                // mid-generation content-refusal seen live on Users/Minda (5 live runs: failures
                // happened with the summary both on and off, no real correlation). See
                // NormalizeActivity's own doc comment on the ongoing investigation into the real
                // trigger (the per-user leaderboard section, real employee names).
                // [ADDED 2026-09-26] supportsReasoning gates this whole block - a genuinely
                // non-reasoning model (gpt-4o-mini, CreateSimpleTextAgent) rejects ReasoningOptions
                // outright (real HTTP 400, "reasoning.effort" not supported), so it must never be
                // attached at all, not even with a null effort level. Every existing caller
                // (CreateJsonAgent/CreateTextAgent) always passes supportsReasoning: true, so this
                // is purely additive - no behaviour change for the o-series/gpt-5 agents.
                RawRepresentationFactory = supportsReasoning
                    ? _ => new CreateResponseOptions
                    {
                        ReasoningOptions = reasoningEffort is null
                            ? new ResponseReasoningOptions
                            {
                                ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Detailed,
                            }
                            : new ResponseReasoningOptions
                            {
                                ReasoningEffortLevel = reasoningEffort.Value,
                                ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Detailed,
                            },
                    }
                    : null,
            },
        };

        return new ChatClientAgent(chatClient, options);
    }
}

using System.Net.Http.Headers;
using System.Text;
using Insights.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Insights.Worker;

/// <summary>
/// OTel -> LangFuse (CLAUDE.md 7, build order item 17, open item O-4). Wires the two signals the
/// design doc names (Sec.12.2): LLM traces (spans, tool calls, tokens, cost, latency) and the
/// application-level cost metrics already built in <see cref="InsightsCostMetrics"/> and
/// <see cref="FreeDigestMetrics"/>. Grafana/Loki (the other half of Sec.12.2) is unrelated to this
/// - that is host-level logging config, not LLM telemetry.
///
/// Call from the composition root (Program.cs) AFTER AddInsightsPaidReportAgents, so the chat
/// clients it builds are already instrumented (Insights.Agents.MafAgentFactory wraps every one
/// with OpenTelemetryChatClient) before this method configures where the resulting spans go.
/// </summary>
public static class ObservabilityRegistration
{
    public static IServiceCollection AddInsightsObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["Otel:LangfuseEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            /*  [TRAP] Deliberately fails OPEN, not closed - matching MeteredChatClient/
                InsightsCostMetrics' established rule that observability must never be the reason a
                report generation run fails. LangFuse is not configured everywhere yet (a fresh dev
                box, a CI run, an environment nobody has wired keys into) - skip silently rather than
                throwing at startup, the one config surface in this file that is genuinely optional.
                Every OTHER key below (public key, secret key) is treated as required THE MOMENT an
                endpoint is present - a half-configured LangFuse integration should fail loud, not
                silently drop half its data.                                                        */
            return services;
        }

        var publicKey = Require(configuration, "Otel:LangfusePublicKey");
        var secretKey = Require(configuration, "Otel:LangfuseSecretKey");

        // LangFuse ingests standard OTLP/HTTP with Basic auth over the public/secret key pair -
        // documented integration path (design doc Sec.3.2 validated-integrations table), not
        // LangFuse-specific SDK wiring.
        var basicAuthValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));
        var baseUri = endpoint.TrimEnd('/');

        /*  [TRAP - found live 2026-08-25] OtlpExporterOptions.Headers takes a "key1=value1,key2=
            value2" STRING - and a Basic-auth base64 value routinely ends in "=" padding (confirmed:
            this exact pk/sk pair's base64 ends in "=="). A confirmed-working curl call with the
            same credentials landed 200 OK against LangFuse; the identical credentials through
            Headers="Authorization=Basic <base64>" produced ZERO observations in LangFuse with NO
            exception anywhere - the OTLP exporter's failures are invisible by default (it logs via
            an internal EventSource, not ILogger). HttpClientFactory sidesteps the whole Headers
            string-parsing path entirely by setting a real AuthenticationHeaderValue object - no
            string to mis-split, regardless of what characters the base64 value contains.
            Also required and previously MISSING: LangFuse v4's x-langfuse-ingestion-version header
            - confirmed via a direct curl call (2026-08-25) that omitting it is NOT accepted the
            same way as including it; matched here rather than assumed still-optional.          */
        HttpClient BuildAuthedClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuthValue);
            client.DefaultRequestHeaders.Add("x-langfuse-ingestion-version", "4");
            return client;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("regtrack-insights-worker"))
            .WithTracing(tracing => tracing
                // ONLY source added is the one that emits real LLM call spans. This is what
                // Sec.12.2's "filter non-LLM spans by instrumentation scope" means in practice for
                // an allowlist-shaped SDK like OpenTelemetry .NET: no HTTP-client or EF Core
                // auto-instrumentation package is referenced anywhere in this project, so those
                // scopes structurally never emit a span to filter in the first place. Otel:
                // FilterInstrumentationScopes documents the INTENT; there is nothing left for code
                // to filter until/unless a broader auto-instrumentation package is added later.
                .AddSource(MafAgentFactory.ChatClientActivitySourceName)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri($"{baseUri}/api/public/otel/v1/traces");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    o.HttpClientFactory = BuildAuthedClient;
                }))
            .WithMetrics(metrics => metrics
                // The Meters InsightsCostMetrics/FreeDigestMetrics/LlmConcurrencyGateMetrics/
                // DimensionFailureMetrics already emit into (System.Diagnostics.Metrics, no
                // exporter previously subscribed) - see each class' doc comment, written
                // specifically anticipating this call.
                .AddMeter(InsightsCostMetrics.MeterName)
                .AddMeter(FreeDigestMetrics.MeterName)
                .AddMeter(LlmConcurrencyGateMetrics.MeterName)
                .AddMeter(DimensionFailureMetrics.MeterName)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri($"{baseUri}/api/public/otel/v1/metrics");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    o.HttpClientFactory = BuildAuthedClient;
                }));

        return services;
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Otel:LangfuseEndpoint is set, so the rest of the LangFuse config is required, not optional.");
}

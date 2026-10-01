using System.Diagnostics;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-29] Wraps the free digest's <see cref="IClaudeClient"/> so every real model call
/// emits one span LangFuse reads as a GENERATION: the model, input/output tokens (LangFuse prices
/// them from the project's model definition), latency, and the tenant / user / subject from
/// <see cref="FreeDigestTelemetry.CurrentContext"/>. See <see cref="FreeDigestTelemetry"/>.
///
/// <para>Fails open: when nothing listens to the source (LangFuse Basic not configured),
/// StartActivity returns null and the call is exactly what it was. Tracing never changes or
/// fails a call - an exception from the inner client is recorded and rethrown untouched.</para>
/// </summary>
/// <param name="model">The name LangFuse matches its price on - the Azure deployment name.</param>
/// <param name="captureContent">
/// Otel:Basic:CaptureContent. Off by default: the prompt carries tenant figures and, where
/// FreeMonthly:AllowPersonNames is on, people's names.
/// </param>
public sealed class TracingClaudeClient(IClaudeClient inner, string system, string model, bool captureContent = false) : IClaudeClient
{
    public async Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        var context = FreeDigestTelemetry.CurrentContext;
        using var activity = FreeDigestTelemetry.Source.StartActivity($"chat {model}", ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag("gen_ai.system", system);
            activity.SetTag("gen_ai.request.model", model);
            activity.SetTag("gen_ai.request.max_tokens", maxTokens);
            activity.SetTag("langfuse.observation.type", "generation");
            /*  Tags are LangFuse's filter chips: lane (email / card), tenant and month, so cost can be
                cut per tenant and per month for either lane without a custom query. Per user is
                langfuse.user.id below.                                                          */
            activity.SetTag("langfuse.trace.tags", context is null
                ? new[] { "insights-basic", "unknown" }
                : new[] { "insights-basic", context.Lane, $"tenant-{context.TenantId}", $"month-{context.Month}" });

            if (context is not null)
            {
                activity.SetTag("langfuse.session.id", context.SessionId);
                activity.SetTag("langfuse.trace.name", context.TraceName);
                activity.SetTag("langfuse.user.id", context.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                activity.SetTag("langfuse.trace.metadata.tenant_id", context.TenantId);
                activity.SetTag("langfuse.trace.metadata.lane", context.Lane);
                activity.SetTag("langfuse.trace.metadata.subject", context.Subject);
                activity.SetTag("langfuse.trace.metadata.week_ending", context.WeekEnding.ToString("yyyy-MM-dd"));
                activity.SetTag("langfuse.trace.metadata.month", context.Month);
            }

            if (captureContent)
                activity.SetTag("langfuse.observation.input", $"[system]\n{systemPrompt}\n\n[user]\n{userMessage}");
        }

        try
        {
            var result = await inner.CompleteAsync(systemPrompt, userMessage, maxTokens, cancellationToken);

            if (activity is not null)
            {
                activity.SetTag("gen_ai.response.model", model);
                activity.SetTag("gen_ai.usage.input_tokens", result.InputTokens);
                activity.SetTag("gen_ai.usage.output_tokens", result.OutputTokens);
                activity.SetTag("gen_ai.response.finish_reasons", new[] { result.WasTruncated ? "length" : "stop" });
                if (captureContent)
                    activity.SetTag("langfuse.observation.output", result.Text);
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            throw;
        }
    }
}

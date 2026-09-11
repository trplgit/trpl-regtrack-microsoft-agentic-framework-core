using System.Diagnostics;
using System.Text.Json;
using DurableTask.Core;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// On-demand runner for one tenant's digest, mirroring InsightsRunOnceWorker's shape.
///
/// Enqueues the SAME orchestrations the scheduler enqueues - there is no second code path. This
/// exists only because the scheduler fires on its own day/hour, and waiting until Monday 9am IST
/// to test tenant 23's send is not a workflow.
///
/// Does nothing unless FreeDigest:RunOnce=true, so a bare `dotnet run` starts an idle host:
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23 --FreeDigest:Phase=generate
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23 --FreeDigest:Phase=send
///
/// [ADDED - ADR-0001, 2026-09-10] --FreeDigest:Phase selects which half of the two-phase digest to
/// run: "generate" (default) enqueues FreeDigestGenerateOrchestrator, "send" enqueues
/// FreeDigestSendOrchestrator.
/// </summary>
public sealed class FreeDigestRunOnceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    FreeDigestSettings settings,
    IHostApplicationLifetime lifetime,
    ILogger<FreeDigestRunOnceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FreeDigest:RunOnce", false))
        {
            logger.LogInformation(
                "FreeDigest:RunOnce is not set - host is idle. Pass --FreeDigest:RunOnce=true --FreeDigest:CustomerId=<id> to run one tenant's digest.");
            return;
        }

        using var scope = services.CreateScope();

        try
        {
            var customerId = configuration.GetValue<int?>("FreeDigest:CustomerId");
            if (customerId is not { } tenantId)
            {
                logger.LogError("FreeDigest:CustomerId is required - the digest orchestration runs one tenant at a time.");
                Environment.ExitCode = 1;
                return;
            }

            var phase = configuration["FreeDigest:Phase"] ?? "generate";

            /*  [ADDED, testing only] --FreeDigest:AsOf lets a manual GENERATE run target a week
                that has not already claimed this tenant's recipients (dbo.InsightsFreeDigestLog is
                shared across legacy/generate/send - a recipient already sent to this week via ANY
                path correctly blocks a re-generate for the SAME week). SEND ignores this - it is
                driven entirely by which artifact rows exist, never by a caller-supplied clock.    */
            var asOf = configuration["FreeDigest:AsOf"];

            var client = scope.ServiceProvider.GetRequiredService<TaskHubClient>();

            /*  Keyed on (phase, tenant, week), exactly as the scheduler keys it. Running this
                twice in one week attaches to - or is refused by - the existing instance rather
                than starting a parallel one, the same one-active-run-per-key rule 4.5 states for
                paid.                                                                            */
            var weekEnding = DigestWeek.EndingFor(string.IsNullOrWhiteSpace(asOf) ? DateTime.UtcNow : DateTime.Parse(asOf)).ToString("yyyy-MM-dd");

            OrchestrationInstance instance;
            string instanceId;

            switch (phase.ToLowerInvariant())
            {
                case "generate":
                    instanceId = $"freedigest-gen-{tenantId}-{weekEnding}";
                    logger.LogInformation("Enqueuing FreeDigestGenerateOrchestrator for tenant {TenantId} as {InstanceId}.", tenantId, instanceId);
                    instance = await client.CreateOrchestrationInstanceAsync(
                        FreeDigestGenerateOrchestrator.Name, FreeDigestGenerateOrchestrator.Version, instanceId,
                        new FreeDigestGenerateOrchestrationInput(tenantId, asOf));
                    break;

                case "send":
                    instanceId = $"freedigest-send-{tenantId}-{weekEnding}";
                    logger.LogInformation("Enqueuing FreeDigestSendOrchestrator for tenant {TenantId} as {InstanceId}.", tenantId, instanceId);
                    instance = await client.CreateOrchestrationInstanceAsync(
                        FreeDigestSendOrchestrator.Name, FreeDigestSendOrchestrator.Version, instanceId,
                        new FreeDigestSendOrchestrationInput(tenantId, settings.ArtifactFreshnessDays));
                    break;

                default:
                    logger.LogError("Unknown FreeDigest:Phase '{Phase}'. Expected generate or send.", phase);
                    Environment.ExitCode = 1;
                    return;
            }

            var stopwatch = Stopwatch.StartNew();
            var state = await client.WaitForOrchestrationAsync(instance, TimeSpan.FromMinutes(30), stoppingToken);
            stopwatch.Stop();

            logger.LogInformation("Status: {Status} (took {ElapsedSeconds:F1}s enqueue-to-completion).",
                state.OrchestrationStatus, stopwatch.Elapsed.TotalSeconds);

            if (state.OrchestrationStatus == OrchestrationStatus.Completed)
            {
                logger.LogInformation("Output: {Output}", state.Output);

                if (string.Equals(phase, "generate", StringComparison.OrdinalIgnoreCase))
                    LogGenerateCostAndScopeSummary(state.Output, tenantId, stopwatch.Elapsed);
            }
            else
            {
                logger.LogError("Run did not complete. Detail: {Output}", state.Output);
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*  A refusal - dictionary gap, missing scope, bad connection - must exit LOUD and
                non-zero, not scroll past in a log nobody reads.                                 */
            logger.LogError(ex, "Free digest run failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /*  Requested 2026-09-11: a per-tenant generate run should self-report cost, scope-resolution
        shape, token totals and wall time, in the log, rather than someone reading raw HTTP
        client trace lines and doing the arithmetic by hand every time (that is how every cost
        figure up to this point in the session was produced - error-prone and not repeatable).

        Pricing is the STANDARD PUBLISHED rate for gpt-4o-mini ($0.15/1M input, $0.60/1M output) -
        this is NOT read from any Azure billing API, so it will not reflect a negotiated/discounted
        rate if one exists on this account. Treat the dollar figure as an estimate, the token
        counts as exact (they come straight from the provider's own usage response).             */
    private const decimal InputTokenCostPerMillion = 0.15m;
    private const decimal OutputTokenCostPerMillion = 0.60m;

    private void LogGenerateCostAndScopeSummary(string orchestrationOutputJson, int tenantId, TimeSpan elapsed)
    {
        try
        {
            using var doc = JsonDocument.Parse(orchestrationOutputJson);
            var root = doc.RootElement;

            // Not every generate outcome carries these fields (e.g. a gate refusal returns them
            // as zero, which is correct - nothing was spent - but a genuinely older/different
            // output shape should not crash this summary, only skip it).
            if (!root.TryGetProperty("TotalInputTokens", out var inTokensEl) ||
                !root.TryGetProperty("TotalOutputTokens", out var outTokensEl))
                return;

            var inputTokens = inTokensEl.GetInt32();
            var outputTokens = outTokensEl.GetInt32();
            var scopeGroups = root.TryGetProperty("ScopeGroups", out var sg) ? sg.GetInt32() : 0;
            var llmCalls = root.TryGetProperty("LlmCalls", out var lc) ? lc.GetInt32() : 0;
            var generated = root.TryGetProperty("Generated", out var g) ? g.GetInt32() : 0;
            var alreadyGenerated = root.TryGetProperty("AlreadyGenerated", out var ag) ? ag.GetInt32() : 0;

            var estimatedCost = inputTokens / 1_000_000m * InputTokenCostPerMillion
                               + outputTokens / 1_000_000m * OutputTokenCostPerMillion;

            logger.LogInformation(
                "GENERATE SUMMARY - tenant {TenantId}: {ScopeGroups} scope group(s) resolved -> {LlmCalls} LLM call(s) made " +
                "({Generated} newly generated, {AlreadyGenerated} already had a complete artifact this week). " +
                "Tokens: {InputTokens} in + {OutputTokens} out = {TotalTokens} total. " +
                "Estimated cost: ${EstimatedCost:F6} (gpt-4o-mini standard rate - see this method's own doc comment for why this is an estimate, not a billed figure). " +
                "Wall time: {ElapsedSeconds:F1}s enqueue-to-completion.",
                tenantId, scopeGroups, llmCalls, generated, alreadyGenerated,
                inputTokens, outputTokens, inputTokens + outputTokens,
                estimatedCost, elapsed.TotalSeconds);
        }
        catch (JsonException ex)
        {
            // A cost/scope summary that fails to parse must never mask that the run itself
            // already succeeded (logged above, unconditionally) - this is purely additive.
            logger.LogWarning(ex, "Could not parse the orchestration output to log a cost/scope summary - the run itself still completed successfully.");
        }
    }
}

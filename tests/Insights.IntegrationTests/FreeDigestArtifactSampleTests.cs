using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// THROWAWAY, manual - produces ONE real, complete sample of what the ADR-0001 GENERATE phase
/// actually writes to blob (real UAT data, real LLM call, real unsubscribe-sentinel render), saved
/// to a local file for a human (Vinay) to open and inspect. Calls the same activities the real
/// orchestrator calls, but deliberately SKIPS the artifact-slot claim/complete SQL calls
/// (sql/29 - ClaimDigestArtifactActivity/PersistDigestArtifactActivity's own repository call) so it
/// works BEFORE sql/29 is deployed - it never writes to InsightsFreeDigestArtifact, only reads
/// existing tables and calls the LLM/renderer directly. Not wired into CI, never referenced
/// elsewhere.
///
/// Requires: ConnectionStrings__RegTrack (real UAT).
/// </summary>
public sealed class FreeDigestArtifactSampleTests(ITestOutputHelper output)
{
    private const int TenantId = 23; // ABC Training Company - genuine Basic-only tenant, confirmed live this session.

    private static readonly string OutputPath = Path.Combine(AppContext.BaseDirectory, "free-digest-artifact-sample.html");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test.");

    [Fact]
    public async Task WriteSampleArtifactAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RegTrack"] = RequireEnv("ConnectionStrings__RegTrack"),
                ["Azure:BlobConnectionString"] = RequireEnv("AZURE_BLOB_CONNECTION_STRING"),
                ["Azure:BlobContainer"] = "insights-reports-temp",
                ["Azure:DigestBlobContainer"] = "insights-digests",
                ["Budget:FreeDigestTokenCap"] = "1500",
                ["Llm:Provider"] = "azure_openai",
                ["Llm:AzureOpenAi:Endpoint"] = "https://trpl-prod-saas-ai-1.openai.azure.com/",
                ["Llm:AzureOpenAi:Deployment"] = "gpt-4o-mini",
                ["Llm:AzureOpenAi:ApiKey"] = RequireEnv("AZURE_OPENAI_API_KEY"),
                ["Email:Provider"] = "ElasticEmail",
                ["Email:ElasticEmail:ApiKey"] = "unused-in-this-script",
                ["Email:FromAddress"] = "noreply@teamleaseregtech.com",
                ["Email:FromName"] = "RegTrack Insights",
                ["Email:UpgradeUrl"] = "https://placeholder.invalid/regtrack/upgrade",
                ["Email:UnsubscribeBaseUrl"] = "https://placeholder.invalid/regtrack/insights/unsubscribe",
                ["Email:UnsubscribeSigningKey"] = "sample-only-not-a-real-secret",
                ["Email:TemplatePath"] = "./templates",
                ["Agents:PromptDirectory"] = "./prompts",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsFreeDigest(configuration);
        // ResolveDigestRecipientsActivity/ComposeDigestActivity are normally registered by
        // WorkerRegistration's TaskHubWorker wiring (AddInsightsOrchestration), not by
        // AddInsightsFreeDigest alone - added here directly, same reasoning ModelComparisonLabTests
        // uses for calling activities as plain objects instead of through DTFx.
        services.AddTransient<ResolveDigestRecipientsActivity>();
        services.AddTransient<ComposeDigestActivity>();
        services.AddSingleton<FreeDigestMetrics>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        // Real tenant 23 recipients already hold real claims for the current week from earlier
        // live testing this session - a fresh test-only week avoids "already sent" refusing this
        // sample run. Never affects anything real; this script never calls the send-claim path.
        var asOf = "2026-09-27T00:00:00Z";

        var resolved = await sp.GetRequiredService<ResolveDigestRecipientsActivity>()
            .RunAsync(new ResolveDigestRecipientsInput(TenantId, asOf));

        output.WriteLine($"Resolve: proceed={resolved.ShouldProceed}, groups={resolved.Groups.Count}, weekEnding={resolved.WeekEnding}");
        Assert.True(resolved.ShouldProceed, $"Gate refused: {resolved.Reason}");
        Assert.True(resolved.Groups.Count > 0, "No scope groups resolved - nothing to sample.");

        var group = resolved.Groups[0];
        var composed = await sp.GetRequiredService<ComposeDigestActivity>()
            .RunAsync(new ComposeDigestInput(TenantId, group.RepresentativeUserId, resolved.WeekEnding, asOf));

        output.WriteLine($"Composed: source={composed.Source}, reason={composed.Reason}");

        var renderer = sp.GetRequiredService<FreeDigestEmailRenderer>();
        var weekEnding = DateOnly.ParseExact(resolved.WeekEnding, "yyyy-MM-dd");
        var settings = sp.GetRequiredService<FreeDigestSettings>();

        // THIS is the exact HTML the real GENERATE phase (PersistDigestArtifactActivity) would
        // encrypt and write to blob - unsubscribe link deliberately left as the sentinel, since
        // that only gets filled in per-recipient at SEND time.
        var html = await renderer.RenderHtmlForArtifactAsync(
            composed.Body, resolved.TenantName, weekEnding.ToDateTime(TimeOnly.MinValue), settings.UpgradeUrl);

        await File.WriteAllTextAsync(OutputPath, html);
        output.WriteLine($"Sample artifact written to {OutputPath}");
    }
}

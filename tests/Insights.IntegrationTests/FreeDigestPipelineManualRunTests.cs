using Insights.Agents;
using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - this spends real LLM tokens against live UAT
/// data, and sends a real email when ELASTICEMAIL_API_KEY + EMAIL_FROM_ADDRESS are both set
/// (dry-run otherwise - generates and prints the email, skips the actual send). Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~FreeDigestPipelineManualRunTests
/// Requires (none read from any committed file, no default - unlike the other integration
/// tests in this project, the UAT connection string carries a live sa password and must
/// never be hardcoded here even as a fallback):
///   ConnectionStrings__RegTrack, AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT,
///   AZURE_OPENAI_API_KEY, TEST_RECIPIENT_EMAIL
/// Optional - enables a real send instead of dry-run:
///   ELASTICEMAIL_API_KEY, EMAIL_FROM_ADDRESS, EMAIL_FROM_NAME
/// </summary>
public sealed class FreeDigestPipelineManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task RunForTenant1403_PrintsBody_SendsIfEmailCredentialsPresent()
    {
        var azureEndpoint = RequireEnv("AZURE_OPENAI_ENDPOINT");
        var azureDeployment = RequireEnv("AZURE_OPENAI_DEPLOYMENT");
        var azureKey = RequireEnv("AZURE_OPENAI_API_KEY");
        var recipient = RequireEnv("TEST_RECIPIENT_EMAIL");

        var elasticKey = Environment.GetEnvironmentVariable("ELASTICEMAIL_API_KEY");
        var fromAddress = Environment.GetEnvironmentVariable("EMAIL_FROM_ADDRESS");
        var fromName = Environment.GetEnvironmentVariable("EMAIL_FROM_NAME") ?? "RegTrack Insights";
        var dryRun = string.IsNullOrEmpty(elasticKey) || string.IsNullOrEmpty(fromAddress);

        using var httpClient = new HttpClient();

        var repository = new SqlFreeDigestRepository(ConnectionString);
        var writer = new FreeDigestWriter(new AzureOpenAiChatClient(httpClient, azureEndpoint, azureDeployment, azureKey), new FilePromptLoader("prompts"));
        var renderer = new FreeDigestEmailRenderer("templates");
        IEmailSender emailSender = dryRun ? new DryRunEmailSender(output) : new ElasticEmailSender(httpClient, elasticKey!);

        var pipeline = new FreeDigestPipeline(
            repository, writer, renderer, emailSender,
            tokenCap: 1500, fromAddress: fromAddress ?? "dry-run@example.com", fromName: fromName,
            upgradeUrl: "https://regtrack.example/upgrade");

        output.WriteLine(dryRun
            ? "DRY RUN - ELASTICEMAIL_API_KEY / EMAIL_FROM_ADDRESS not both set, skipping the real send."
            : "LIVE SEND - Elastic Email credentials present.");

        // Tenant 1403 - test ProductMapping row inserted 2026-08-19 (Product 18 only, no 19).
        // 1490 turned out to be soft-deleted (Customer.IsDeleted=1) so it could never reach
        // PROCEED; 1403 is active with zero pre-existing product mappings. Tenant-wide, no
        // specific recipient's scope, purely to see the pipeline produce real output end to end.
        var result = await pipeline.RunForRecipientAsync(
            customerId: 1403, userId: null, recipientEmail: recipient, recipientName: null,
            tenantName: "Tenant 1403 (UAT)", unsubscribeUrl: "https://regtrack.example/unsubscribe?token=manual-test");

        output.WriteLine($"Sent: {result.Sent}");
        output.WriteLine($"Source: {result.Source}");
        output.WriteLine($"Reason: {result.Reason}");
        output.WriteLine($"ProviderUsed: {result.ProviderUsed}");
        output.WriteLine("--- Body ---");
        output.WriteLine(result.Body ?? "(none - pipeline exited before generating a body)");
        output.WriteLine("--- HTML ---");
        output.WriteLine(result.Html ?? "(none)");

        Assert.True(result.Sent, $"Pipeline did not send - gate reason: {result.Reason}");
    }

    private sealed class DryRunEmailSender(ITestOutputHelper output) : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            output.WriteLine($"[dry run] would send to {message.ToAddress}, subject: {message.Subject}");
            return Task.FromResult(new EmailSendResult("DryRun"));
        }
    }
}
